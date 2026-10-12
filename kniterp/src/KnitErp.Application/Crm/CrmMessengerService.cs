using System.Security.Cryptography;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>
/// Клиент мессенджера MAX (D648): проверка токена, подписка вебхука, отправка сообщения. Реализация — в Infrastructure (HTTP к MAX Bot API);
/// не настроен адрес API — IsAvailable = false.
/// </summary>
public interface ICrmMaxClient
{
    bool IsAvailable { get; }

    /// <summary>Имя бота по токену; null — токен не подошёл. Недоступность сервиса — ExternalServiceUnavailableException.</summary>
    Task<string?> BotNameAsync(string token, CancellationToken ct = default);

    Task<bool> SubscribeAsync(string token, string url, string secret, CancellationToken ct = default);

    Task<bool> SendAsync(string token, string userId, string text, CancellationToken ct = default);
}

/// <summary>Входящее сообщение MAX, разобранное из вебхука (update_type = message_created).</summary>
public sealed record CrmMaxUpdate(string UserId, string? Name, string? Username, string Text, string? MessageId);

/// <summary>Диалог с клиентом в чатах CRM: собеседник, к чему привязан, последнее сообщение, ждёт ли ответа.</summary>
public sealed record CrmChatDto(string Peer, string Channel, string Client, string Href, string LastText, DateTime LastAtUtc, bool AwaitingReply, int Messages);

public sealed record CrmChatMessageDto(long Id, bool Incoming, string Text, DateTime AtUtc, string? Author);

public sealed record CrmMaxStatusDto(bool Available, bool Connected, string? ConnectedBy, DateTime? ConnectedAtUtc, string? PublicUrl);

/// <param name="WebhookUrl">Адрес вебхука с секретом — показывается один раз, при подключении.</param>
public sealed record CrmMaxConnectResult(string BotName, string WebhookUrl, bool Subscribed);

/// <summary>
/// Мессенджер MAX в CRM (D648, запрос владельца 12.10.2026): подключение бота по токену (crm.settings), приём входящих сообщений вебхуком в
/// ленту контакта, сделки или лида (незнакомый — новый лид, D644), ответ клиенту из ленты. Входящие обрабатываются с правами того, кто подключил
/// бота: отозвали права или заблокировали — приём останавливается. Токен хранится зашифрованным, секрет вебхука — только хэшем.
/// </summary>
public sealed class CrmMessengerService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, ICrmMaxClient max, IEmailSender mail)
{
    public const string Channel = "MAX";

    public async Task<CrmMaxStatusDto> StatusAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var s = await db.CrmSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        var by = s?.MaxConnectedByUserId is { } uid ? await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.DisplayName).SingleOrDefaultAsync(ct) : null;
        return new CrmMaxStatusDto(max.IsAvailable, s?.MaxConnected == true, by, s?.MaxConnectedAtUtc, mail.PublicUrl);
    }

    public async Task<CrmMaxConnectResult> ConnectAsync(string? token, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmSettings, ct);
        await guard.DemandAsync(Permissions.CrmEdit, ct);
        var t = token?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            throw new BusinessRuleException("crm.max.token", "Укажите токен бота MAX.");
        }

        if (!max.IsAvailable)
        {
            throw new BusinessRuleException("crm.max.unavailable", "Подключение к MAX на сервере не настроено — обратитесь к администратору.");
        }

        string? name;
        try
        {
            name = await max.BotNameAsync(t, ct);
        }
        catch (KnitErp.Application.Workspace.ExternalServiceUnavailableException)
        {
            throw new BusinessRuleException("crm.max.down", "MAX сейчас недоступен — попробуйте позже.");
        }

        if (name is null)
        {
            throw new BusinessRuleException("crm.max.bad_token", "Токен не подошёл: проверьте его в настройках бота MAX.");
        }

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var s = await db.CrmSettings.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        if (s is null)
        {
            s = CrmSettings.Default(ctx.OrganizationId);
            db.CrmSettings.Add(s);
        }

        s.ConnectMax(t, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)), ctx.UserId, clock.UtcNow);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmSettingsChanged, nameof(CrmSettings),
            ctx.OrganizationId.ToString(), null, $"MAX подключён: бот {name}", "Мессенджеры", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
        var url = $"{mail.PublicUrl}/hooks/crm/max/{secret}";
        var subscribed = mail.PublicUrl is not null && await max.SubscribeAsync(t, url, secret, ct);
        return new CrmMaxConnectResult(name, url, subscribed);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmSettings, ct);
        var s = await db.CrmSettings.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        if (s is null || !s.MaxConnected)
        {
            return;
        }

        s.DisconnectMax();
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmSettingsChanged, nameof(CrmSettings),
            ctx.OrganizationId.ToString(), "MAX подключён", "MAX отключён", "Мессенджеры", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
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

        return chats.Select(c => new CrmChatDto(c.Peer!, ChannelOf(c.ExternalRef), titles[(c.Target, c.TargetId)].Item1, titles[(c.Target, c.TargetId)].Item2,
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

    private static string ChannelOf(string? externalRef) => externalRef?.Split(':')[0] ?? Channel;

    /// <summary>Ответ клиенту в MAX из ленты (на входящее сообщение): уходит через бота, сохраняется исходящим сообщением в той же ленте.</summary>
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
        if (!visible || source.Peer is null || source.Source != CrmActivitySource.Messenger || source.ExternalRef?.StartsWith(Channel + ":", StringComparison.Ordinal) != true)
        {
            throw new NotFoundException("Активность");
        }

        var settings = await db.CrmSettings.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        if (settings is not { MaxConnected: true })
        {
            throw new BusinessRuleException("crm.max.not_connected", "MAX не подключён — подключите бота в «Настройках CRM».");
        }

        if (!await max.SendAsync(settings.MaxToken!, source.Peer, body, ct))
        {
            throw new BusinessRuleException("crm.max.failed", "Сообщение не отправлено: MAX не принял его. Попробуйте позже.");
        }

        var reply = CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.Message, source.Target, source.TargetId, $"Ответ в {Channel}", body, null, ctx.UserId, ctx.UserId,
            clock.UtcNow, done: true, result: "отправлено", source: CrmActivitySource.Messenger);
        reply.SetPeer(source.Peer);
        db.CrmActivities.Add(reply);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmActivityCreated, nameof(CrmActivity),
            activityId.ToString(), null, $"ответ в {Channel}", null, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Входящее из вебхука MAX (без входа): организация — по хэшу секрета из адреса; обработка — с правами подключившего бота. Неизвестный
    /// секрет или права отозваны — false (вебхук отвечает 404).
    /// </summary>
    public static async Task<bool> InboundAsync(IKnitErpDbContext db, IClock clock, string secret, CrmMaxUpdate update, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 100)
        {
            return false;
        }

        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
        var s = await db.CrmSettings.AsNoTracking().SingleOrDefaultAsync(x => x.MaxSecretHash == hash, ct);
        if (s is not { MaxConnected: true, MaxConnectedByUserId: { } userId })
        {
            return false;
        }

        var user = new IntegrationUser(userId, s.OrganizationId);
        var integrationGuard = new AccessGuard(db, user, clock);
        try
        {
            await new CrmIntakeService(db, integrationGuard, user, clock).RegisterMessageAsync(
                new CrmMessageEvent("max", update.UserId, update.Text, null, update.Name ?? update.Username, update.MessageId), ct);
        }
        catch (AccessDeniedException)
        {
            return false;
        }

        return true;
    }

    public static async Task<bool> KnownSecretAsync(IKnitErpDbContext db, string secret, CancellationToken ct = default)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret ?? ""));
        return await db.CrmSettings.AsNoTracking().AnyAsync(x => x.MaxSecretHash == hash, ct);
    }

    /// <summary>Пользователь, от имени которого работает интеграция (подключивший бота).</summary>
    private sealed class IntegrationUser(long userId, long organizationId) : ICurrentUser
    {
        public long? UserId => userId;
        public long? OrganizationId => organizationId;
        public string? CorrelationId => "max-webhook";
    }
}
