using KnitErp.Application.Authentication;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Access;

/// <summary>
/// Ограничения входа участника для формы. CanEdit = false — объяснение в CannotEditReason (себя, Владельца, без права).
/// Days — биты дней (пн = 1 … вс = 64), минуты — от полуночи в часовом поясе организации.
/// </summary>
public sealed record SignInRestrictionDto(
    long UserId,
    string Name,
    bool SingleDevice,
    IReadOnlyList<string> Networks,
    bool ScheduleEnabled,
    byte Days,
    int StartMinute,
    int EndMinute,
    string TimeZoneId,
    bool CanEdit,
    string? CannotEditReason,
    byte[]? RowVersion);

/// <summary>Networks — текст: адреса и сети через перевод строки, запятую или пробел.</summary>
public sealed record SaveSignInRestrictionCommand(
    long UserId,
    bool SingleDevice,
    string? Networks,
    bool ScheduleEnabled,
    byte Days,
    int StartMinute,
    int EndMinute,
    string? Reason,
    byte[]? RowVersion);

/// <summary>
/// D127: «Ограничения входа» пользователя в организации — одно устройство, разрешённые сети (IP), время работы. Задаёт администратор
/// с правом управления пользователями после повторного подтверждения паролем (D123). Себя ограничить нельзя (администратор не закроет
/// вход сам себе), Владельца — тоже (организация не останется без входа); ограничения Администратора меняет только Владелец.
/// Аварийная команда сервера (D06) снимает ограничения пользователя.
/// </summary>
public sealed class SignInRestrictionService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, StepUpGuard stepUp)
{
    public async Task<SignInRestrictionDto> GetAsync(long userId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserView, ct);
        var member = await RequireMemberAsync(ctx, userId, ct);
        var name = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(ct);
        var zone = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        var r = await db.SignInRestrictions.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId && x.MemberId == member.Id, ct);
        var cannot = await CannotEditAsync(ctx, userId, ct);
        var window = r?.Window;
        return new SignInRestrictionDto(userId, name, r?.SingleDevice ?? false, r?.Networks ?? [], window is not null,
            window?.Days ?? 0b0011111, window?.StartMinute ?? 8 * 60, window?.EndMinute ?? 20 * 60, zone, cannot is null, cannot?.Message, r?.RowVersion);
    }

    public async Task SaveAsync(SaveSignInRestrictionCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // Та же блокировка, что у смены ролей: «не Владелец» проверяется и сохраняется под ней.
        await db.LockAsync($"kniterp.access.{ctx.OrganizationId}", ct);
        var member = await RequireMemberAsync(ctx, cmd.UserId, ct);
        if (await CannotEditAsync(ctx, cmd.UserId, ct) is { } refusal)
        {
            throw refusal;
        }

        var networks = IpNetworks.ParseList(cmd.Networks);
        var window = cmd.ScheduleEnabled ? WorkWindow.Create(cmd.Days, cmd.StartMinute, cmd.EndMinute) : null;

        var now = clock.UtcNow;
        var existing = await db.SignInRestrictions.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId && x.MemberId == member.Id, ct);
        if (existing is not null && cmd.RowVersion is not null)
        {
            existing.EnsureVersion(existing.RowVersion, cmd.RowVersion);
        }
        else if (existing is not null)
        {
            throw new ConcurrencyConflictException();
        }

        // Сначала — без изменения отслеживаемой записи: отказ подтверждения не должен оставить правку в контексте вкладки.
        var draft = SignInRestriction.Create(ctx.OrganizationId, member.Id, now);
        draft.Update(cmd.SingleDevice, networks, window, ctx.UserId, now);
        var before = existing?.Describe() ?? "нет";
        var after = draft.Describe();
        if (before == after)
        {
            return;
        }

        stepUp.Demand(ctx); // D123: ограничения входа — действие безопасности
        if (existing is null)
        {
            db.SignInRestrictions.Add(draft);
        }
        else
        {
            existing.Update(cmd.SingleDevice, networks, window, ctx.UserId, now);
        }

        db.AuditEntries.Add(AuditEntry.Create(now, ctx.OrganizationId, ctx.UserId, AuditActions.SignInRestrictionsChanged, nameof(UserAccount),
            cmd.UserId.ToString(), before, after, string.IsNullOrWhiteSpace(cmd.Reason) ? null : cmd.Reason.Trim(), currentUser.CorrelationId));
        try
        {
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>Почему нельзя менять ограничения этого пользователя (сообщение — ключ перевода); null — можно.</summary>
    private async Task<BusinessRuleException?> CannotEditAsync(AccessContext ctx, long userId, CancellationToken ct)
    {
        if (!ctx.Permissions.Has(Permissions.UserManage))
        {
            return new BusinessRuleException("access.restriction.no_right", "Ограничения входа меняет администратор с правом управления пользователями.");
        }

        if (userId == ctx.UserId)
        {
            return new BusinessRuleException("access.restriction.self", "Свои ограничения входа меняет другой администратор: так нельзя случайно закрыть вход самому себе.");
        }

        if (await IsOwnerAsync(ctx, userId, ct))
        {
            return new BusinessRuleException("access.restriction.owner", "Владельца ограничить нельзя: организация не должна остаться без входа.");
        }

        if (!ctx.Permissions.Has(Permissions.AdminGrant) && await HasRoleAsync(ctx, userId, SystemRoles.Administrator, ct))
        {
            return new BusinessRuleException("access.restriction.admin", "Ограничения входа Администратора меняет только Владелец.");
        }

        return null;
    }

    private Task<bool> IsOwnerAsync(AccessContext ctx, long userId, CancellationToken ct) =>
        SignInRestrictionRules.IsOwnerAsync(db, userId, ctx.OrganizationId, clock.UtcNow, ct);

    private async Task<bool> HasRoleAsync(AccessContext ctx, long userId, string roleCode, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await db.RoleAssignments
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.UserId == userId && a.RevokedAtUtc == null
                        && a.ValidFromUtc <= now && (a.ValidToUtc == null || a.ValidToUtc > now))
            .Join(db.Roles, a => a.RoleId, r => (long?)r.Id, (a, r) => r.Code)
            .AnyAsync(code => code == roleCode, ct);
    }

    /// <summary>Пользователь чужой организации — «не найдено».</summary>
    private async Task<OrganizationMember> RequireMemberAsync(AccessContext ctx, long userId, CancellationToken ct) =>
        await db.OrganizationMembers.AsNoTracking().SingleOrDefaultAsync(m => m.OrganizationId == ctx.OrganizationId && m.UserId == userId, ct)
        ?? throw new NotFoundException("Пользователь");
}
