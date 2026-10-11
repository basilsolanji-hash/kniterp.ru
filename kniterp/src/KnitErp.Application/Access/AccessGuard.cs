using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Access;

/// <summary>Проверенный контекст запроса: кто, в какой организации и с какими правами.</summary>
public sealed record AccessContext(long UserId, long OrganizationId, EffectivePermissionSet Permissions);

public interface IAccessGuard
{
    /// <summary>Контекст без проверки конкретного права. Без активного участия в организации — отказ.</summary>
    Task<AccessContext> CurrentAsync(CancellationToken ct = default);

    /// <summary>Требует право; при отказе пишет запись в журнал аудита и бросает <see cref="AccessDeniedException"/>.</summary>
    Task<AccessContext> DemandAsync(string permissionCode, CancellationToken ct = default);

    Task<EffectivePermissionSet> LoadAsync(long userId, long organizationId, CancellationToken ct = default);

    /// <summary>
    /// Права пользователя, если его роли заменить на <paramref name="newRole"/>: индивидуальные права и запреты остаются.
    /// Статус учётной записи не учитывается — это предпросмотр для формы, а не проверка доступа.
    /// </summary>
    Task<EffectivePermissionSet> PreviewWithRoleAsync(long? userId, long organizationId, Role newRole, CancellationToken ct = default);

    Task DenyAsync(AccessContext? context, string permissionCode, CancellationToken ct = default);
}

/// <summary>
/// Запрет по умолчанию. Права читаются из базы на каждый запрос — так отзыв действует сразу (требование «не позднее 60 секунд»).
/// Любая ошибка чтения прав прерывает запрос: доступ не выдаётся (fail closed).
/// </summary>
public sealed class AccessGuard(IKnitErpDbContext db, ICurrentUser currentUser, IClock clock) : IAccessGuard
{
    public async Task<AccessContext> CurrentAsync(CancellationToken ct = default)
    {
        if (currentUser.UserId is not { } userId || currentUser.OrganizationId is not { } orgId)
        {
            throw new AccessDeniedException(Permissions.OrganizationView);
        }

        // Аудит 11.10.2026 (D109): заблокированный участник или учётная запись не получают даже контекст — иначе действия
        // без отдельного права (теги, личные инструменты) оставались бы доступны после блокировки, в том числе по токену API.
        if (!await IsActiveMemberAsync(userId, orgId, ct))
        {
            throw new AccessDeniedException(Permissions.OrganizationView);
        }

        var permissions = await ComputeAsync(userId, orgId, ct);

        // D88: по токену API — не больше прав токена и не больше текущих прав его создателя.
        if (currentUser.ApiScope is { } scope)
        {
            permissions = permissions.Restrict(scope);
        }

        return new AccessContext(userId, orgId, permissions);
    }

    public async Task<AccessContext> DemandAsync(string permissionCode, CancellationToken ct = default)
    {
        var context = await CurrentAsync(ct);
        if (!context.Permissions.Has(permissionCode))
        {
            await DenyAsync(context, permissionCode, ct);
        }

        return context;
    }

    public async Task DenyAsync(AccessContext? context, string permissionCode, CancellationToken ct = default)
    {
        db.AuditEntries.Add(AuditEntry.Create(
            clock.UtcNow,
            context?.OrganizationId ?? currentUser.OrganizationId,
            context?.UserId ?? currentUser.UserId,
            AuditActions.AccessDenied,
            "Permission",
            null,
            after: permissionCode,
            correlationId: currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        throw new AccessDeniedException(permissionCode);
    }

    public async Task<EffectivePermissionSet> LoadAsync(long userId, long organizationId, CancellationToken ct = default)
    {
        return await IsActiveMemberAsync(userId, organizationId, ct) ? await ComputeAsync(userId, organizationId, ct) : EffectivePermissionSet.Empty;
    }

    private async Task<EffectivePermissionSet> ComputeAsync(long userId, long organizationId, CancellationToken ct)
    {
        var assignments = await ActiveAssignmentsAsync(userId, organizationId, ct);
        return EffectivePermissionSet.Compute(await ExpandAsync(organizationId, assignments, ct));
    }

    private async Task<bool> IsActiveMemberAsync(long userId, long organizationId, CancellationToken ct) =>
        await db.Users.AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active, ct)
        && await db.OrganizationMembers.AnyAsync(
            m => m.UserId == userId && m.OrganizationId == organizationId && m.Status == MembershipStatus.Active, ct);

    public async Task<EffectivePermissionSet> PreviewWithRoleAsync(long? userId, long organizationId, Role newRole, CancellationToken ct = default)
    {
        var individual = userId is { } id
            ? (await ActiveAssignmentsAsync(id, organizationId, ct)).Where(a => a.RoleId == null).ToList()
            : [];
        var grants = await ExpandAsync(organizationId, individual, ct);
        grants.AddRange(newRole.Permissions.Select(p => new PermissionGrant(p.PermissionCode, p.Level, false)));
        return EffectivePermissionSet.Compute(grants);
    }

    private async Task<List<RoleAssignment>> ActiveAssignmentsAsync(long userId, long organizationId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await db.RoleAssignments
            .Where(a => a.OrganizationId == organizationId && a.UserId == userId && a.RevokedAtUtc == null
                        && a.ValidFromUtc <= now && (a.ValidToUtc == null || a.ValidToUtc > now))
            .ToListAsync(ct);
    }

    /// <summary>Роли раскрываются в права; область назначения переносится на каждое право роли.</summary>
    private async Task<List<PermissionGrant>> ExpandAsync(long organizationId, List<RoleAssignment> assignments, CancellationToken ct)
    {
        var roleIds = assignments.Where(a => a.RoleId != null).Select(a => a.RoleId!.Value).Distinct().ToList();
        var roles = await db.Roles
            .Where(r => r.OrganizationId == organizationId && roleIds.Contains(r.Id))
            .Include(r => r.Permissions)
            .ToDictionaryAsync(r => r.Id, ct);

        var grants = new List<PermissionGrant>();
        foreach (var a in assignments)
        {
            if (a.RoleId is { } roleId)
            {
                if (!roles.TryGetValue(roleId, out var role))
                {
                    continue;
                }

                grants.AddRange(role.Permissions.Select(p =>
                    new PermissionGrant(p.PermissionCode, p.Level, false, a.DepartmentId, a.WarehouseId)));
            }
            else if (a.PermissionCode is { } code)
            {
                grants.Add(new PermissionGrant(code, PermissionLevel.Full, a.IsDeny, a.DepartmentId, a.WarehouseId));
            }
        }

        return grants;
    }
}
