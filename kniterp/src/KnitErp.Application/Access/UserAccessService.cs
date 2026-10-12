using KnitErp.Application.Authentication;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Access;

public sealed record UserRowDto(
    long UserId,
    string DisplayName,
    string Email,
    UserStatus AccountStatus,
    MembershipStatus MembershipStatus,
    IReadOnlyList<string> RoleCodes,
    IReadOnlyList<string> RoleNames,
    bool HasPassword,
    bool TwoFactorEnabled,
    string ScopeText,
    bool IsCurrentUser,
    long? DepartmentId = null,
    long? WarehouseId = null);

/// <summary>Строка итоговых прав «на человеческом языке» для блока «Было → Станет».</summary>
public sealed record PermissionLineDto(string PermissionCode, string Label, PermissionLevel Level)
{
    public string Text => Level switch
    {
        PermissionLevel.Scoped => $"{Label} — своя область",
        PermissionLevel.ReadOnly => $"{Label} — только чтение",
        PermissionLevel.OwnOnly => $"{Label} — только свои данные",
        _ => Label,
    };
}

public sealed record AccessPreviewDto(
    IReadOnlyList<PermissionLineDto> Before,
    IReadOnlyList<PermissionLineDto> After,
    string ScopeText,
    bool IsAdministrative,
    bool ReasonRequired);

/// <summary>Роль в форме. Allowed = false: выдать её текущий пользователь не может (сервер всё равно проверит).</summary>
public sealed record RoleOptionDto(string Code, string Name, bool IsAdministrative, bool Allowed, string? DeniedReason);

/// <summary>Кто вошёл и что ему доступно — для меню. Защитой не является: каждое действие проверяет сервер.</summary>
public sealed record CurrentAccessDto(
    long UserId,
    string DisplayName,
    string OrganizationName,
    string TimeZoneId,
    IReadOnlyList<string> RoleCodes,
    IReadOnlyList<string> RoleNames,
    IReadOnlySet<string> Granted,
    IReadOnlySet<string> OwnOnly,
    string? PositionName = null,
    string? WebsiteUrl = null,
    string CountryCode = KnitErp.Domain.Organizations.Countries.Russia)
{
    /// <summary>Инициалы для значка профиля (фотографий в системе пока нет — допущение D50).</summary>
    public string Initials => string.Concat(DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.FirstOrDefault(char.IsLetter)).Where(c => c != default).Take(2).Select(char.ToUpperInvariant));

    /// <summary>Подпись под именем: должность сотрудника, а если учётная запись не связана с сотрудником — роль.</summary>
    public string Subtitle => PositionName ?? (RoleNames.Count > 0 ? string.Join(", ", RoleNames) : "Без роли");

    public bool Has(string permissionCode) => Granted.Contains(permissionCode);

    public bool HasOwnOnly(string permissionCode) => Granted.Contains(permissionCode) || OwnOnly.Contains(permissionCode);
}

/// <summary>
/// Приглашение. DepartmentId / WarehouseId — область роли (подразделение с вложенными или склад); EmployeeId — карточка сотрудника,
/// к которой привязывается учётная запись (имя тогда берётся из карточки).
/// </summary>
public sealed record InviteUserCommand(
    string Email, string DisplayName, string RoleCode, string? Reason, long? DepartmentId = null, long? EmployeeId = null,
    long? WarehouseId = null);

public sealed record ChangeRoleCommand(long UserId, string NewRoleCode, string? Reason, long? DepartmentId = null, long? WarehouseId = null);

/// <summary>
/// Итог приглашения. Токен ссылки показывается администратору один раз и не хранится.
/// Пользователю, у которого уже есть пароль (участник другой организации), ссылка не нужна.
/// </summary>
/// <summary>D112: строка итоговых прав — есть ли право, от каких ролей, есть ли особое разрешение или запрет.</summary>
public sealed record UserRightRowDto(string Code, string Label, string Group, bool Effective, string? FromRoles, bool Granted, bool Denied);

public sealed record IndividualRightDto(Guid Id, string PermissionCode, string Label, bool Deny, DateTime? ValidToUtc, string? Reason, string GrantedBy, DateTime GrantedAtUtc);

public sealed record UserRightsDto(long UserId, string Name, IReadOnlyList<string> Roles, IReadOnlyList<UserRightRowDto> Rows,
    IReadOnlyList<IndividualRightDto> Individual, bool CanEdit, bool Active = true);

public sealed record InvitationResult(long UserId, string? SetupToken, DateTime? ExpiresAtUtc);

public sealed record RoleMatrixDto(IReadOnlyList<RoleColumnDto> Roles, IReadOnlyList<MatrixRowDto> Rows);

public sealed record RoleColumnDto(long RoleId, string Code, string Name);

public sealed record MatrixRowDto(string PermissionCode, string Label, IReadOnlyList<PermissionLevel> Levels);

/// <summary>
/// Пользователи, роли и назначения прав организации (ТЗ §4.6–4.14).
/// D123: опасные действия — выдача и снятие административной роли (в том числе Владельца), особые права управления
/// пользователями, блокировка и разблокировка, сброс 2FA и ссылка смены пароля — только после повторного подтверждения
/// паролем (StepUpGuard). Проверка идёт после прав и правил: без права — отказ, а не окно подтверждения.
/// </summary>
public sealed class UserAccessService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, StepUpGuard stepUp)
{
    public async Task<IReadOnlyList<UserRowDto>> ListUsersAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserView, ct);
        var now = clock.UtcNow;

        var members = await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { m, u })
            .OrderBy(x => x.u.DisplayName)
            .ToListAsync(ct);

        var userIds = members.Select(x => x.u.Id).ToList();
        var roleLinks = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && userIds.Contains(a.UserId) && a.RoleId != null
                        && a.RevokedAtUtc == null && a.ValidFromUtc <= now && (a.ValidToUtc == null || a.ValidToUtc > now))
            .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => (long?)r.Id, (a, r) => new { a.UserId, r.Code, r.Name })
            .ToListAsync(ct);

        var scopes = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && userIds.Contains(a.UserId) && a.RoleId != null
                        && a.RevokedAtUtc == null && a.ValidFromUtc <= now && (a.ValidToUtc == null || a.ValidToUtc > now))
            .Select(a => new { a.UserId, a.DepartmentId, a.WarehouseId })
            .ToListAsync(ct);
        var names = await ScopeNamesAsync(ctx.OrganizationId, scopes.Select(a => (a.DepartmentId, a.WarehouseId)), ct);

        return members.Select(x =>
        {
            var roles = roleLinks.Where(r => r.UserId == x.u.Id).ToList();
            var userScopes = scopes.Where(a => a.UserId == x.u.Id).ToList();
            return new UserRowDto(x.u.Id, x.u.DisplayName, x.u.Email, x.u.Status, x.m.Status,
                roles.Select(r => r.Code).ToList(), roles.Select(r => r.Name).ToList(),
                x.u.PasswordHash != null, x.u.TwoFactorEnabled,
                DescribeScope(userScopes.Select(a => (a.DepartmentId, a.WarehouseId)), names),
                x.u.Id == ctx.UserId,
                userScopes.Select(a => a.DepartmentId).FirstOrDefault(id => id is not null),
                userScopes.Select(a => a.WarehouseId).FirstOrDefault(id => id is not null));
        }).ToList();
    }

    /// <summary>Что видно в меню. Источник — те же итоговые права, что проверяет сервер.</summary>
    public async Task<CurrentAccessDto> GetCurrentAccessAsync(CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == ctx.UserId, ct);
        var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == ctx.OrganizationId, ct);
        var roles = await ActiveRolesAsync(ctx.OrganizationId, ctx.UserId, ct);
        var ownOnly = Permissions.All.Where(c => !ctx.Permissions.Has(c) && ctx.Permissions.HasOwnOnly(c)).ToHashSet();
        var position = await db.Employees.AsNoTracking()
            .Where(e => e.OrganizationId == ctx.OrganizationId && e.UserId == ctx.UserId)
            .Join(db.Positions.AsNoTracking(), e => e.PositionId, p => p.Id, (e, p) => p.Name)
            .FirstOrDefaultAsync(ct);
        return new CurrentAccessDto(ctx.UserId, user.DisplayName, org.ShortName, org.TimeZoneId, roles.Select(r => r.Code).ToList(), roles.Select(r => r.Name).ToList(),
            ctx.Permissions.GrantedCodes.ToHashSet(), ownOnly, position, org.WebsiteUrl, org.CountryCode);
    }

    /// <summary>Роли организации для формы: какие текущий пользователь вправе выдать и почему нет.</summary>
    public async Task<IReadOnlyList<RoleOptionDto>> ListGrantableRolesAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        var roles = await db.Roles.AsNoTracking().Where(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived).Include(r => r.Permissions).ToListAsync(ct);

        return OrderRoles(roles).Select(role =>
        {
            try
            {
                GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, role.IsAdministrative);
                GrantPolicy.EnsureNoEscalation(ctx.Permissions, role.Permissions.Select(p => (p.PermissionCode, p.Level)));
                return new RoleOptionDto(role.Code, role.Name, role.IsAdministrative, true, null);
            }
            catch (BusinessRuleException ex)
            {
                return new RoleOptionDto(role.Code, role.Name, role.IsAdministrative, false, ex.Message);
            }
        }).ToList();
    }

    /// <summary>
    /// «Было → Станет» до сохранения: итоговые права пользователя сейчас и после назначения роли.
    /// Индивидуальные права и запреты сохраняются — смена роли их не трогает.
    /// </summary>
    public async Task<AccessPreviewDto> PreviewRoleAsync(
        long? userId, string roleCode, long? departmentId = null, long? warehouseId = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        var role = await FindRoleAsync(ctx, roleCode, ct);
        var before = EffectivePermissionSet.Empty;
        if (userId is { } id)
        {
            await RequireMemberAsync(ctx, id, ct);
            before = await guard.LoadAsync(id, ctx.OrganizationId, ct);
        }

        var after = await guard.PreviewWithRoleAsync(userId, ctx.OrganizationId, role, ct);
        var scope = await ResolveScopeAsync(ctx, departmentId, warehouseId, ct);
        return new AccessPreviewDto(Lines(before), Lines(after), scope ?? "Вся организация", role.IsAdministrative, role.IsAdministrative);
    }

    private static List<PermissionLineDto> Lines(EffectivePermissionSet set) =>
        RoleMatrixP0.PermissionCodes
            .Select(code => set.Has(code)
                ? new PermissionLineDto(code, Permissions.Describe(code), set.LevelOf(code))
                : set.HasOwnOnly(code) ? new PermissionLineDto(code, Permissions.Describe(code), PermissionLevel.OwnOnly) : null)
            .OfType<PermissionLineDto>()
            .ToList();

    /// <summary>
    /// Назначение без области действует на всю организацию; с подразделением — на него и вложенные (D29);
    /// со складом — только на этот склад.
    /// </summary>
    private static string DescribeScope(IEnumerable<(long? Department, long? Warehouse)> scopes, ScopeNames names)
    {
        var list = scopes.ToList();
        if (list.Count == 0)
        {
            return "—";
        }

        if (list.Any(x => x.Department is null && x.Warehouse is null))
        {
            return "Вся организация";
        }

        return string.Join(", ", list.SelectMany(x => new[]
        {
            x.Department is { } d ? $"«{names.Departments.GetValueOrDefault(d, "подразделение")}» и вложенные" : null,
            x.Warehouse is { } w ? $"склад «{names.Warehouses.GetValueOrDefault(w, "?")}»" : null,
        }).OfType<string>().Distinct());
    }

    private sealed record ScopeNames(Dictionary<long, string> Departments, Dictionary<long, string> Warehouses);

    private async Task<ScopeNames> ScopeNamesAsync(long organizationId, IEnumerable<(long? Department, long? Warehouse)> scopes, CancellationToken ct)
    {
        var list = scopes.ToList();
        var deps = list.Select(x => x.Department).OfType<long>().Distinct().ToList();
        var whs = list.Select(x => x.Warehouse).OfType<long>().Distinct().ToList();
        return new ScopeNames(
            deps.Count == 0 ? [] : await db.Departments.AsNoTracking().Where(d => d.OrganizationId == organizationId && deps.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Name, ct),
            whs.Count == 0 ? [] : await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == organizationId && whs.Contains(w.Id))
                .ToDictionaryAsync(w => w.Id, w => w.Name, ct));
    }

    /// <summary>Проверяет область (своя организация, не в архиве) и возвращает её подпись для журнала; null — вся организация.</summary>
    private async Task<string?> ResolveScopeAsync(AccessContext ctx, long? departmentId, long? warehouseId, CancellationToken ct)
    {
        var parts = new List<string>();
        if (departmentId is { } depId)
        {
            var d = await db.Departments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == depId && x.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Подразделение");
            parts.Add(d.IsArchived
                ? throw new BusinessRuleException("structure.department.archived", "Подразделение в архиве.")
                : $"«{d.Name}» и вложенные");
        }

        if (warehouseId is { } whId)
        {
            var w = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == whId && x.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Склад");
            parts.Add(w.IsArchived ? throw new BusinessRuleException("catalog.archived", "Склад в архиве.") : $"склад «{w.Name}»");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static List<Role> OrderRoles(List<Role> roles) =>
        roles.OrderBy(r =>
        {
            var i = SystemRoles.Ordered.ToList().IndexOf(r.Code);
            return i < 0 ? int.MaxValue : i;
        }).ThenBy(r => r.Name).ToList();

    private async Task<List<Role>> ActiveRolesAsync(long organizationId, long userId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var roles = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.UserId == userId && a.RoleId != null
                        && a.RevokedAtUtc == null && a.ValidFromUtc <= now && (a.ValidToUtc == null || a.ValidToUtc > now))
            .Join(db.Roles.AsNoTracking(), a => a.RoleId, r => (long?)r.Id, (a, r) => r)
            .ToListAsync(ct);
        return OrderRoles(roles);
    }

    /// <summary>
    /// Приглашение пользователя с ролью и ссылкой установки пароля на 72 часа.
    /// С карточкой сотрудника учётная запись привязывается к ней (§4.10 п.1).
    /// </summary>
    public async Task<InvitationResult> InviteAsync(InviteUserCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        var role = await FindRoleAsync(ctx, cmd.RoleCode, ct);
        await EnsureMayGrantRoleAsync(ctx, role, cmd.Reason, ct);
        var scopeName = await ResolveScopeAsync(ctx, cmd.DepartmentId, cmd.WarehouseId, ct);
        if (role.IsAdministrative)
        {
            stepUp.Demand(ctx); // D123
        }

        Employee? employee = null;
        if (cmd.EmployeeId is { } employeeId)
        {
            employee = await db.Employees.SingleOrDefaultAsync(e => e.Id == employeeId && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        }

        var displayName = string.IsNullOrWhiteSpace(cmd.DisplayName) && employee is not null ? employee.FullName : cmd.DisplayName;
        var now = clock.UtcNow;
        await using var tx = await db.BeginTransactionAsync(ct);

        var normalized = (cmd.Email ?? string.Empty).Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (user is null)
        {
            user = UserAccount.Invite(cmd.Email ?? string.Empty, displayName, now);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }
        else if (await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.UserId == user.Id, ct))
        {
            throw new BusinessRuleException("access.user.exists", "Этот пользователь уже есть в организации.");
        }

        // Аудит 11.10.2026 (D109): учётная запись, приглашённая другой организацией и ещё без пароля, получает пароль по своей
        // первой ссылке; новая ссылка отсюда дала бы этой организации вход в чужую учётную запись.
        var invitedElsewhere = !user.HasPassword && user.Id != 0
                               && await db.OrganizationMembers.AnyAsync(m => m.UserId == user.Id && m.OrganizationId != ctx.OrganizationId, ct);
        var token = user.HasPassword || invitedElsewhere ? null : user.IssueSetupToken(now);
        db.OrganizationMembers.Add(OrganizationMember.Join(ctx.OrganizationId, user.Id, now));
        db.RoleAssignments.Add(RoleAssignment.ForRole(ctx.OrganizationId, user.Id, role.Id, ctx.UserId, now, cmd.Reason,
            departmentId: cmd.DepartmentId, warehouseId: cmd.WarehouseId));
        db.AuditEntries.Add(Audit(ctx, AuditActions.UserInvited, user.Id, null, user.Email, null));
        if (employee is not null)
        {
            employee.LinkUser(user.Id);
            db.AuditEntries.Add(AuditEntry.Create(now, ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeLinkedToUser, nameof(Employee),
                employee.Id.ToString(), null, user.Email, null, currentUser.CorrelationId));
        }
        db.AuditEntries.Add(Audit(ctx, role.IsAdministrative ? AuditActions.AdminRoleGranted : AuditActions.RoleGranted,
            user.Id, "нет доступа", scopeName is null ? role.Name : $"{role.Name} ({scopeName})", cmd.Reason));
        if (token is not null)
        {
            db.AuditEntries.Add(Audit(ctx, AuditActions.InvitationIssued, user.Id, null, $"до {user.SetupTokenExpiresAtUtc:yyyy-MM-dd HH:mm} UTC", null));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new InvitationResult(user.Id, token, user.SetupTokenExpiresAtUtc);
    }

    /// <summary>Новая ссылка приглашения, если старая истекла или потерялась. Старая перестаёт действовать.</summary>
    public async Task<InvitationResult> ReissueInvitationAsync(long userId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        await RequireMemberAsync(ctx, userId, ct);
        await EnsureAccountActionAllowedAsync(ctx, userId, ct);
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var token = user.IssueSetupToken(clock.UtcNow);
        db.AuditEntries.Add(Audit(ctx, AuditActions.InvitationIssued, userId, null, $"до {user.SetupTokenExpiresAtUtc:yyyy-MM-dd HH:mm} UTC", "повторная ссылка"));
        await db.SaveChangesAsync(ct);
        return new InvitationResult(userId, token, user.SetupTokenExpiresAtUtc);
    }

    /// <summary>
    /// Ссылка смены пароля, если пользователь его забыл, а почта на сервере не настроена (D81). Действует 24 часа и
    /// передаётся лично; прежний пароль работает, пока не задан новый; 2FA остаётся. Правила — как у сброса 2FA:
    /// не себе, причина обязательна, для Владельца и Администратора — только Владелец.
    /// </summary>
    public async Task<InvitationResult> IssuePasswordResetAsync(long userId, string? reason, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        await RequireMemberAsync(ctx, userId, ct);
        if (userId == ctx.UserId)
        {
            throw new BusinessRuleException("access.password_reset.self", "Свой пароль меняйте через «Забыли пароль» или у другого администратора.");
        }

        GrantPolicy.EnsureReason(required: true, reason);
        await EnsureAccountActionAllowedAsync(ctx, userId, ct);
        stepUp.Demand(ctx); // D123

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var token = user.IssuePasswordResetToken(clock.UtcNow, SignInPolicy.AdminPasswordResetLifetime);
        db.AuditEntries.Add(Audit(ctx, AuditActions.PasswordResetIssued, userId, null, $"до {user.SetupTokenExpiresAtUtc:yyyy-MM-dd HH:mm} UTC", reason));
        await db.SaveChangesAsync(ct);
        return new InvitationResult(userId, token, user.SetupTokenExpiresAtUtc);
    }

    /// <summary>
    /// Сброс двухфакторной аутентификации, если пользователь потерял телефон. Сессии пользователя закрываются,
    /// при следующем входе он подключит аутентификатор заново. Сброс у Владельца или Администратора — только Владелец.
    /// </summary>
    public async Task ResetTwoFactorAsync(long userId, string? reason, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        await RequireMemberAsync(ctx, userId, ct);
        if (userId == ctx.UserId)
        {
            throw new BusinessRuleException("access.2fa_reset.self", "Свою двухфакторную аутентификацию сбрасывает другой администратор.");
        }

        GrantPolicy.EnsureReason(required: true, reason);
        await EnsureAccountActionAllowedAsync(ctx, userId, ct);
        stepUp.Demand(ctx); // D123

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        user.ResetTwoFactor();
        db.RecoveryCodes.RemoveRange(await db.RecoveryCodes.Where(r => r.UserId == userId).ToListAsync(ct));
        db.AuditEntries.Add(Audit(ctx, AuditActions.TwoFactorReset, userId, "включена", "сброшена", reason));
        await db.SaveChangesAsync(ct);
    }

    public async Task ChangeRoleAsync(ChangeRoleCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        // Аудит 10.10.2026 (п. 3): проверка «останется Владелец» и снятие роли — под одной блокировкой организации,
        // иначе два Владельца, параллельно снимающие роли друг с друга, оставят организацию без Владельца.
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync(AccessLock(ctx.OrganizationId), ct);
        await RequireMemberAsync(ctx, cmd.UserId, ct);
        var newRole = await FindRoleAsync(ctx, cmd.NewRoleCode, ct);
        var now = clock.UtcNow;

        var current = await db.RoleAssignments
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.UserId == cmd.UserId && a.RoleId != null
                        && a.RevokedAtUtc == null && (a.ValidToUtc == null || a.ValidToUtc > now))
            .ToListAsync(ct);
        var currentRoleIds = current.Select(a => a.RoleId!.Value).ToList();
        var currentRoles = await db.Roles.Where(r => currentRoleIds.Contains(r.Id)).ToListAsync(ct);

        var scopeName = await ResolveScopeAsync(ctx, cmd.DepartmentId, cmd.WarehouseId, ct);

        if (currentRoles.Count == 1 && currentRoles[0].Id == newRole.Id && current.Count == 1
            && current[0].DepartmentId == cmd.DepartmentId && current[0].WarehouseId == cmd.WarehouseId)
        {
            throw new BusinessRuleException("access.role.unchanged", "Роль не изменилась.");
        }

        // Снять административную роль — тоже управление административными правами.
        var touchesAdmin = newRole.IsAdministrative || currentRoles.Any(r => r.IsAdministrative);
        GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, touchesAdmin);
        await EnsureMayGrantRoleAsync(ctx, newRole, cmd.Reason, ct);

        if (currentRoles.Any(r => r.Code == SystemRoles.Owner) && newRole.Code != SystemRoles.Owner)
        {
            var owners = await CountActiveOwnersAsync(ctx.OrganizationId, excludeUserId: cmd.UserId, ct);
            GrantPolicy.EnsureOwnerRemains(owners);
        }

        if (touchesAdmin)
        {
            stepUp.Demand(ctx); // D123: выдача и снятие Владельца и Администратора
        }

        foreach (var a in current)
        {
            a.Revoke(ctx.UserId, now);
        }

        db.RoleAssignments.Add(RoleAssignment.ForRole(ctx.OrganizationId, cmd.UserId, newRole.Id, ctx.UserId, now, cmd.Reason,
            departmentId: cmd.DepartmentId, warehouseId: cmd.WarehouseId));
        var user = await db.Users.SingleAsync(u => u.Id == cmd.UserId, ct);
        user.RotateSecurityStamp();

        db.AuditEntries.Add(Audit(ctx, newRole.IsAdministrative ? AuditActions.AdminRoleGranted : AuditActions.RoleGranted,
            cmd.UserId, string.Join(", ", currentRoles.Select(r => r.Name)),
            scopeName is null ? newRole.Name : $"{newRole.Name} ({scopeName})", cmd.Reason));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Блокировка состава ролей организации до конца транзакции.</summary>
    private static string AccessLock(long organizationId) => $"kniterp.access.{organizationId}";

    public Task BlockAsync(long userId, string? reason, CancellationToken ct = default) => BlockCoreAsync(userId, reason, dismissal: false, ct);

    /// <summary>
    /// D123: блокировка уволенного в приказе или при увольнении из карточки. Это отнимает доступ, поэтому повторное подтверждение
    /// нужно, только если увольняют Владельца или Администратора; прямая блокировка (BlockAsync) подтверждается всегда.
    /// </summary>
    internal Task BlockOnDismissalAsync(long userId, string? reason, CancellationToken ct) => BlockCoreAsync(userId, reason, dismissal: true, ct);

    private async Task BlockCoreAsync(long userId, string? reason, bool dismissal, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync(AccessLock(ctx.OrganizationId), ct);
        var member = await RequireMemberAsync(ctx, userId, ct);
        if (userId == ctx.UserId)
        {
            throw new BusinessRuleException("access.block.self", "Нельзя заблокировать самого себя.");
        }

        var isOwner = await HasActiveRoleAsync(ctx.OrganizationId, userId, SystemRoles.Owner, ct);
        if (isOwner)
        {
            GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, isAdministrative: true);
            GrantPolicy.EnsureOwnerRemains(await CountActiveOwnersAsync(ctx.OrganizationId, excludeUserId: userId, ct));
        }

        if (!dismissal || await IsAdministratorHereAsync(ctx, userId, ct))
        {
            stepUp.Demand(ctx); // D123
        }

        var now = clock.UtcNow;
        member.Block(ctx.UserId, now);
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        user.RotateSecurityStamp();
        db.AuditEntries.Add(Audit(ctx, AuditActions.UserBlocked, userId, "активен", "заблокирован", reason));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task UnblockAsync(long userId, string? reason, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        var member = await RequireMemberAsync(ctx, userId, ct);
        // Аудит 11.10.2026 (D109): разблокировать Владельца или Администратора может только Владелец — как и заблокировать.
        GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, await IsAdministratorHereAsync(ctx, userId, ct));
        stepUp.Demand(ctx); // D123
        member.Unblock();
        db.AuditEntries.Add(Audit(ctx, AuditActions.UserUnblocked, userId, "заблокирован", "активен", reason));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// D112: итоговые права пользователя с источником — роль, особое право или запрет — и действующие особые права и запреты.
    /// </summary>
    public async Task<UserRightsDto> UserRightsAsync(long userId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserView, ct);
        await RequireMemberAsync(ctx, userId, ct);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        var effective = await guard.LoadAsync(userId, ctx.OrganizationId, ct);
        var now = clock.UtcNow;
        var assignments = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.UserId == userId && a.RevokedAtUtc == null && (a.ValidToUtc == null || a.ValidToUtc > now))
            .ToListAsync(ct);
        var roleIds = assignments.Where(a => a.RoleId != null).Select(a => a.RoleId!.Value).ToList();
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
        var grantorIds = assignments.Select(a => a.GrantedByUserId).Distinct().ToList();
        var grantors = await db.Users.AsNoTracking().Where(u => grantorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var individual = assignments.Where(a => a.PermissionCode != null)
            .OrderBy(a => a.GrantedAtUtc)
            .Select(a => new IndividualRightDto(a.Id, a.PermissionCode!, Permissions.Describe(a.PermissionCode!), a.IsDeny, a.ValidToUtc, a.Reason,
                grantors.GetValueOrDefault(a.GrantedByUserId, "?"), a.GrantedAtUtc))
            .ToList();
        var rows = Permissions.All.OrderBy(code => Permissions.Groups.ToList().IndexOf(Permissions.GroupOf(code))).Select(code =>
        {
            var fromRoles = roles.Where(r => r.Permissions.Any(p => p.PermissionCode == code && p.Level.Grants())).Select(r => r.Name).ToList();
            return new UserRightRowDto(code, Permissions.Describe(code), Permissions.GroupOf(code), effective.Has(code),
                fromRoles.Count == 0 ? null : string.Join(", ", fromRoles),
                individual.Any(i => i.PermissionCode == code && !i.Deny), individual.Any(i => i.PermissionCode == code && i.Deny));
        }).ToList();
        var active = user.Status == UserStatus.Active && await db.OrganizationMembers.AnyAsync(
            m => m.OrganizationId == ctx.OrganizationId && m.UserId == userId && m.Status == MembershipStatus.Active, ct);
        return new UserRightsDto(userId, user.DisplayName, OrderRoles(roles).Select(r => r.Name).ToList(), rows, individual,
            userId != ctx.UserId && ctx.Permissions.Has(Permissions.UserManage), active);
    }

    /// <summary>
    /// D112: особое право («разрешить») или запрет поверх роли — со сроком и причиной. Нельзя себе; нельзя выдать право выше своего;
    /// административное право, а также любые особые права Владельцу и Администратору — только Владелец; Владельца ограничить нельзя.
    /// Запрет требует причину и сильнее любого разрешения.
    /// </summary>
    public async Task<Guid> GrantIndividualAsync(long userId, string permissionCode, bool deny, DateOnly? validTo, string? reason, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync(AccessLock(ctx.OrganizationId), ct);
        await RequireMemberAsync(ctx, userId, ct);
        if (userId == ctx.UserId)
        {
            throw new BusinessRuleException("access.individual.self", "Свои права меняет другой администратор или Владелец.");
        }

        GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, Permissions.IsAdministrative(permissionCode) || await IsAdministratorHereAsync(ctx, userId, ct));
        if (deny && await HasActiveRoleAsync(ctx.OrganizationId, userId, SystemRoles.Owner, ct))
        {
            throw new BusinessRuleException("access.individual.owner", "Права Владельца не ограничиваются запретами.");
        }

        if (!deny)
        {
            GrantPolicy.EnsureNoEscalation(ctx.Permissions, [(permissionCode, PermissionLevel.Full)]);
        }

        var now = clock.UtcNow;
        DateTime? until = null;
        if (validTo is { } day)
        {
            var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
            until = TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeZoneInfo.FindSystemTimeZoneById(tz));
        }

        if (await db.RoleAssignments.AnyAsync(a => a.OrganizationId == ctx.OrganizationId && a.UserId == userId && a.PermissionCode == permissionCode
                                                   && a.IsDeny == deny && a.RevokedAtUtc == null && (a.ValidToUtc == null || a.ValidToUtc > now), ct))
        {
            throw new BusinessRuleException("access.individual.exists", deny ? "Такой запрет уже действует." : "Такое право уже выдано.");
        }

        var assignment = RoleAssignment.ForPermission(ctx.OrganizationId, userId, permissionCode, deny, ctx.UserId, now, reason, until);
        if (Permissions.IsAdministrative(permissionCode))
        {
            stepUp.Demand(ctx); // D123: разрешение или запрет управления пользователями
        }

        db.RoleAssignments.Add(assignment);
        // Права читаются из базы на каждый запрос — действуют сразу, без выхода пользователя из программы.
        db.AuditEntries.Add(Audit(ctx, deny ? AuditActions.PermissionDenied : AuditActions.PermissionGranted, userId, null,
            $"{Permissions.Describe(permissionCode)}{(validTo is { } v ? $" до {v:dd.MM.yyyy}" : "")}", reason));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return assignment.Id;
    }

    /// <summary>D112: отозвать особое право или запрет. Те же правила, что при выдаче.</summary>
    public async Task RevokeIndividualAsync(Guid assignmentId, string? reason, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync(AccessLock(ctx.OrganizationId), ct);
        var a = await db.RoleAssignments.SingleOrDefaultAsync(x => x.Id == assignmentId && x.OrganizationId == ctx.OrganizationId && x.PermissionCode != null, ct)
                ?? throw new NotFoundException("Особое право");
        if (a.UserId == ctx.UserId)
        {
            throw new BusinessRuleException("access.individual.self", "Свои права меняет другой администратор или Владелец.");
        }

        GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, Permissions.IsAdministrative(a.PermissionCode!) || await IsAdministratorHereAsync(ctx, a.UserId, ct));
        // Снять запрет — значит вернуть право: тоже не выше своего.
        if (a.IsDeny)
        {
            GrantPolicy.EnsureNoEscalation(ctx.Permissions, [(a.PermissionCode!, PermissionLevel.Full)]);
        }

        if (Permissions.IsAdministrative(a.PermissionCode!))
        {
            stepUp.Demand(ctx); // D123
        }

        a.Revoke(ctx.UserId, clock.UtcNow);
        db.AuditEntries.Add(Audit(ctx, AuditActions.AssignmentRevoked, a.UserId, $"{(a.IsDeny ? "запрет" : "право")}: {Permissions.Describe(a.PermissionCode!)}",
            "отозвано", reason));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<RoleMatrixDto> GetRoleMatrixAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserView, ct);
        var roles = await db.Roles.AsNoTracking()
            .Where(r => r.OrganizationId == ctx.OrganizationId && !r.IsArchived)
            .Include(r => r.Permissions)
            .ToListAsync(ct);

        roles = OrderRoles(roles);

        var rows = RoleMatrixP0.PermissionCodes.Select(code => new MatrixRowDto(
            code,
            Permissions.Describe(code),
            roles.Select(r => r.Permissions.FirstOrDefault(p => p.PermissionCode == code)?.Level ?? PermissionLevel.None).ToList()))
            .ToList();

        return new RoleMatrixDto(roles.Select(r => new RoleColumnDto(r.Id, r.Code, r.Name)).ToList(), rows);
    }

    private async Task EnsureMayGrantRoleAsync(AccessContext ctx, Role role, string? reason, CancellationToken ct)
    {
        try
        {
            GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, role.IsAdministrative);
            GrantPolicy.EnsureNoEscalation(ctx.Permissions, role.Permissions.Select(p => (p.PermissionCode, p.Level)));
        }
        catch (BusinessRuleException ex)
        {
            db.AuditEntries.Add(Audit(ctx, AuditActions.AccessDenied, null, null, ex.Code, role.Code));
            await db.SaveChangesAsync(ct);
            throw;
        }

        GrantPolicy.EnsureReason(role.IsAdministrative, reason);
    }

    private async Task<Role> FindRoleAsync(AccessContext ctx, string roleCode, CancellationToken ct) =>
        await db.Roles.Include(r => r.Permissions)
            .SingleOrDefaultAsync(r => r.OrganizationId == ctx.OrganizationId && r.Code == roleCode && !r.IsArchived, ct)
        ?? throw new NotFoundException("Роль");

    /// <summary>
    /// Аудит 11.10.2026 (D109): учётная запись (пароль, 2FA, ссылка входа) общая для всех организаций пользователя. Менять её
    /// отсюда можно, только если пользователь не состоит в другой организации, иначе администратор одной организации получил бы
    /// вход в чужую. Для Владельца и Администратора — только Владелец.
    /// </summary>
    private async Task EnsureAccountActionAllowedAsync(AccessContext ctx, long userId, CancellationToken ct)
    {
        if (await db.OrganizationMembers.AnyAsync(m => m.UserId == userId && m.OrganizationId != ctx.OrganizationId, ct))
        {
            throw new BusinessRuleException("access.account.shared",
                "Пользователь работает и в другой организации: пароль и вход он восстанавливает сам — «Забыли пароль» или аварийная процедура.");
        }

        GrantPolicy.EnsureCanGrantAdministrative(ctx.Permissions, await IsAdministratorHereAsync(ctx, userId, ct));
    }

    private async Task<bool> IsAdministratorHereAsync(AccessContext ctx, long userId, CancellationToken ct) =>
        await HasActiveRoleAsync(ctx.OrganizationId, userId, SystemRoles.Owner, ct)
        || await HasActiveRoleAsync(ctx.OrganizationId, userId, SystemRoles.Administrator, ct);

    /// <summary>Пользователь чужой организации — «не найдено» (ТЗ §4.12).</summary>
    private async Task<OrganizationMember> RequireMemberAsync(AccessContext ctx, long userId, CancellationToken ct) =>
        await db.OrganizationMembers.SingleOrDefaultAsync(m => m.OrganizationId == ctx.OrganizationId && m.UserId == userId, ct)
        ?? throw new NotFoundException("Пользователь");

    private async Task<bool> HasActiveRoleAsync(long organizationId, long userId, string roleCode, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await db.RoleAssignments
            .Where(a => a.OrganizationId == organizationId && a.UserId == userId && a.RevokedAtUtc == null
                        && a.ValidFromUtc <= now && (a.ValidToUtc == null || a.ValidToUtc > now))
            .Join(db.Roles, a => a.RoleId, r => (long?)r.Id, (a, r) => r.Code)
            .AnyAsync(code => code == roleCode, ct);
    }

    private async Task<int> CountActiveOwnersAsync(long organizationId, long excludeUserId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await db.RoleAssignments
            .Where(a => a.OrganizationId == organizationId && a.UserId != excludeUserId && a.RevokedAtUtc == null
                        && a.ValidFromUtc <= now && (a.ValidToUtc == null || a.ValidToUtc > now))
            .Join(db.Roles.Where(r => r.Code == SystemRoles.Owner), a => a.RoleId, r => (long?)r.Id, (a, r) => a.UserId)
            .Join(db.OrganizationMembers.Where(m => m.OrganizationId == organizationId && m.Status == MembershipStatus.Active),
                userId => userId, m => m.UserId, (userId, m) => userId)
            .Join(db.Users.Where(u => u.Status == UserStatus.Active), userId => userId, u => u.Id, (userId, u) => userId)
            .Distinct()
            .CountAsync(ct);
    }

    private AuditEntry Audit(AccessContext ctx, string action, long? targetUserId, string? before, string? after, string? reason) =>
        AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(UserAccount),
            targetUserId?.ToString(), before, after, reason, currentUser.CorrelationId);
}
