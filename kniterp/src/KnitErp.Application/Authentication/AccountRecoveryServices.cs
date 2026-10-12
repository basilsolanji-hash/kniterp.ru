using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Authentication;

/// <summary>
/// «Забыли пароль» (D81). По email — ссылка на 1 час на почту (если на сервере настроена почта); ответ всегда одинаковый,
/// чтобы по нему нельзя было узнать, есть ли такой пользователь. Ссылка меняет только пароль: 2FA остаётся, все сессии
/// и доверенные устройства закрываются, токены API пользователя отзываются (D115). Без почты пароль меняет администратор —
/// ссылкой из «Пользователей».
/// </summary>
public sealed class PasswordResetService(
    IKnitErpDbContext db, IPasswordHasher<UserAccount> hasher, IEmailSender email, ICurrentUser currentUser, IClock clock)
{
    public const string ResetPath = "account/reset-password";

    /// <summary>Можно ли восстановить пароль самому (настроена почта и адрес системы).</summary>
    public bool SelfServiceAvailable => email.IsConfigured;

    /// <summary>
    /// Запрос ссылки. Неизвестный email, неактивный пользователь, недавно отправленная ссылка — тот же результат без письма.
    /// Письмо уходит в фоне: время ответа не выдаёт, существует ли адрес.
    /// </summary>
    public async Task RequestAsync(string? address, CancellationToken ct = default)
    {
        if (!email.IsConfigured)
        {
            throw new BusinessRuleException("auth.reset.unavailable", "Восстановление по почте не настроено. Обратитесь к администратору организации.");
        }

        var now = clock.UtcNow;
        var normalized = (address ?? string.Empty).Trim().ToUpperInvariant();
        var user = normalized.Length is 0 or > UserAccount.EmailMaxLength
            ? null
            : await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (user is null || user.Status != UserStatus.Active || !user.HasPassword)
        {
            db.AuditEntries.Add(AuditEntry.Create(now, null, user?.Id, AuditActions.PasswordResetRequested, nameof(UserAccount), user?.Id.ToString(),
                reason: WithAddress(user is null ? "неизвестный email" : "учётная запись не активна, письмо не отправлено"),
                correlationId: currentUser.CorrelationId));
            await db.SaveChangesAsync(ct);
            return;
        }

        if (user.ResetRecentlyIssued(now))
        {
            await AuditAsync(user, AuditActions.PasswordResetRequested, "повтор раньше чем через 5 минут, письмо не отправлено", ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        var token = user.IssuePasswordResetToken(now, SignInPolicy.PasswordResetLifetime);
        await AuditAsync(user, AuditActions.PasswordResetRequested, "ссылка отправлена на почту, действует 1 час", ct);
        await db.SaveChangesAsync(ct);

        var (to, subject, text) = (user.Email, "knitERP: смена пароля", Letter(user.DisplayName, user.Email, $"{email.PublicUrl}/{ResetPath}?token={token}"));
        _ = Task.Run(() => email.SendAsync(to, subject, text), CancellationToken.None);
    }

    /// <summary>Email по ссылке или null, если ссылка недействительна или истекла.</summary>
    public async Task<string?> DescribeAsync(string? token, CancellationToken ct = default)
    {
        var user = await FindAsync(token, ct);
        return user?.SetupTokenExpiresAtUtc > clock.UtcNow ? user.Email : null;
    }

    /// <summary>Новый пароль по ссылке: прежний перестаёт действовать, все сессии и доверенные устройства закрываются.</summary>
    public async Task ResetAsync(string? token, string? password, string? confirmation, CancellationToken ct = default)
    {
        var user = await FindAsync(token, ct)
                   ?? throw new BusinessRuleException("auth.reset.invalid", "Ссылка недействительна. Запросите новую.");
        if (user.SetupTokenExpiresAtUtc is not { } expires || expires <= clock.UtcNow)
        {
            throw new BusinessRuleException("auth.reset.expired", "Срок ссылки истёк. Запросите новую.");
        }

        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            throw new BusinessRuleException("auth.password.mismatch", "Пароли не совпадают.");
        }

        SignInPolicy.EnsurePasswordAcceptable(password, user.Email);
        user.CompleteSetup(token!, hasher.HashPassword(user, password!), clock.UtcNow);
        foreach (var device in await db.TrustedDevices.Where(d => d.UserId == user.Id && d.RevokedAtUtc == null).ToListAsync(ct))
        {
            device.Revoke(clock.UtcNow);
        }

        await SecurityEvents.RevokeSessionsAsync(db, user.Id, clock.UtcNow, ct);

        // D115: сброс пароля — признак, что пароль мог попасть к чужому. Токены API, выпущенные этим пользователем, отзываются:
        // иначе выпущенный злоумышленником токен пережил бы смену пароля. Интеграции перевыпускают токен в «Интеграциях».
        var tokens = await SecurityEvents.RevokeApiTokensAsync(db, user.Id, clock.UtcNow, ct);
        await AuditAsync(user, AuditActions.PasswordReset, tokens > 0
            ? $"пароль сменён по ссылке; сессии и доверенные устройства закрыты, отозвано токенов API: {tokens}"
            : "пароль сменён по ссылке; сессии и доверенные устройства закрыты", ct);
        await db.SaveChangesAsync(ct);
        SecurityEvents.Notify(email, user, "Пароль knitERP изменён",
            "Пароль вашей учётной записи изменён по ссылке восстановления. Все сессии закрыты"
            + (tokens > 0 ? $", отозвано токенов API: {tokens} — выпустите новые в «Интеграциях»." : "."));
    }

    internal static string Letter(string name, string address, string link) =>
        $"""
         Здравствуйте, {name}!

         Для учётной записи knitERP {address} запрошена смена пароля.
         Чтобы задать новый пароль, откройте ссылку в течение 1 часа:

         {link}

         Если вы не запрашивали смену пароля, удалите это письмо — пароль останется прежним.
         При входе по-прежнему понадобится код из приложения-аутентификатора, если он подключён.

         Письмо отправлено автоматически, отвечать на него не нужно.
         """;

    private async Task<UserAccount?> FindAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hash = SetupTokens.Hash(token);
        return await db.Users.SingleOrDefaultAsync(u => u.SetupTokenHash == hash && u.Status == UserStatus.Active && u.PasswordHash != null, ct);
    }

    private async Task AuditAsync(UserAccount user, string action, string reason, CancellationToken ct)
    {
        var orgIds = await db.OrganizationMembers.Where(m => m.UserId == user.Id).Select(m => (long?)m.OrganizationId).ToListAsync(ct);
        foreach (var orgId in orgIds.DefaultIfEmpty(null))
        {
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, orgId, user.Id, action, nameof(UserAccount), user.Id.ToString(),
                reason: WithAddress(reason), correlationId: currentUser.CorrelationId));
        }
    }

    private string? WithAddress(string? reason) => currentUser.ClientAddress is { } ip ? $"{reason} · адрес {ip}" : reason;
}

public sealed record TrustedDeviceDto(long Id, string Label, DateTime CreatedAtUtc, DateTime? LastUsedAtUtc, DateTime ExpiresAtUtc);

/// <summary>Доверенные устройства текущего пользователя (D81): список и отзыв — на странице «Безопасность».</summary>
public sealed class TrustedDeviceService(IKnitErpDbContext db, ICurrentUser currentUser, IClock clock)
{
    public async Task<IReadOnlyList<TrustedDeviceDto>> ListAsync(CancellationToken ct = default)
    {
        var userId = currentUser.UserId ?? throw new NotFoundException("Пользователь");
        var stamp = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync(ct);
        var now = clock.UtcNow;
        return (await db.TrustedDevices.AsNoTracking().Where(d => d.UserId == userId && d.RevokedAtUtc == null && d.ExpiresAtUtc > now)
                .OrderByDescending(d => d.LastUsedAtUtc ?? d.CreatedAtUtc).ToListAsync(ct))
            .Where(d => d.IsActive(stamp, now))
            .Select(d => new TrustedDeviceDto(d.Id, d.Label, d.CreatedAtUtc, d.LastUsedAtUtc, d.ExpiresAtUtc)).ToList();
    }

    /// <summary>Отзыв одного устройства; null — всех. Чужое устройство — «не найдено».</summary>
    public async Task RevokeAsync(long? id, CancellationToken ct = default)
    {
        var userId = currentUser.UserId ?? throw new NotFoundException("Пользователь");
        var devices = await db.TrustedDevices.Where(d => d.UserId == userId && d.RevokedAtUtc == null && (id == null || d.Id == id)).ToListAsync(ct);
        if (id is not null && devices.Count == 0)
        {
            throw new NotFoundException("Устройство");
        }

        foreach (var device in devices)
        {
            device.Revoke(clock.UtcNow);
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, currentUser.OrganizationId, userId, AuditActions.DeviceRevoked, nameof(UserAccount),
            userId.ToString(), after: id is null ? $"все устройства ({devices.Count})" : devices[0].Label, correlationId: currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
    }
}
