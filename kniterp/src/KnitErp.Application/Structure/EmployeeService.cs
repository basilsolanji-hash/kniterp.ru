using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Structure;

public sealed record EmployeeRowDto(
    long Id,
    string PersonnelNumber,
    string LastName,
    string FirstName,
    string? MiddleName,
    string FullName,
    long DepartmentId,
    string DepartmentName,
    long PositionId,
    string PositionName,
    EmploymentStatus Status,
    DateOnly HiredOn,
    DateOnly? DismissedOn,
    long? UserId,
    string? UserEmail,
    byte[] RowVersion,
    DateOnly? BirthDate = null,
    bool ShareBirthday = false)
{
    public string StatusName => Employee.StatusName(Status);
}

public sealed record EmployeeListDto(IReadOnlyList<EmployeeRowDto> Employees, bool CanEdit, bool OwnOnly, bool IsScoped);

public sealed record EmployeeFilter(long? DepartmentId = null, bool IncludeDismissed = false, string? Search = null);

public sealed record EmployeeCommand(
    string PersonnelNumber, string LastName, string FirstName, string? MiddleName, long DepartmentId, long PositionId, DateOnly HiredOn);

public sealed record LookupDto(long Id, string Name);

public sealed record EmployeeOptionsDto(IReadOnlyList<LookupDto> Departments, IReadOnlyList<LookupDto> Positions);

/// <summary>Сотрудники организации. Область данных: вся организация, свои подразделения или только своя карточка.</summary>
public sealed class EmployeeService(
    IKnitErpDbContext db, IAccessGuard guard, UserAccessService userAccess, ICurrentUser currentUser, IClock clock)
{
    public async Task<EmployeeListDto> ListAsync(EmployeeFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var ownOnly = !ctx.Permissions.Has(Permissions.EmployeeView);
        if (ownOnly && !ctx.Permissions.HasOwnOnly(Permissions.EmployeeView))
        {
            await guard.DenyAsync(ctx, Permissions.EmployeeView, ct);
        }

        var visible = ownOnly ? null : await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeView, ct);
        var query = db.Employees.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId);
        if (ownOnly)
        {
            query = query.Where(e => e.UserId == ctx.UserId);
        }
        else if (visible is not null)
        {
            query = query.Where(e => visible.Contains(e.DepartmentId));
        }

        if (filter.DepartmentId is { } dep)
        {
            query = query.Where(e => e.DepartmentId == dep);
        }

        if (!filter.IncludeDismissed)
        {
            query = query.Where(e => e.Status != EmploymentStatus.Dismissed);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim();
            query = query.Where(e => e.LastName.Contains(s) || e.FirstName.Contains(s) || e.PersonnelNumber.Contains(s));
        }

        var rows = await Project(query.OrderBy(e => e.LastName).ThenBy(e => e.FirstName)).ToListAsync(ct);
        return new EmployeeListDto(rows, ctx.Permissions.Has(Permissions.EmployeeEdit), ownOnly, visible is not null);
    }

    /// <summary>Карточка сотрудника: в области просмотра пользователя или своя; иначе «не найдено».</summary>
    public async Task<EmployeeRowDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var query = db.Employees.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && e.Id == id);
        if (ctx.Permissions.Has(Permissions.EmployeeView))
        {
            var visible = await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeView, ct);
            if (visible is not null)
            {
                query = query.Where(e => visible.Contains(e.DepartmentId));
            }
        }
        else if (ctx.Permissions.HasOwnOnly(Permissions.EmployeeView))
        {
            query = query.Where(e => e.UserId == ctx.UserId);
        }
        else
        {
            await guard.DenyAsync(ctx, Permissions.EmployeeView, ct);
        }

        return await Project(query).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Сотрудник");
    }

    /// <summary>Действующие подразделения и должности для формы сотрудника.</summary>
    public async Task<EmployeeOptionsDto> GetOptionsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.EmployeeEdit, ct);
        var visible = await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeEdit, ct);
        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && !d.IsArchived)
            .OrderBy(d => d.Name).Select(d => new LookupDto(d.Id, d.Name)).ToListAsync(ct);
        var positions = await db.Positions.AsNoTracking()
            .Where(p => p.OrganizationId == ctx.OrganizationId && !p.IsArchived)
            .OrderBy(p => p.Name).Select(p => new LookupDto(p.Id, p.Name)).ToListAsync(ct);
        return new EmployeeOptionsDto(departments.Where(d => visible is null || visible.Contains(d.Id)).ToList(), positions);
    }

    /// <summary>Сотрудники без учётной записи — для формы приглашения пользователя.</summary>
    public async Task<IReadOnlyList<LookupDto>> ListWithoutAccountAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        return await db.Employees.AsNoTracking()
            .Where(e => e.OrganizationId == ctx.OrganizationId && e.UserId == null && e.Status != EmploymentStatus.Dismissed)
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .Select(e => new LookupDto(e.Id, e.MiddleName == null ? e.LastName + " " + e.FirstName : e.LastName + " " + e.FirstName + " " + e.MiddleName))
            .ToListAsync(ct);
    }

    public async Task<long> HireAsync(EmployeeCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.EmployeeEdit, ct);
        await RequireAssignableAsync(ctx, cmd.DepartmentId, cmd.PositionId, ct);
        var employee = Employee.Hire(ctx.OrganizationId, cmd.PersonnelNumber, cmd.LastName, cmd.FirstName, cmd.MiddleName,
            cmd.DepartmentId, cmd.PositionId, cmd.HiredOn);
        await EnsureNumberFreeAsync(ctx, employee.PersonnelNumber, null, ct);
        db.Employees.Add(employee);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.EmployeeHired, employee.Id, null, $"{employee.PersonnelNumber} {employee.FullName}", null);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return employee.Id;
    }

    public async Task UpdateAsync(long id, EmployeeCommand cmd, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.EmployeeEdit, ct);
        var employee = await LoadForEditAsync(ctx, id, rowVersion, ct);
        await RequireAssignableAsync(ctx, cmd.DepartmentId, cmd.PositionId, ct);
        var names = await NamesAsync(ctx, employee.DepartmentId, employee.PositionId, cmd.DepartmentId, cmd.PositionId, ct);
        var changes = employee.Update(cmd.PersonnelNumber, cmd.LastName, cmd.FirstName, cmd.MiddleName, cmd.DepartmentId, cmd.PositionId);
        await EnsureNumberFreeAsync(ctx, employee.PersonnelNumber, id, ct);
        foreach (var c in changes)
        {
            Audit(ctx, AuditActions.EmployeeChanged, id, names.GetValueOrDefault(c.Field + ":" + c.Before) ?? c.Before,
                names.GetValueOrDefault(c.Field + ":" + c.After) ?? c.After, FieldLabel(c.Field));
        }

        await SaveAsync(ct);
    }

    /// <summary>Дата рождения и согласие показывать день рождения коллегам (D83). Согласие даёт сам сотрудник — кадровик его отмечает.</summary>
    /// <returns>Новая версия карточки — для следующего сохранения той же формы.</returns>
    public async Task<byte[]> SetBirthdayAsync(long id, DateOnly? birthDate, bool share, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.EmployeeEdit, ct);
        var employee = await LoadForEditAsync(ctx, id, rowVersion, ct);
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz));
        if (employee.SetBirthday(birthDate, share, today) is { } change)
        {
            // В журнал — факт изменения без самой даты: журнал читают шире, чем карточку сотрудника.
            Audit(ctx, AuditActions.EmployeeChanged, id, null, share ? "указана, видна коллегам" : birthDate is null ? "не указана" : "указана",
                change.Field);
        }

        await SaveAsync(ct);
        return employee.RowVersion;
    }

    public async Task SetOnLeaveAsync(long id, bool onLeave, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.EmployeeEdit, ct);
        var employee = await LoadForEditAsync(ctx, id, rowVersion, ct);
        var change = employee.SetOnLeave(onLeave);
        Audit(ctx, AuditActions.EmployeeChanged, id, change.Before, change.After, "Статус занятости");
        await SaveAsync(ct);
    }

    /// <summary>
    /// Увольнение. Если у сотрудника есть учётная запись, её доступ к организации блокируется в той же транзакции:
    /// уволенный не должен продолжать работать в системе. Для этого нужно и право управления пользователями.
    /// </summary>
    public async Task DismissAsync(long id, DateOnly dismissedOn, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.EmployeeEdit, ct);
        GrantPolicy.EnsureReason(required: true, reason);
        var employee = await LoadForEditAsync(ctx, id, rowVersion, ct);

        await using var tx = await db.BeginTransactionAsync(ct);
        if (employee.UserId is { } userId
            && await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.UserId == userId
                                                          && m.Status == MembershipStatus.Active, ct))
        {
            await userAccess.BlockAsync(userId, $"Увольнение: {reason}", ct);
        }

        var change = employee.Dismiss(dismissedOn);
        Audit(ctx, AuditActions.EmployeeDismissed, id, change.Before, change.After, reason);
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Проекция — последней: фильтры и сортировка остаются над сущностью и переводятся в SQL.</summary>
    private IQueryable<EmployeeRowDto> Project(IQueryable<Employee> employees) =>
        from e in employees
        join d in db.Departments.AsNoTracking() on e.DepartmentId equals d.Id
        join p in db.Positions.AsNoTracking() on e.PositionId equals p.Id
        join u in db.Users.AsNoTracking() on e.UserId equals u.Id into users
        from u in users.DefaultIfEmpty()
        select new EmployeeRowDto(e.Id, e.PersonnelNumber, e.LastName, e.FirstName, e.MiddleName,
            e.MiddleName == null ? e.LastName + " " + e.FirstName : e.LastName + " " + e.FirstName + " " + e.MiddleName,
            e.DepartmentId, d.Name, e.PositionId, p.Name, e.Status, e.HiredOn, e.DismissedOn, e.UserId,
            u == null ? null : u.Email, e.RowVersion, e.BirthDate, e.ShareBirthday);

    /// <summary>Сотрудник своей организации и своей области; иначе «не найдено», как для чужих данных.</summary>
    private async Task<Employee> LoadForEditAsync(AccessContext ctx, long id, byte[] rowVersion, CancellationToken ct)
    {
        var employee = await db.Employees.SingleOrDefaultAsync(e => e.Id == id && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        var visible = await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeEdit, ct);
        if (visible is not null && !visible.Contains(employee.DepartmentId))
        {
            throw new NotFoundException("Сотрудник");
        }

        return employee.RowVersion.AsSpan().SequenceEqual(rowVersion) ? employee : throw new ConcurrencyConflictException();
    }

    private async Task RequireAssignableAsync(AccessContext ctx, long departmentId, long positionId, CancellationToken ct)
    {
        var department = await db.Departments.AsNoTracking()
                             .SingleOrDefaultAsync(d => d.Id == departmentId && d.OrganizationId == ctx.OrganizationId, ct)
                         ?? throw new NotFoundException("Подразделение");
        if (department.IsArchived)
        {
            throw new BusinessRuleException("structure.department.archived", "Подразделение в архиве.");
        }

        var visible = await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeEdit, ct);
        if (visible is not null && !visible.Contains(departmentId))
        {
            throw new NotFoundException("Подразделение");
        }

        var position = await db.Positions.AsNoTracking()
                           .SingleOrDefaultAsync(p => p.Id == positionId && p.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Должность");
        if (position.IsArchived)
        {
            throw new BusinessRuleException("structure.position.archived", "Должность в архиве.");
        }
    }

    private async Task EnsureNumberFreeAsync(AccessContext ctx, string number, long? exceptId, CancellationToken ct)
    {
        if (await db.Employees.AnyAsync(e => e.OrganizationId == ctx.OrganizationId && e.PersonnelNumber == number && e.Id != exceptId, ct))
        {
            throw new BusinessRuleException("hr.employee.number_taken", $"Табельный номер {number} уже занят.");
        }
    }

    /// <summary>Названия подразделений и должностей для журнала: «Цех 1 → Цех 2», а не номера.</summary>
    private async Task<Dictionary<string, string>> NamesAsync(
        AccessContext ctx, long oldDep, long oldPos, long newDep, long newPos, CancellationToken ct)
    {
        var deps = await db.Departments.AsNoTracking().Where(d => d.OrganizationId == ctx.OrganizationId && (d.Id == oldDep || d.Id == newDep))
            .ToDictionaryAsync(d => $"{nameof(Employee.DepartmentId)}:{d.Id}", d => d.Name, ct);
        var poss = await db.Positions.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && (p.Id == oldPos || p.Id == newPos))
            .ToDictionaryAsync(p => $"{nameof(Employee.PositionId)}:{p.Id}", p => p.Name, ct);
        return deps.Concat(poss).ToDictionary(x => x.Key, x => x.Value);
    }

    private static string FieldLabel(string field) => field switch
    {
        nameof(Employee.PersonnelNumber) => "Табельный номер",
        nameof(Employee.FullName) => "ФИО",
        nameof(Employee.DepartmentId) => "Перевод в подразделение",
        nameof(Employee.PositionId) => "Должность",
        _ => field,
    };

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException();
        }
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(Employee), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
