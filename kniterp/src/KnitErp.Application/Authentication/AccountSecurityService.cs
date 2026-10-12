using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Authentication;

/// <summary>D122: сессия в списке «Активные сессии». Current — это устройство (сессия текущего запроса).</summary>
public sealed record UserSessionDto(Guid Id, string Label, string? AddressPrefix, DateTime CreatedAtUtc, DateTime LastSeenAtUtc, bool Current);

/// <summary>
/// D115: личная безопасность входа — смена пароля и «Выйти на всех устройствах».
/// Каждое действие подтверждается текущим паролем, а при подключённой 2FA — и кодом из приложения (повторная проверка):
/// оставленный открытым браузер не позволяет сменить пароль или выгнать владельца учётной записи.
/// Неверный пароль или код считается неудачной попыткой входа (общий счётчик блокировки).
/// После действия штамп безопасности меняется: все сессии, включая текущую, закрываются в течение минуты, доверенные устройства
/// забываются. Владелец учётной записи получает письмо (если на сервере настроена почта).
/// </summary>
public sealed class AccountSecurityService(
    IKnitErpDbContext db, IPasswordHasher<UserAccount> hasher, IEmailSender email, ICurrentUser currentUser, IClock clock)
{
    public async Task<bool> TwoFactorRequiredAsync(CancellationToken ct = default)
    {
        var userId = currentUser.UserId ?? throw new NotFoundException("Пользователь");
        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TwoFactorEnabled).SingleAsync(ct);
    }

    /// <summary>Смена пароля самим пользователем. Токены API, выпущенные им, остаются: пароль известен владельцу.</summary>
    public async Task ChangePasswordAsync(string? currentPassword, string? newPassword, string? confirmation, string? code, CancellationToken ct = default)
    {
        var user = await ConfirmIdentityAsync(currentPassword, code, ct);
        if (!string.Equals(newPassword, confirmation, StringComparison.Ordinal))
        {
            throw new BusinessRuleException("auth.password.mismatch", "Пароли не совпадают.");
        }

        if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
        {
            throw new BusinessRuleException("auth.password.same", "Новый пароль совпадает с текущим.");
        }

        SignInPolicy.EnsurePasswordAcceptable(newPassword, user.Email);
        user.ChangePassword(hasher.HashPassword(user, newPassword!));
        await RevokeDevicesAsync(user.Id, ct);
        await SecurityEvents.RevokeSessionsAsync(db, user.Id, clock.UtcNow, ct);
        await SecurityEvents.AuditAsync(db, currentUser, clock, user, AuditActions.PasswordChanged, "пароль сменён; сессии и доверенные устройства закрыты", ct);
        await db.SaveChangesAsync(ct);
        SecurityEvents.Notify(email, user, "Пароль knitERP изменён",
            "Пароль вашей учётной записи только что изменён. Все сессии закрыты, доверенные устройства забыты.");
    }

    /// <summary>Выход на всех устройствах; revokeApiTokens — также отозвать токены API, выпущенные этим пользователем.</summary>
    public async Task<int> SignOutEverywhereAsync(string? currentPassword, string? code, bool revokeApiTokens, CancellationToken ct = default)
    {
        var user = await ConfirmIdentityAsync(currentPassword, code, ct);
        user.RegisterSuccessfulSignIn();
        user.RotateSecurityStamp();
        await RevokeDevicesAsync(user.Id, ct);
        await SecurityEvents.RevokeSessionsAsync(db, user.Id, clock.UtcNow, ct);
        var tokens = revokeApiTokens ? await SecurityEvents.RevokeApiTokensAsync(db, user.Id, clock.UtcNow, ct) : 0;
        await SecurityEvents.AuditAsync(db, currentUser, clock, user, AuditActions.SessionsRevoked,
            tokens > 0 ? $"все сессии и доверенные устройства закрыты, отозвано токенов API: {tokens}" : "все сессии и доверенные устройства закрыты", ct);
        await db.SaveChangesAsync(ct);
        SecurityEvents.Notify(email, user, "Выход на всех устройствах knitERP",
            "По вашей команде закрыты все сессии knitERP" + (tokens > 0 ? $" и отозвано токенов API: {tokens}" : string.Empty) + ".");
        return tokens;
    }

    /// <summary>
    /// D122: действующие сессии текущего пользователя — по одной на каждый вход (устройство, браузер).
    /// Сессии, закрытые сменой штампа (блокировка, смена роли, пароля), истёкшие и завершённые, не показываются.
    /// </summary>
    public async Task<IReadOnlyList<UserSessionDto>> ListSessionsAsync(CancellationToken ct = default)
    {
        var userId = currentUser.UserId ?? throw new NotFoundException("Пользователь");
        var stamp = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync(ct);
        var now = clock.UtcNow;
        var since = now - SignInPolicy.SessionIdleLimit;
        var current = currentUser.SessionId;
        return (await db.UserSessions.AsNoTracking()
                .Where(x => x.UserId == userId && x.RevokedAtUtc == null && x.SecurityStamp == stamp && x.LastSeenAtUtc > since)
                .OrderByDescending(x => x.LastSeenAtUtc).Take(MaxListedSessions).ToListAsync(ct))
            .Where(x => x.IsActive(stamp, now))
            .Select(x => new UserSessionDto(x.PublicId, x.Label, x.AddressPrefix, x.CreatedAtUtc, x.LastSeenAtUtc, x.PublicId == current))
            .OrderByDescending(x => x.Current).ThenByDescending(x => x.LastSeenAtUtc)
            .ToList();
    }

    /// <summary>Сколько сессий показывает список: защищает страницу от тысяч входов роботом.</summary>
    public const int MaxListedSessions = 50;

    /// <summary>
    /// D122: завершить одну свою сессию на другом устройстве. Подтверждается текущим паролем и кодом 2FA, как «Выйти везде» (D115).
    /// Чужая или неизвестная сессия — «не найдено» (до проверки пароля: попытка не засчитывается в блокировку и ничего не раскрывает).
    /// Текущую сессию завершает кнопка «Выйти». Устройство теряет доступ при следующей проверке cookie — не позднее чем через минуту.
    /// </summary>
    public async Task RevokeSessionAsync(Guid sessionId, string? currentPassword, string? code, CancellationToken ct = default)
    {
        var userId = currentUser.UserId ?? throw new NotFoundException("Пользователь");
        var session = await db.UserSessions.AsNoTracking().SingleOrDefaultAsync(x => x.PublicId == sessionId && x.UserId == userId && x.RevokedAtUtc == null, ct)
                      ?? throw new NotFoundException("Сессия");
        if (session.PublicId == currentUser.SessionId)
        {
            throw new BusinessRuleException("auth.session.current", "Текущую сессию завершает кнопка «Выйти».");
        }

        var user = await ConfirmIdentityAsync(currentPassword, code, ct);
        user.RegisterSuccessfulSignIn();
        var now = clock.UtcNow;
        await using var tx = await db.BeginTransactionAsync(ct);
        // Условный UPDATE не спорит по rowversion с отметкой активности той же сессии; 0 строк — её уже завершили.
        var revoked = await db.UserSessions.Where(x => x.Id == session.Id && x.UserId == userId && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(x => x.SetProperty(v => v.RevokedAtUtc, now), ct);
        if (revoked == 0)
        {
            throw new NotFoundException("Сессия");
        }

        await SecurityEvents.AuditAsync(db, currentUser, clock, user, AuditActions.SessionRevoked,
            $"завершена сессия: {session.Label}, сеть {session.AddressPrefix ?? "неизвестна"}, вход {session.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Проверка пароля и кода — общая с повторным подтверждением D123 (IdentityConfirmation), поведение прежнее.</summary>
    private Task<UserAccount> ConfirmIdentityAsync(string? password, string? code, CancellationToken ct) =>
        IdentityConfirmation.ConfirmAsync(db, hasher, currentUser, clock, password, code,
            AuditActions.SignInFailed, "неверный пароль при подтверждении действия", "неверный код 2FA при подтверждении действия", ct);

    private async Task RevokeDevicesAsync(long userId, CancellationToken ct)
    {
        foreach (var device in await db.TrustedDevices.Where(d => d.UserId == userId && d.RevokedAtUtc == null).ToListAsync(ct))
        {
            device.Revoke(clock.UtcNow);
        }
    }
}

/// <summary>Общие действия событий безопасности: запись в журнал всех организаций пользователя, письмо, отзыв токенов API.</summary>
internal static class SecurityEvents
{
    public static async Task AuditAsync(IKnitErpDbContext db, ICurrentUser currentUser, IClock clock, UserAccount user, string action, string reason,
        CancellationToken ct)
    {
        var orgIds = await db.OrganizationMembers.Where(m => m.UserId == user.Id).Select(m => (long?)m.OrganizationId).ToListAsync(ct);
        var withAddress = currentUser.ClientAddress is { } ip ? $"{reason} · адрес {ip}" : reason;
        foreach (var orgId in orgIds.DefaultIfEmpty(null))
        {
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, orgId, user.Id, action, nameof(UserAccount), user.Id.ToString(),
                reason: withAddress, correlationId: currentUser.CorrelationId));
        }
    }

    /// <summary>D122: все серверные сессии пользователя завершаются (выход везде, смена и сброс пароля).</summary>
    public static Task<int> RevokeSessionsAsync(IKnitErpDbContext db, long userId, DateTime nowUtc, CancellationToken ct) =>
        db.UserSessions.Where(x => x.UserId == userId && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(x => x.SetProperty(v => v.RevokedAtUtc, nowUtc), ct);

    /// <summary>Токены API, выпущенные пользователем, отзываются: после сброса пароля и по его просьбе (D115).</summary>
    public static async Task<int> RevokeApiTokensAsync(IKnitErpDbContext db, long userId, DateTime nowUtc, CancellationToken ct)
    {
        var tokens = await db.ApiTokens.Where(t => t.CreatedByUserId == userId && t.RevokedAtUtc == null).ToListAsync(ct);
        foreach (var token in tokens)
        {
            token.Revoke(userId, nowUtc);
        }

        return tokens.Count;
    }

    /// <summary>
    /// Письмо о событии безопасности. Уходит в фоне и не содержит ссылок — его нельзя использовать для фишинга от нашего имени;
    /// если действие сделал не владелец, письмо подсказывает, что делать.
    /// </summary>
    public static void Notify(IEmailSender email, UserAccount user, string subject, string what)
    {
        if (!email.IsConfigured)
        {
            return;
        }

        var text = $"""
                    Здравствуйте, {user.DisplayName}!

                    {what}

                    Если это сделали не вы, срочно смените пароль через «Забыли пароль» на странице входа и сообщите
                    администратору вашей организации.

                    Письмо отправлено автоматически, отвечать на него не нужно.
                    """;
        var to = user.Email;
        _ = Task.Run(() => email.SendAsync(to, subject, text), CancellationToken.None);
    }
}
