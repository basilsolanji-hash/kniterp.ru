using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Structure;

/// <summary>
/// Загрузка и выгрузка сотрудников в Excel для запуска (допущение D57). Строка находит сотрудника по табельному номеру,
/// подразделение и должность — по наименованию. Новые принимаются с датой приёма, у существующих меняются ФИО, подразделение
/// и должность (дата приёма не меняется). Уволенные не меняются. Область — подразделения, где у пользователя право изменения.
/// </summary>
public sealed class EmployeeExchangeService(
    IKnitErpDbContext db, IAccessGuard guard, ISpreadsheetFormat spreadsheet, ICurrentUser currentUser, IClock clock)
{
    public static readonly IReadOnlyList<string> Columns =
        ["Табельный номер", "Фамилия", "Имя", "Отчество", "Подразделение", "Должность", "Дата приёма"];

    public async Task<byte[]> TemplateAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.EmployeeEdit, ct);
        var (departments, positions) = await OptionsAsync(ctx, ct);
        return spreadsheet.Write(
        [
            new SheetData("Сотрудники", Columns,
            [
                ["0001", "Иванова", "Мария", "Петровна", departments.FirstOrDefault()?.Name ?? "Вязальный цех", positions.FirstOrDefault()?.Name ?? "Вязальщица",
                    DateTime.Today.ToString("dd.MM.yyyy"), ],
            ]),
            new SheetData("Допустимые значения", ["Подразделение", "Должность"],
                Enumerable.Range(0, Math.Max(departments.Count, positions.Count)).Select(i => (IReadOnlyList<string>)
                [
                    i < departments.Count ? departments[i].Name : "",
                    i < positions.Count ? positions[i].Name : "",
                ]).ToList()),
        ]);
    }

    public async Task<byte[]> ExportAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.EmployeeView, ct);
        var visible = await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeView, ct);
        var rows = await (
                from e in db.Employees.AsNoTracking()
                join d in db.Departments.AsNoTracking() on e.DepartmentId equals d.Id
                join p in db.Positions.AsNoTracking() on e.PositionId equals p.Id
                where e.OrganizationId == ctx.OrganizationId && e.Status != EmploymentStatus.Dismissed
                orderby e.LastName, e.FirstName
                select new { e.PersonnelNumber, e.LastName, e.FirstName, e.MiddleName, e.DepartmentId, Department = d.Name, Position = p.Name, e.HiredOn })
            .ToListAsync(ct);
        return spreadsheet.Write(
        [
            new SheetData("Сотрудники", Columns, rows.Where(r => visible is null || visible.Contains(r.DepartmentId))
                .Select(r => (IReadOnlyList<string>)
                    [r.PersonnelNumber, r.LastName, r.FirstName, r.MiddleName ?? "", r.Department, r.Position, r.HiredOn.ToString("dd.MM.yyyy")])
                .ToList()),
        ]);
    }

    public async Task<ImportPlan> PreviewAsync(Stream file, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.EmployeeEdit, ct);
        return await PlanAsync(ctx, TableImport.Read(spreadsheet, file, Columns), ct);
    }

    public async Task<ImportResult> ApplyAsync(IReadOnlyList<ImportRow> rows, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.EmployeeEdit, ct);
        TableImport.EnsureApplicable(rows);

        await using var tx = await db.BeginTransactionAsync(ct);
        await Hr.StaffingRules.LockAsync(db, ctx.OrganizationId, ct);
        var plan = await PlanAsync(ctx, rows, ct);
        if (plan.ErrorRows > 0)
        {
            throw TableImport.HasErrors(plan);
        }

        var (departments, positions) = await OptionsAsync(ctx, ct);
        var existing = await db.Employees.Where(e => e.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(e => e.PersonnelNumber, ct);
        var created = new List<Employee>();
        var updated = 0;
        var affected = new List<(long DepartmentId, long PositionId)>();
        foreach (var row in plan.Rows.Where(r => r.Action is ImportAction.Create or ImportAction.Update))
        {
            var department = Find(departments, row.Cell(4)).Single();
            var position = Find(positions, row.Cell(5)).Single();
            var number = row.Cell(0).Trim().ToUpperInvariant();
            if (row.Action == ImportAction.Create)
            {
                var e = Employee.Hire(ctx.OrganizationId, number, row.Cell(1), row.Cell(2), TableImport.Optional(row.Cell(3)),
                    department.Id, position.Id, TableImport.ParseDate(row.Cell(6))!.Value);
                db.Employees.Add(e);
                created.Add(e);
                affected.Add((department.Id, position.Id));
                continue;
            }

            var current = existing[number];
            if (current.DepartmentId != department.Id || current.PositionId != position.Id)
            {
                affected.Add((department.Id, position.Id));
            }

            var before = (Department: departments.FirstOrDefault(d => d.Id == current.DepartmentId)?.Name, Position: positions.FirstOrDefault(p => p.Id == current.PositionId)?.Name);
            foreach (var change in current.Update(number, row.Cell(1), row.Cell(2), TableImport.Optional(row.Cell(3)), department.Id, position.Id))
            {
                var (label, b, a) = change.Field switch
                {
                    nameof(Employee.DepartmentId) => ("Подразделение", before.Department ?? change.Before, department.Name),
                    nameof(Employee.PositionId) => ("Должность", before.Position ?? change.Before, position.Name),
                    _ => ("ФИО", change.Before, change.After),
                };
                Audit(ctx, AuditActions.EmployeeChanged, current.Id.ToString(), b, a, $"Импорт: {label}");
            }

            updated++;
        }

        await db.SaveChangesAsync(ct);

        // D203: импорт не обходит штатное расписание — итоговая занятость проверяется до фиксации, при нарушении откатывается вся загрузка.
        try
        {
            await Hr.StaffingRules.EnsureWithinAsync(db, ctx.OrganizationId, affected, ct);
        }
        catch (BusinessRuleException)
        {
            db.DiscardChanges();
            throw;
        }

        foreach (var e in created)
        {
            Audit(ctx, AuditActions.EmployeeHired, e.Id.ToString(), null, $"{e.PersonnelNumber} {e.FullName}", "Импорт");
        }

        Audit(ctx, AuditActions.EmployeesImported, null, null,
            $"строк {plan.Total}: принято {created.Count}, обновлено {updated}, без изменений {plan.Unchanged}", null);
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
        return new ImportResult(created.Count, updated);
    }

    private async Task<ImportPlan> PlanAsync(AccessContext ctx, IReadOnlyList<ImportRow> raw, CancellationToken ct)
    {
        var (departments, positions) = await OptionsAsync(ctx, ct);
        var visible = await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeEdit, ct);
        var numbers = raw.Select(r => r.Cell(0).Trim().ToUpperInvariant()).Where(n => n.Length > 0).Distinct().ToList();
        var existing = await db.Employees.AsNoTracking()
            .Where(e => e.OrganizationId == ctx.OrganizationId && numbers.Contains(e.PersonnelNumber))
            .ToDictionaryAsync(e => e.PersonnelNumber, ct);
        var duplicates = TableImport.Duplicates(raw.Select(r => (r.Cell(0).Trim().ToUpperInvariant(), r.RowNumber)));

        var rows = new List<ImportRow>(raw.Count);
        foreach (var r in raw)
        {
            var errors = new List<string>();
            var number = r.Cell(0).Trim().ToUpperInvariant();
            if (duplicates.TryGetValue(number, out var same))
            {
                errors.Add($"Табельный номер {number} повторяется в строках {string.Join(", ", same)}.");
            }

            var department = Resolve(departments, r.Cell(4), "Подразделение", errors);
            var position = Resolve(positions, r.Cell(5), "Должность", errors);
            var hiredOn = TableImport.ParseDate(r.Cell(6));
            existing.TryGetValue(number, out var current);
            if (current is null && hiredOn is null)
            {
                errors.Add(r.Cell(6).Length == 0 ? "Не указана дата приёма." : $"Дата приёма «{r.Cell(6)}» не распознана: пишите ДД.ММ.ГГГГ.");
            }

            var action = ImportAction.Error;
            if (department is not null && position is not null && (current is not null || hiredOn is not null))
            {
                try
                {
                    // Те же правила, что при ручном вводе: проверка через доменную сущность.
                    var candidate = Employee.Hire(ctx.OrganizationId, number, r.Cell(1), r.Cell(2), TableImport.Optional(r.Cell(3)),
                        department.Id, position.Id, hiredOn ?? DateOnly.MinValue);
                    if (current is null)
                    {
                        action = ImportAction.Create;
                    }
                    else if (current.Status == EmploymentStatus.Dismissed)
                    {
                        errors.Add($"Табельный номер {number} у уволенного сотрудника «{current.FullName}»: карточка закрыта. Уберите строку или смените номер.");
                    }
                    else if (visible is not null && !visible.Contains(current.DepartmentId))
                    {
                        errors.Add($"Табельный номер {number} занят сотрудником другого подразделения.");
                    }
                    else
                    {
                        action = current.FullName == candidate.FullName && current.DepartmentId == candidate.DepartmentId
                                 && current.PositionId == candidate.PositionId
                            ? ImportAction.Unchanged
                            : ImportAction.Update;
                    }
                }
                catch (BusinessRuleException ex)
                {
                    errors.Add(ex.Message);
                }
            }

            rows.Add(r with { Action = errors.Count > 0 ? ImportAction.Error : action, Errors = errors });
        }

        return new ImportPlan(Columns, rows);
    }

    /// <summary>Действующие подразделения области пользователя и действующие должности.</summary>
    private async Task<(List<LookupDto> Departments, List<LookupDto> Positions)> OptionsAsync(AccessContext ctx, CancellationToken ct)
    {
        var visible = await StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeEdit, ct);
        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.OrganizationId == ctx.OrganizationId && !d.IsArchived)
            .OrderBy(d => d.Name).Select(d => new LookupDto(d.Id, d.Name)).ToListAsync(ct);
        var positions = await db.Positions.AsNoTracking()
            .Where(p => p.OrganizationId == ctx.OrganizationId && !p.IsArchived)
            .OrderBy(p => p.Name).Select(p => new LookupDto(p.Id, p.Name)).ToListAsync(ct);
        return (departments.Where(d => visible is null || visible.Contains(d.Id)).ToList(), positions);
    }

    private static List<LookupDto> Find(IEnumerable<LookupDto> list, string name) =>
        list.Where(x => string.Equals(x.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    private static LookupDto? Resolve(IEnumerable<LookupDto> list, string name, string what, List<string> errors)
    {
        if (name.Length == 0)
        {
            errors.Add($"Не указано: {what.ToLowerInvariant()}.");
            return null;
        }

        var found = Find(list, name);
        switch (found.Count)
        {
            case 1:
                return found[0];
            case 0:
                errors.Add($"{what}: «{name}» нет среди действующих" + (what == "Подразделение" ? " в вашей области." : "."));
                return null;
            default:
                errors.Add($"{what}: «{name}» встречается несколько раз — переименуйте в разделе «Структура», чтобы различать.");
                return null;
        }
    }

    private void Audit(AccessContext ctx, string action, string? id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(Employee), id,
            before, after, reason, currentUser.CorrelationId));
}
