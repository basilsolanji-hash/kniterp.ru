using KnitErp.Application.Authentication;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Access;

/// <summary>Роль для страницы «Роли и права»: системная (шаблон, не меняется) или своя; сколько у неё пользователей.</summary>
public sealed record RoleCardDto(
    long Id, string Code, string Name, string? Description, bool IsSystem, bool IsArchived, bool IsAdministrative, int Users,
    IReadOnlyList<string> PermissionCodes, byte[] RowVersion);

public sealed record PermissionItemDto(string Code, string Label, bool Administrative, bool OwnerOnly);

public sealed record PermissionGroupDto(string Name, IReadOnlyList<PermissionItemDto> Items);

/// <summary>
/// Свои роли организации (D112). Смотреть — «Пользователи и роли: просмотр», менять — «изменение». Нельзя положить в роль право выше
/// своего; роль с управлением пользователями — административная: её создаёт, меняет и выдаёт только Владелец. Изменение роли сразу
/// действует на всех, у кого она есть: права читаются из базы на каждый запрос. Каждое изменение — в журнале аудита.
/// D123: создать административную роль или изменить роль, которая до или после правки административная, — только после
/// повторного подтверждения паролем (StepUpGuard).
/// </summary>
public sealed class RoleService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, StepUpGuard stepUp)
{
    public const int MaxCustomRoles = 50;

    public async Task<IReadOnlyList<RoleCardDto>> ListAsync(bool includeArchived = false, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserView, ct);
        var now = clock.UtcNow;
        var users = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.RoleId != null && a.RevokedAtUtc == null && (a.ValidToUtc == null || a.ValidToUtc > now))
            .GroupBy(a => a.RoleId!.Value).Select(g => new { g.Key, Count = g.Select(a => a.UserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions)
            .Where(r => r.OrganizationId == ctx.OrganizationId && (includeArchived || !r.IsArchived)).ToListAsync(ct);
        return roles
            .OrderBy(r => r.IsArchived).ThenBy(r => r.IsSystem ? 0 : 1)
            .ThenBy(r => r.IsSystem ? SystemRoles.Ordered.ToList().IndexOf(r.Code) : 0).ThenBy(r => r.Name)
            .Select(r => new RoleCardDto(r.Id, r.Code, r.Name, r.Description, r.IsSystem, r.IsArchived, r.IsAdministrative, users.GetValueOrDefault(r.Id),
                r.Permissions.Where(p => p.Level.Grants()).Select(p => p.PermissionCode).Order().ToList(), r.RowVersion))
            .ToList();
    }

    /// <summary>Все права по группам — для редактора роли и особых прав пользователя.</summary>
    public static IReadOnlyList<PermissionGroupDto> Catalog() =>
        Permissions.All.GroupBy(Permissions.GroupOf)
            .OrderBy(g => Permissions.Groups.ToList().IndexOf(g.Key) is var i && i < 0 ? int.MaxValue : i)
            .Select(g => new PermissionGroupDto(g.Key, g.Select(code => new PermissionItemDto(code, Permissions.Describe(code),
                Permissions.IsAdministrative(code), code == Permissions.AdminGrant)).ToList()))
            .ToList();

    public async Task<long> CreateAsync(string? name, string? description, IReadOnlyCollection<string> permissionCodes, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        var role = Role.CreateCustom(ctx.OrganizationId, name, description, permissionCodes);
        EnsureMayDefine(ctx, role);
        if (await db.Roles.CountAsync(r => r.OrganizationId == ctx.OrganizationId && !r.IsSystem && !r.IsArchived, ct) >= MaxCustomRoles)
        {
            throw new BusinessRuleException("access.role.too_many", $"Своих ролей — не больше {MaxCustomRoles}.");
        }

        await EnsureFreeNameAsync(ctx, role.Name, null, ct);
        if (role.IsAdministrative)
        {
            stepUp.Demand(ctx); // D123
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.RoleCreated, role, null, Describe(role), null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return role.Id;
    }

    /// <summary>Своя роль на основе другой (обычно системной): те же права, название «… (копия)».</summary>
    public async Task<long> CopyAsync(long sourceRoleId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        var source = await LoadAsync(ctx, sourceRoleId, ct);
        var codes = source.Permissions.Where(p => p.Level.Grants() && p.PermissionCode != Permissions.AdminGrant).Select(p => p.PermissionCode).ToList();
        var name = source.Name + " (копия)";
        for (var i = 2; await db.Roles.AnyAsync(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived && r.Name == name, ct); i++)
        {
            name = $"{source.Name} (копия {i})";
        }

        return await CreateAsync(name, source.Description, codes, ct);
    }

    public async Task UpdateAsync(long id, string? name, string? description, IReadOnlyCollection<string> permissionCodes, byte[] rowVersion,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        var role = (await LoadAsync(ctx, id, ct)).EnsureVersion(rowVersion);
        // Административная роль до или после правки — только Владелец.
        GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, role.IsAdministrative);
        var administrativeAfter = permissionCodes.Any(Permissions.IsAdministrative);
        GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, administrativeAfter);
        if (role.IsAdministrative || administrativeAfter)
        {
            stepUp.Demand(ctx); // D123: до изменения сущности — отказ не оставляет правок в контексте вкладки
        }

        var before = Describe(role);
        role.Update(name, description, permissionCodes);
        EnsureMayDefine(ctx, role);
        await EnsureFreeNameAsync(ctx, role.Name, id, ct);
        Audit(ctx, AuditActions.RoleChanged, role, before, Describe(role), null);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>В архив — только роль, которую никто не носит: иначе люди молча потеряли бы права.</summary>
    public async Task ArchiveAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        var role = (await LoadAsync(ctx, id, ct)).EnsureVersion(rowVersion);
        GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, role.IsAdministrative);
        var now = clock.UtcNow;
        var holders = await db.RoleAssignments.CountAsync(a => a.OrganizationId == ctx.OrganizationId && a.RoleId == id && a.RevokedAtUtc == null
                                                               && (a.ValidToUtc == null || a.ValidToUtc > now), ct);
        if (holders > 0)
        {
            throw new BusinessRuleException("access.role.in_use", $"Роль выдана пользователям: {holders}. Сначала смените им роль.");
        }

        role.Archive();
        Audit(ctx, AuditActions.RoleChanged, role, "действует", "в архиве", null);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RestoreAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        var role = (await LoadAsync(ctx, id, ct)).EnsureVersion(rowVersion);
        GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, role.IsAdministrative);
        await EnsureFreeNameAsync(ctx, role.Name, id, ct);
        role.Restore();
        Audit(ctx, AuditActions.RoleChanged, role, "в архиве", "действует", null);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Нельзя положить в роль право выше своего; административная роль — только Владелец. D128: своя роль даёт права уровня «Д»
    /// на всю организацию (область задаётся при назначении), поэтому положить в неё право можно только при своём «Д».
    /// </summary>
    private static void EnsureMayDefine(AccessContext ctx, Role role)
    {
        GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, role.IsAdministrative);
        GrantPolicy.EnsureNoEscalation(ctx.Permissions, role.Permissions.Select(p => (p.PermissionCode, p.Level)));
    }

    private async Task<Role> LoadAsync(AccessContext ctx, long id, CancellationToken ct) =>
        await db.Roles.Include(r => r.Permissions).SingleOrDefaultAsync(r => r.Id == id && r.OrganizationId == ctx.OrganizationId, ct)
        ?? throw new NotFoundException("Роль");

    private async Task EnsureFreeNameAsync(AccessContext ctx, string name, long? exceptId, CancellationToken ct)
    {
        if (await db.Roles.AnyAsync(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived && r.Name == name && r.Id != exceptId, ct))
        {
            throw new BusinessRuleException("access.role.duplicate", $"Роль «{name}» уже есть.");
        }
    }

    private static string Describe(Role role) =>
        $"{role.Name}: {string.Join(", ", role.Permissions.Where(p => p.Level.Grants()).Select(p => Permissions.Describe(p.PermissionCode)).Order())}";

    private void Audit(AccessContext ctx, string action, Role role, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(Role), role.Id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}

internal static class RoleVersion
{
    public static Role EnsureVersion(this Role role, byte[] rowVersion) => role.EnsureVersion(role.RowVersion, rowVersion);
}
