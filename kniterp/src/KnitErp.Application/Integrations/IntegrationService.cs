using System.Net;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Integrations;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Integrations;

public sealed record ApiTokenDto(
    long Id, string Name, string Prefix, IReadOnlyList<string> Permissions, string CreatedBy, DateTime CreatedAtUtc, DateTime? ExpiresAtUtc,
    DateTime? LastUsedAtUtc, DateTime? RevokedAtUtc, bool IsActive, byte[] RowVersion);

/// <summary>Выпущенный токен: Secret показывается один раз и больше нигде не хранится.</summary>
public sealed record IssuedSecretDto(long Id, string Secret);

public sealed record WebhookDto(
    long Id, string Url, IReadOnlyList<string> Events, bool IsActive, string CreatedBy, DateTime CreatedAtUtc, int Pending, int Failed,
    DateTime? LastDeliveredAtUtc, byte[] RowVersion);

public sealed record WebhookDeliveryDto(
    long Id, long SubscriptionId, Guid EventId, string EventType, long Sequence, WebhookDeliveryStatus Status, int Attempts, int? LastStatusCode,
    string? LastError, DateTime CreatedAtUtc, DateTime? DeliveredAtUtc, DateTime NextAttemptAtUtc);

/// <summary>Права, которые пользователь может выдать токену: его собственные, кроме запрещённых для API.</summary>
public sealed record IntegrationOptionsDto(IReadOnlyList<(string Code, string Title)> Permissions, IReadOnlyList<(string Event, string Title)> Events);

/// <summary>
/// Интеграции (D88): токены собственного API и подписки на события (вебхуки). Управляет тот, кто вправе менять реквизиты
/// организации (Владелец, Администратор). Права токена — только из своих и не из запрещённых для API; адрес вебхука — только
/// HTTPS и не во внутреннюю сеть (защита от SSRF; при отправке адрес проверяется ещё раз — после разрешения имени).
/// </summary>
/// D123: выпуск и отзыв токена API — только после повторного подтверждения паролем (StepUpGuard).
public sealed class IntegrationService(
    IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IntegrationSettings settings, KnitErp.Application.Authentication.StepUpGuard stepUp)
{
    public const int MaxTokens = 50;
    public const int MaxWebhooks = 20;

    public async Task<IntegrationOptionsDto> OptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var codes = ctx.Permissions.GrantedCodes.Where(c => !ApiToken.Forbidden.Contains(c)).OrderBy(c => c, StringComparer.Ordinal)
            .Select(c => (c, Permissions.Describe(c))).ToList();
        return new IntegrationOptionsDto(codes, WebhookEvents.All);
    }

    public async Task<IReadOnlyList<ApiTokenDto>> ListTokensAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var now = clock.UtcNow;
        var rows = await (from t in db.ApiTokens.AsNoTracking()
                          where t.OrganizationId == ctx.OrganizationId
                          join u in db.Users.AsNoTracking() on t.CreatedByUserId equals u.Id
                          orderby t.RevokedAtUtc != null, t.Id descending
                          select new { t, u.DisplayName }).ToListAsync(ct);
        return rows.Select(r => new ApiTokenDto(r.t.Id, r.t.Name, r.t.Prefix, r.t.PermissionList, r.DisplayName, r.t.CreatedAtUtc, r.t.ExpiresAtUtc,
            r.t.LastUsedAtUtc, r.t.RevokedAtUtc, r.t.IsActive(now), r.t.RowVersion)).ToList();
    }

    /// <summary>Выпуск токена: права — подмножество своих (без запрещённых для API), срок — до года; секрет — в ответе один раз.</summary>
    public async Task<IssuedSecretDto> IssueTokenAsync(string? name, IReadOnlyCollection<string> permissions, int? days, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        if (currentUser.ApiScope is not null)
        {
            throw new BusinessRuleException("api.token.via_api", "Токен выпускается только в интерфейсе, не через API.");
        }

        if (permissions.FirstOrDefault(p => !ctx.Permissions.Has(p)) is { } over)
        {
            throw new BusinessRuleException("api.token.over_rights", $"Нельзя выдать токену право, которого нет у вас: «{Permissions.Describe(over)}».");
        }

        if (await db.ApiTokens.CountAsync(t => t.OrganizationId == ctx.OrganizationId && t.RevokedAtUtc == null, ct) >= MaxTokens)
        {
            throw new BusinessRuleException("api.token.limit", $"Действующих токенов — не больше {MaxTokens}. Отзовите ненужные.");
        }

        var (token, secret) = ApiToken.Issue(ctx.OrganizationId, name, permissions, ctx.UserId, clock.UtcNow, days);
        stepUp.Demand(ctx); // D123
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.ApiTokenIssued, nameof(ApiToken), token.Id, null, $"{token.Name} ({token.Prefix}…)",
            $"Права: {token.Permissions}; срок: {(token.ExpiresAtUtc is { } e ? e.ToString("dd.MM.yyyy") : "бессрочно")}");
        await db.SaveChangesAsync(ct);
        return new IssuedSecretDto(token.Id, secret);
    }

    /// <summary>
    /// Отзыв действует сразу. Версия строки не сверяется: токен в работе раз в минуту отмечает использование, и отзыв не должен
    /// из-за этого получать конфликт.
    /// </summary>
    public async Task RevokeTokenAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var token = await db.ApiTokens.SingleOrDefaultAsync(t => t.Id == id && t.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Токен");
        stepUp.Demand(ctx); // D123
        token.Revoke(ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.ApiTokenRevoked, nameof(ApiToken), id, "действует", "отозван", $"{token.Name} ({token.Prefix}…)");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task<IReadOnlyList<WebhookDto>> ListWebhooksAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var rows = await (from w in db.WebhookSubscriptions.AsNoTracking()
                          where w.OrganizationId == ctx.OrganizationId
                          join u in db.Users.AsNoTracking() on w.CreatedByUserId equals u.Id
                          orderby w.Id descending
                          select new
                          {
                              w.Id, w.Url, w.Events, w.IsActive, u.DisplayName, w.CreatedAtUtc, w.RowVersion,
                              Pending = db.WebhookDeliveries.Count(d => d.SubscriptionId == w.Id && d.Status == WebhookDeliveryStatus.Pending),
                              Failed = db.WebhookDeliveries.Count(d => d.SubscriptionId == w.Id && d.Status == WebhookDeliveryStatus.Failed),
                              Last = db.WebhookDeliveries.Where(d => d.SubscriptionId == w.Id).Max(d => d.DeliveredAtUtc),
                          }).ToListAsync(ct);
        return rows.Select(r => new WebhookDto(r.Id, r.Url, r.Events.Split(',', StringSplitOptions.RemoveEmptyEntries), r.IsActive, r.DisplayName,
            r.CreatedAtUtc, r.Pending, r.Failed, r.Last, r.RowVersion)).ToList();
    }

    /// <summary>Подписка: события после текущей позиции журнала (прошлое не присылается); секрет подписи — в ответе один раз.</summary>
    public async Task<IssuedSecretDto> CreateWebhookAsync(string? url, IReadOnlyCollection<string> events, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var checkedUrl = ValidateUrl(url, settings.AllowPrivateNetworks);
        if (await db.WebhookSubscriptions.CountAsync(w => w.OrganizationId == ctx.OrganizationId, ct) >= MaxWebhooks)
        {
            throw new BusinessRuleException("webhook.limit", $"Подписок — не больше {MaxWebhooks}.");
        }

        var cursor = await db.AuditEntries.MaxAsync(a => (long?)a.Id, ct) ?? 0;
        var (subscription, secret) = WebhookSubscription.Create(ctx.OrganizationId, checkedUrl, events, cursor, ctx.UserId, clock.UtcNow);
        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.WebhookCreated, nameof(WebhookSubscription), subscription.Id, null, subscription.Url, subscription.Events);
        await db.SaveChangesAsync(ct);
        return new IssuedSecretDto(subscription.Id, secret);
    }

    public async Task SetWebhookActiveAsync(long id, bool active, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var w = await db.WebhookSubscriptions.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Подписка");
        w.EnsureVersion(w.RowVersion, rowVersion);
        w.SetActive(active, await db.AuditEntries.MaxAsync(a => (long?)a.Id, ct) ?? 0);
        Audit(ctx, AuditActions.WebhookChanged, nameof(WebhookSubscription), id, null, active ? "включена" : "выключена", w.Url);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Тестовое событие «ping» — проверить адрес и подпись на стороне получателя.</summary>
    public async Task SendTestAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var w = await db.WebhookSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Подписка");
        var eventId = Guid.NewGuid();
        var now = clock.UtcNow;
        db.WebhookDeliveries.Add(WebhookDelivery.Create(ctx.OrganizationId, w.Id, eventId, WebhookEvents.Ping, 0,
            WebhookDispatcher.Payload(eventId, WebhookEvents.Ping, 0, now, null, null, null), now));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WebhookDeliveryDto>> DeliveriesAsync(long? subscriptionId = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        return await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && (subscriptionId == null || d.SubscriptionId == subscriptionId))
            .OrderByDescending(d => d.Id).Take(100)
            .Select(d => new WebhookDeliveryDto(d.Id, d.SubscriptionId, d.EventId, d.EventType, d.Sequence, d.Status, d.Attempts, d.LastStatusCode,
                d.LastError, d.CreatedAtUtc, d.DeliveredAtUtc, d.NextAttemptAtUtc))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Адрес вебхука: абсолютный HTTPS, без логина и пароля в адресе, не localhost и не IP-адрес внутренней сети.
    /// allowPrivate — только для разработки и тестов.
    /// </summary>
    public static string ValidateUrl(string? url, bool allowPrivate)
    {
        var text = url?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > WebhookSubscription.UrlMaxLength || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw new BusinessRuleException("webhook.url", "Укажите полный адрес, например https://example.ru/knit-erp/events.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new BusinessRuleException("webhook.url_credentials", "Логин и пароль в адресе не допускаются — проверяйте подпись запроса.");
        }

        if (allowPrivate)
        {
            return uri.AbsoluteUri;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new BusinessRuleException("webhook.url_https", "Адрес — только HTTPS.");
        }

        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                           || (IPAddress.TryParse(uri.Host, out var ip) && NetworkGuard.IsPrivate(ip)))
        {
            throw new BusinessRuleException("webhook.url_private", "Адрес во внутренней сети не допускается.");
        }

        return uri.AbsoluteUri;
    }

    private void Audit(AccessContext ctx, string action, string entity, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(), before, after, reason,
            currentUser.CorrelationId));
}

/// <summary>Настройки интеграций: AllowPrivateNetworks — отправка во внутреннюю сеть и по HTTP (только разработка и тесты).</summary>
public sealed record IntegrationSettings(bool AllowPrivateNetworks = false);

/// <summary>Адреса, куда вебхуки не отправляются: петля, частные сети, link-local, CGNAT, служебные и многоадресные.</summary>
public static class NetworkGuard
{
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
               || b[0] >= 224;
    }
}
