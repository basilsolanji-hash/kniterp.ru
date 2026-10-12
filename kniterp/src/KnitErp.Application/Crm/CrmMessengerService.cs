using System.Security.Cryptography;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>Реквизиты бота: токен; для WhatsApp — ещё id номера (Phone number ID).</summary>
public sealed record CrmMessengerCredentials(string Token, string? AccountId);

/// <summary>
/// Клиент мессенджера (D670): проверка токена, подписка вебхука, отправка сообщения. Реализации — в Infrastructure (HTTP к Bot API);
/// не настроен адрес API — IsAvailable = false.
/// </summary>
public interface ICrmMessengerClient
{
    CrmMessengerKind Kind { get; }

    bool IsAvailable { get; }

    /// <summary>Имя бота (номер) по реквизитам; null — реквизиты не подошли. Недоступность сервиса — ExternalServiceUnavailableException.</summary>
    Task<string?> AccountNameAsync(CrmMessengerCredentials credentials, CancellationToken ct = default);

    /// <summary>Подписка вебхука. false — подписать автоматически нельзя (WhatsApp: адрес указывается в кабинете Meta).</summary>
    Task<bool> SubscribeAsync(CrmMessengerCredentials credentials, string url, string secret, CancellationToken ct = default);

    Task<bool> SendAsync(CrmMessengerCredentials credentials, string peer, string text, CancellationToken ct = default);
}

/// <summary>Входящее сообщение мессенджера, разобранное из вебхука. Peer — id собеседника для ответа (MAX user_id, Telegram chat_id, WhatsApp номер).</summary>
public sealed record CrmMessengerUpdate(string Peer, string? Name, string? Username, string Text, string? MessageId, string? Phone = null);

/// <summary>Диалог с клиентом в чатах CRM: собеседник, к чему привязан, последнее сообщение, ждёт ли ответа.</summary>
public sealed record CrmChatDto(string Peer, string Channel, string Client, string Href, string LastText, DateTime LastAtUtc, bool AwaitingReply, int Messages);

public sealed record CrmChatMessageDto(long Id, bool Incoming, string Text, DateTime AtUtc, string? Author);

public sealed record CrmMessengerStatusDto(CrmMessengerKind Kind, string Name, bool Available, bool Connected, string? AccountName, string? ConnectedBy,
    DateTime? ConnectedAtUtc, bool AutoSubscribe);

public sealed record CrmMessengersDto(IReadOnlyList<CrmMessengerStatusDto> Channels, string? PublicUrl)
{
    public bool AnyConnected => Channels.Any(c => c.Connected);
}

/// <param name="WebhookUrl">Адрес вебхука с секретом — показывается один раз, при подключении.</param>
/// <param name="VerifyToken">WhatsApp: «Verify token» для кабинета Meta (тот же секрет) — показывается один раз.</param>
public sealed record CrmMessengerConnectResult(CrmMessengerKind Kind, string AccountName, string WebhookUrl, bool Subscribed, string? VerifyToken);

/// <summary>Канал, найденный по секрету вебхука: для проверки подписи и обработки входящего.</summary>
public sealed record CrmMessengerHook(long ChannelId, long OrganizationId, CrmMessengerKind Kind, long UserId, string? AppSecret);

/// <summary>
/// Чаты с клиентами в мессенджерах (D648, D670): подключение ботов MAX, Telegram и WhatsApp (crm.settings), приём входящих вебхуком в ленту
/// контакта, сделки или лида (незнакомый — новый лид, D644), ответ клиенту из чатов и ленты. Входящие обрабатываются с правами того, кто
/// подключил бота: отозвали права или заблокировали — приём останавливается. Токены хранятся зашифрованными, секрет вебхука — только хэшем.
/// </summary>
public sealed class CrmMessengerService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock,
    IEnumerable<ICrmMessengerClient> clients, IEmailSender mail)
{
    public static readonly CrmMessengerKind[] Kinds = [CrmMessengerKind.Telegram, CrmMessengerKind.WhatsApp, CrmMessengerKind.Max];

    public async Task<CrmMessengersDto> StatusAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var rows = await (from c in db.CrmMessengerChannels.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId)
                          join u in db.Users.AsNoTracking() on c.ConnectedByUserId equals u.Id
                          select new { c.Kind, Connected = c.Token != null && c.SecretHash != null, c.AccountName, u.DisplayName, c.ConnectedAtUtc }).ToListAsync(ct);
        return new CrmMessengersDto(Kinds.Select(k =>
        {
            var r = rows.SingleOrDefault(x => x.Kind == k);
            return new CrmMessengerStatusDto(k, CrmMessengerChannel.KindName(k), Client(k)?.IsAvailable == true, r?.Connected == true,
                r?.Connected == true ? r.AccountName : null, r?.Connected == true ? r.DisplayName : null, r?.Connected == true ? r.ConnectedAtUtc : null,
                k != CrmMessengerKind.WhatsApp);
        }).ToList(), mail.PublicUrl);
    }

    public async Task<CrmMessengerConnectResult> ConnectAsync(CrmMessengerKind kind, string? token, string? accountId = null, string? appSecret = null,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmSettings, ct);
        await guard.DemandAsync(Permissions.CrmEdit, ct);
        var name = CrmMessengerChannel.KindName(kind);
        var t = token?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            throw new BusinessRuleException("crm.messenger.token", $"Укажите токен бота {name}.");
        }

        if (kind == CrmMessengerKind.WhatsApp && (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(appSecret)))
        {
            throw new BusinessRuleException("crm.messenger.whatsapp", "Для WhatsApp укажите токен, Phone number ID и секрет приложения из кабинета Meta.");
        }

        var client = Client(kind);
        if (client is not { IsAvailable: true })
        {
            throw new BusinessRuleException("crm.messenger.unavailable", $"Подключение к {name} на сервере не настроено — обратитесь к администратору.");
        }

        var credentials = new CrmMessengerCredentials(t, accountId?.Trim());
        string? account;
        try
        {
            account = await client.AccountNameAsync(credentials, ct);
        }
        catch (KnitErp.Application.Workspace.ExternalServiceUnavailableException)
        {
            throw new BusinessRuleException("crm.messenger.down", $"{name} сейчас недоступен — попробуйте позже.");
        }

        if (account is null)
        {
            throw new BusinessRuleException("crm.messenger.bad_token", $"Токен не подошёл: проверьте его в настройках бота {name}.");
        }

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var channel = await db.CrmMessengerChannels.SingleOrDefaultAsync(c => c.OrganizationId == ctx.OrganizationId && c.Kind == kind, ct);
        var before = channel is { IsConnected: true } ? $"{name} подключён: {channel.AccountName}" : null;
        if (channel is null)
        {
            channel = CrmMessengerChannel.Create(ctx.OrganizationId, kind);
            channel.Connect(t, credentials.AccountId, appSecret?.Trim(), account, Hash(secret), ctx.UserId, clock.UtcNow);
            db.CrmMessengerChannels.Add(channel); // добавляется только после проверки реквизитов
        }
        else
        {
            CrmMessengerChannel.Create(ctx.OrganizationId, kind).Connect(t, credentials.AccountId, appSecret?.Trim(), account, Hash(secret), ctx.UserId, clock.UtcNow);
            channel.Connect(t, credentials.AccountId, appSecret?.Trim(), account, Hash(secret), ctx.UserId, clock.UtcNow);
        }
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmSettingsChanged, nameof(CrmMessengerChannel),
            ctx.OrganizationId.ToString(), before, $"{name} подключён: {account}", "Мессенджеры", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
        var url = $"{mail.PublicUrl}/hooks/crm/{CrmMessengerChannel.Key(kind)}/{secret}";
        var subscribed = mail.PublicUrl is not null && kind != CrmMessengerKind.WhatsApp && await client.SubscribeAsync(credentials, url, secret, ct);
        return new CrmMessengerConnectResult(kind, account, url, subscribed, kind == CrmMessengerKind.WhatsApp ? secret : null);
    }

    public async Task DisconnectAsync(CrmMessengerKind kind, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmSettings, ct);
        var channel = await db.CrmMessengerChannels.SingleOrDefaultAsync(c => c.OrganizationId == ctx.OrganizationId && c.Kind == kind, ct);
        if (channel is not { IsConnected: true })
        {
            return;
        }

        var name = CrmMessengerChannel.KindName(kind);
        channel.Disconnect(clock.UtcNow);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmSettingsChanged, nameof(CrmMessengerChannel),
            ctx.OrganizationId.ToString(), $"{name} подключён: {channel.AccountName}", $"{name} отключён", "Мессенджеры", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    private ICrmMessengerClient? Client(CrmMessengerKind kind) => clients.FirstOrDefault(c => c.Kind == kind);

    private static byte[] Hash(string secret) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));

    /// <summary>Мессенджер по префиксу собеседника («Telegram:123»).</summary>
    public static CrmMessengerKind? KindOf(string? peer)
    {
        var prefix = peer?.Split(':')[0];
        return Kinds.Cast<CrmMessengerKind?>().FirstOrDefault(k => CrmMessengerChannel.KindName(k!.Value) == prefix);
    }

    /// <summary>
    /// Чаты с клиентами (D649): диалоги по собеседникам мессенджеров на видимых пользователю лидах, сделках и контактах — новые сверху;
    /// «ждёт ответа» — последнее сообщение входящее.
    /// </summary>
    public async Task<IReadOnlyList<CrmChatDto>> ChatsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var rows = await Visible(ctx).Where(a => a.Peer != null)
            .OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Take(2000)
            .Select(a => new { a.Id, a.Peer, a.Target, a.TargetId, a.Text, a.CreatedAtUtc, Incoming = a.ExternalRef != null, a.ExternalRef }).ToListAsync(ct);
        var chats = rows.GroupBy(r => r.Peer!).Select(g => g.First()).Take(200).ToList();
        var titles = new Dictionary<(CrmTarget, long), (string, string)>();
        foreach (var c in chats.Select(c => (c.Target, c.TargetId)).Distinct())
        {
            titles[c] = c.Target switch
            {
                CrmTarget.Lead => (await db.CrmLeads.AsNoTracking().Where(l => l.Id == c.TargetId).Select(l => l.ContactName ?? l.Title).SingleAsync(ct), $"crm/leads/{c.TargetId}"),
                CrmTarget.Deal => (await db.CrmDeals.AsNoTracking().Where(d => d.Id == c.TargetId).Select(d => d.Number + " " + d.Title).SingleAsync(ct), $"crm/deals/{c.TargetId}"),
                _ => (await db.CrmContacts.AsNoTracking().Where(x => x.Id == c.TargetId).Select(x => x.FullName).SingleAsync(ct), $"crm/contacts/{c.TargetId}"),
            };
        }

        return chats.Select(c => new CrmChatDto(c.Peer!, c.Peer!.Split(':')[0], titles[(c.Target, c.TargetId)].Item1, titles[(c.Target, c.TargetId)].Item2,
            c.Text ?? "", c.CreatedAtUtc, c.Incoming, rows.Count(r => r.Peer == c.Peer))).ToList();
    }

    /// <summary>Переписка с собеседником — по времени, последние 200 сообщений.</summary>
    public async Task<IReadOnlyList<CrmChatMessageDto>> MessagesAsync(string peer, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var list = await (from a in Visible(ctx).Where(a => a.Peer == peer)
                          join u in db.Users.AsNoTracking() on a.CreatedByUserId equals u.Id
                          orderby a.CreatedAtUtc descending, a.Id descending
                          select new CrmChatMessageDto(a.Id, a.ExternalRef != null, a.Text ?? "", a.CreatedAtUtc, a.ExternalRef != null ? null : u.DisplayName))
            .Take(200).ToListAsync(ct);
        list.Reverse();
        return list;
    }

    /// <summary>Ответ в чате: по последнему входящему сообщению собеседника.</summary>
    public async Task ReplyToPeerAsync(string peer, string? text, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var last = await Visible(ctx).Where(a => a.Peer == peer && a.ExternalRef != null).OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Select(a => (long?)a.Id)
                       .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Чат");
        await ReplyAsync(last, text, ct);
    }

    private IQueryable<CrmActivity> Visible(AccessContext ctx)
    {
        var q = db.CrmActivities.AsNoTracking().Where(a => a.OrganizationId == ctx.OrganizationId && a.Source == CrmActivitySource.Messenger);
        if (CrmAccess.SeesAll(ctx))
        {
            return q;
        }

        var leads = CrmAccess.Leads(db, ctx).Select(l => l.Id);
        var deals = CrmAccess.Deals(db, ctx).Select(d => d.Id);
        var contacts = CrmAccess.Contacts(db, ctx).Select(c => c.Id);
        return q.Where(a => a.AssigneeUserId == ctx.UserId || (a.Target == CrmTarget.Lead && leads.Contains(a.TargetId))
                            || (a.Target == CrmTarget.Deal && deals.Contains(a.TargetId)) || (a.Target == CrmTarget.Contact && contacts.Contains(a.TargetId)));
    }

    /// <summary>Ответ клиенту в мессенджер из ленты (на входящее сообщение): уходит через бота, сохраняется исходящим сообщением в той же ленте.</summary>
    public async Task ReplyAsync(long activityId, string? text, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var body = CrmText.Require(text, CrmActivity.TextMaxLength, "Текст");
        var source = await db.CrmActivities.AsNoTracking().SingleOrDefaultAsync(a => a.Id == activityId && a.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Активность");
        var visible = source.Target switch
        {
            CrmTarget.Lead => await CrmAccess.Leads(db, ctx).AnyAsync(l => l.Id == source.TargetId, ct),
            CrmTarget.Deal => await CrmAccess.Deals(db, ctx).AnyAsync(d => d.Id == source.TargetId, ct),
            _ => await CrmAccess.Contacts(db, ctx).AnyAsync(c => c.Id == source.TargetId, ct),
        };
        if (!visible || source.Peer is null || source.Source != CrmActivitySource.Messenger || KindOf(source.Peer) is not { } kind)
        {
            throw new NotFoundException("Активность");
        }

        var name = CrmMessengerChannel.KindName(kind);
        var channel = await db.CrmMessengerChannels.AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == ctx.OrganizationId && c.Kind == kind, ct);
        if (channel is not { IsConnected: true } || Client(kind) is not { IsAvailable: true } client)
        {
            throw new BusinessRuleException("crm.messenger.not_connected", $"{name} не подключён — подключите бота в «Настройках CRM».");
        }

        if (!await client.SendAsync(new CrmMessengerCredentials(channel.Token!, channel.AccountId), source.Peer[(source.Peer.IndexOf(':') + 1)..], body, ct))
        {
            throw new BusinessRuleException("crm.messenger.failed", kind == CrmMessengerKind.WhatsApp
                ? "Сообщение не отправлено: WhatsApp принимает ответ в течение 24 часов после последнего сообщения клиента. Позже — только шаблоном из кабинета Meta."
                : $"Сообщение не отправлено: {name} не принял его. Попробуйте позже.");
        }

        var reply = CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.Message, source.Target, source.TargetId, $"Ответ в {name}", body, null, ctx.UserId, ctx.UserId,
            clock.UtcNow, done: true, result: "отправлено", source: CrmActivitySource.Messenger);
        reply.SetPeer(source.Peer);
        db.CrmActivities.Add(reply);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmActivityCreated, nameof(CrmActivity),
            activityId.ToString(), null, $"ответ в {name}", null, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Подключённый канал по мессенджеру и секрету из адреса вебхука (без входа); неизвестный — null.</summary>
    public static async Task<CrmMessengerHook?> FindHookAsync(IKnitErpDbContext db, CrmMessengerKind kind, string? secret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 100)
        {
            return null;
        }

        var hash = Hash(secret);
        return await db.CrmMessengerChannels.AsNoTracking().Where(c => c.SecretHash == hash && c.Kind == kind && c.Token != null)
            .Select(c => new CrmMessengerHook(c.Id, c.OrganizationId, c.Kind, c.ConnectedByUserId, c.AppSecret)).SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// Входящее из вебхука (без входа, канал уже найден по секрету и подпись проверена): обработка — с правами подключившего бота.
    /// Права отозваны — false (вебхук отвечает 404).
    /// </summary>
    public static async Task<bool> InboundAsync(IKnitErpDbContext db, IClock clock, CrmMessengerHook hook, CrmMessengerUpdate update, CancellationToken ct = default)
    {
        var user = new IntegrationUser(hook.UserId, hook.OrganizationId);
        var integrationGuard = new AccessGuard(db, user, clock);
        try
        {
            await new CrmIntakeService(db, integrationGuard, user, clock).RegisterMessageAsync(
                new CrmMessageEvent(CrmMessengerChannel.Key(hook.Kind), update.Peer, update.Text, update.Phone, update.Name ?? update.Username, update.MessageId), ct);
        }
        catch (AccessDeniedException)
        {
            return false;
        }

        return true;
    }

    /// <summary>Пользователь, от имени которого работает интеграция (подключивший бота).</summary>
    private sealed class IntegrationUser(long userId, long organizationId) : ICurrentUser
    {
        public long? UserId => userId;
        public long? OrganizationId => organizationId;
        public string? CorrelationId => "messenger-webhook";
    }
}
