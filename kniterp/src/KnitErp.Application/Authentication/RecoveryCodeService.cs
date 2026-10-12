using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Authentication;

public sealed record RecoveryCodeStatus(bool TwoFactorEnabled, int Remaining, DateTime? IssuedAtUtc);

public sealed record EmergencyAccessResult(long UserId, string DisplayName, string RecoveryToken);

/// <summary>
/// Аварийный доступ (D06). Первый рубеж — одноразовые резервные коды, которые пользователь с 2FA хранит на бумаге:
/// ими входят вместо кода с телефона. Второй — сброс 2FA другим администратором (UserAccessService). Последний —
/// команда сервера <see cref="EmergencyRestoreAsync"/>, если потерян доступ у единственного Владельца.
/// </summary>
public sealed class RecoveryCodeService(IKnitErpDbContext db, ICurrentUser currentUser, IClock clock)
{
    public async Task<RecoveryCodeStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var userId = RequireUser();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        var codes = await db.RecoveryCodes.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(ct);
        return new RecoveryCodeStatus(user.TwoFactorEnabled, codes.Count(c => c.UsedAtUtc is null),
            codes.Count == 0 ? null : codes.Max(c => c.CreatedAtUtc));
    }

    /// <summary>Новый набор кодов вместо прежнего. Коды возвращаются один раз — в базе только их хеши.</summary>
    public async Task<IReadOnlyList<string>> IssueAsync(CancellationToken ct = default)
    {
        var userId = RequireUser();
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (!user.TwoFactorEnabled)
        {
            throw new BusinessRuleException("auth.recovery.no_2fa", "Резервные коды нужны только при входе с кодом (2FA). Сначала подключите приложение-аутентификатор.");
        }

        db.RecoveryCodes.RemoveRange(await db.RecoveryCodes.Where(r => r.UserId == userId).ToListAsync(ct));
        var (codes, records) = RecoveryCode.Issue(userId, clock.UtcNow);
        db.RecoveryCodes.AddRange(records);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, currentUser.OrganizationId, userId, AuditActions.RecoveryCodesIssued,
            nameof(UserAccount), userId.ToString(), after: $"{codes.Count} кодов", reason: "прежние коды недействительны",
            correlationId: currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return codes;
    }

    /// <summary>
    /// Команда сервера «emergency-access»: сброс 2FA, резервных кодов и блокировки входа, ссылка установки нового пароля.
    /// Нужен доступ к серверу и мастер-ключу, поэтому это последний рубеж, а не обычный путь. Пишется в журнал всех
    /// организаций пользователя.
    /// </summary>
    public async Task<EmergencyAccessResult> EmergencyRestoreAsync(string? email, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
        {
            throw new BusinessRuleException("auth.emergency.reason", "Укажите причину (--reason), она попадёт в журнал аудита.");
        }

        var normalized = (email ?? string.Empty).Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct)
                   ?? throw new NotFoundException("Пользователь");

        var now = clock.UtcNow;
        await using var tx = await db.BeginTransactionAsync(ct);
        var had2Fa = user.TwoFactorEnabled;
        user.ResetTwoFactor();
        var token = user.IssueRecoveryToken(now);
        db.RecoveryCodes.RemoveRange(await db.RecoveryCodes.Where(r => r.UserId == user.Id).ToListAsync(ct));

        var orgIds = await db.OrganizationMembers.Where(m => m.UserId == user.Id).Select(m => (long?)m.OrganizationId).ToListAsync(ct);

        // D127: ограничения входа пользователя (сети, время, одно устройство) снимаются во всех организациях — иначе восстановленный
        // вход мог бы снова упереться в ограничение, ошибочно заданное администратором.
        var memberIds = await db.OrganizationMembers.Where(m => m.UserId == user.Id).Select(m => m.Id).ToListAsync(ct);
        var restrictions = await db.SignInRestrictions.Where(r => memberIds.Contains(r.MemberId)).ToListAsync(ct);
        var lifted = new HashSet<long>();
        foreach (var r in restrictions.Where(r => !r.IsEmpty))
        {
            r.Update(false, [], null, null, now);
            lifted.Add(r.OrganizationId);
        }

        foreach (var orgId in orgIds.DefaultIfEmpty(null))
        {
            db.AuditEntries.Add(AuditEntry.Create(now, orgId, null, AuditActions.EmergencyAccess, nameof(UserAccount), user.Id.ToString(),
                before: had2Fa ? "2FA включена" : "2FA не подключена",
                after: $"2FA и резервные коды сброшены, ссылка нового пароля до {user.SetupTokenExpiresAtUtc:yyyy-MM-dd HH:mm} UTC"
                       + (orgId is { } id && lifted.Contains(id) ? ", ограничения входа сняты" : ""),
                reason: "команда сервера: " + reason.Trim(), correlationId: currentUser.CorrelationId));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new EmergencyAccessResult(user.Id, user.DisplayName, token);
    }

    private long RequireUser() =>
        currentUser.UserId ?? throw new NotFoundException("Пользователь");
}
