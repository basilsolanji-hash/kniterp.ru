using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Authentication;

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
        var tokens = revokeApiTokens ? await SecurityEvents.RevokeApiTokensAsync(db, user.Id, clock.UtcNow, ct) : 0;
        await SecurityEvents.AuditAsync(db, currentUser, clock, user, AuditActions.SessionsRevoked,
            tokens > 0 ? $"все сессии и доверенные устройства закрыты, отозвано токенов API: {tokens}" : "все сессии и доверенные устройства закрыты", ct);
        await db.SaveChangesAsync(ct);
        SecurityEvents.Notify(email, user, "Выход на всех устройствах knitERP",
            "По вашей команде закрыты все сессии knitERP" + (tokens > 0 ? $" и отозвано токенов API: {tokens}" : string.Empty) + ".");
        return tokens;
    }

    private async Task<UserAccount> ConfirmIdentityAsync(string? password, string? code, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new NotFoundException("Пользователь");
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, ct)
                   ?? throw new NotFoundException("Пользователь");
        var now = clock.UtcNow;
        if (user.IsLockedOut(now))
        {
            throw new BusinessRuleException("auth.locked", "Вход временно заблокирован после неудачных попыток. Попробуйте позже.");
        }

        var passwordOk = user.PasswordHash is not null
                         && hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty) != PasswordVerificationResult.Failed;
        var codeOk = !user.TwoFactorEnabled || user.VerifyTotp(code, now);
        if (passwordOk && codeOk)
        {
            return user;
        }

        user.RegisterFailedSignIn(now);
        await SecurityEvents.AuditAsync(db, currentUser, clock, user, AuditActions.SignInFailed,
            passwordOk ? "неверный код 2FA при подтверждении действия" : "неверный пароль при подтверждении действия", ct);
        await db.SaveChangesAsync(ct);
        if (user.TwoFactorEnabled)
        {
            throw new BusinessRuleException("auth.confirm.failed", "Неверный текущий пароль или код из приложения.");
        }

        throw new BusinessRuleException("auth.confirm.failed", "Неверный текущий пароль.");
    }

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
