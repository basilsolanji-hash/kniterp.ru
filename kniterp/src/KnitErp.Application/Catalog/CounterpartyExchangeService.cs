using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

/// <summary>
/// Загрузка и выгрузка контрагентов в Excel для перевода справочника поставщиков при запуске (допущение D57).
/// Строка находит существующего контрагента по ИНН и КПП, а без ИНН — по наименованию; иначе создаёт нового.
/// Права — как у импорта номенклатуры (D34). Всё или ничего.
/// </summary>
public sealed class CounterpartyExchangeService(
    IKnitErpDbContext db, IAccessGuard guard, ISpreadsheetFormat spreadsheet, ICurrentUser currentUser, IClock clock)
{
    /// <summary>«Страна» — необязательный последний столбец (D60): пусто — страна организации.</summary>
    public static readonly IReadOnlyList<string> Columns = ["Наименование", "ИНН", "КПП", "Поставщик", "Покупатель", "Комментарий", "Страна"];

    private const int RequiredColumns = 6;

    public async Task<byte[]> TemplateAsync(CancellationToken ct = default)
    {
        await guard.DemandAsync(Permissions.CatalogImport, ct);
        return spreadsheet.Write(
        [
            new SheetData("Контрагенты", Columns,
            [
                ["ООО «Пример пряжи»", "7700000016", "770001001", "да", "нет", "Пример — удалите строку", "Россия"],
                ["ИП Пример", "", "", "да", "", "ИНН можно не указывать", ""],
            ]),
            new SheetData("Подсказка", ["Столбец", "Что писать"],
            [
                ["ИНН", "10 цифр у организации, 12 у ИП; можно пусто"],
                ["КПП", "9 символов, только у организации"],
                ["Поставщик, Покупатель", "да / нет (хотя бы одно «да»)"],
                ["Страна", "Россия, Узбекистан, Казахстан, Беларусь или код RU, UZ, KZ, BY; пусто — страна организации"],
                ["ИНН другой страны", "Узбекистан — СТИР 9 цифр (ПИНФЛ 14), Казахстан — БИН/ИИН 12, Беларусь — УНП 9; КПП только у России"],
            ]),
        ]);
    }

    public async Task<byte[]> ExportAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var rows = await db.Counterparties.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Name, c.Inn, c.Kpp, c.IsSupplier, c.IsCustomer, c.Comment, c.CountryCode })
            .ToListAsync(ct);
        return spreadsheet.Write(
        [
            new SheetData("Контрагенты", Columns, rows.Select(r => (IReadOnlyList<string>)
                [r.Name, r.Inn ?? "", r.Kpp ?? "", r.IsSupplier ? "да" : "нет", r.IsCustomer ? "да" : "нет", r.Comment ?? "",
                    KnitErp.Domain.Organizations.Countries.Get(r.CountryCode).Name]).ToList()),
        ]);
    }

    public async Task<ImportPlan> PreviewAsync(Stream file, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogImport, ct);
        return await PlanAsync(ctx, TableImport.Read(spreadsheet, file, Columns, RequiredColumns), ct);
    }

    public async Task<ImportResult> ApplyAsync(IReadOnlyList<ImportRow> rows, CancellationToken ct = default)
    {
        try
        {
            return await ApplyAsyncCoreAsync(rows, ct);
        }
        catch
        {
            db.DiscardChanges(); // аудит каталога (D347): транзакция откатилась — несохранённое не должно уйти следующим сохранением вкладки
            throw;
        }
    }

    private async Task<ImportResult> ApplyAsyncCoreAsync(IReadOnlyList<ImportRow> rows, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogImport, ct);
        TableImport.EnsureApplicable(rows);

        await using var tx = await db.BeginTransactionAsync(ct);
        var plan = await PlanAsync(ctx, rows, ct);
        if (plan.ErrorRows > 0)
        {
            throw TableImport.HasErrors(plan);
        }

        var existing = await db.Counterparties.Where(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived).ToListAsync(ct);
        var created = new List<Counterparty>();
        var updated = 0;
        foreach (var row in plan.Rows.Where(r => r.Action is ImportAction.Create or ImportAction.Update))
        {
            var v = Values(row)!.Value;
            if (row.Action == ImportAction.Create)
            {
                var c = Counterparty.Create(ctx.OrganizationId, v.Name, v.Inn, v.Kpp, v.Supplier, v.Customer, v.Comment, v.Country);
                db.Counterparties.Add(c);
                created.Add(c);
                continue;
            }

            var current = Match(existing, Key(v.Country, v.Name, v.Inn, v.Kpp))!;
            foreach (var change in current.Update(v.Name, v.Inn, v.Kpp, v.Supplier, v.Customer, v.Comment, v.Country))
            {
                Audit(ctx, AuditActions.CatalogChanged, nameof(Counterparty), current.Id.ToString(), change.Before, change.After,
                    $"Импорт: {change.Field}");
            }

            updated++;
        }

        await db.SaveChangesAsync(ct);
        foreach (var c in created)
        {
            Audit(ctx, AuditActions.CatalogCreated, nameof(Counterparty), c.Id.ToString(), null,
                c.Inn is null ? c.Name : $"{c.Name}, ИНН {c.Inn}", "Импорт");
        }

        Audit(ctx, AuditActions.CatalogImported, "CounterpartyImport", null, null,
            $"контрагенты, строк {plan.Total}: создано {created.Count}, обновлено {updated}, без изменений {plan.Unchanged}", null);
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
        return new ImportResult(created.Count, updated);
    }

    private async Task<ImportPlan> PlanAsync(AccessContext ctx, IReadOnlyList<ImportRow> raw, CancellationToken ct)
    {
        var all = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId).ToListAsync(ct);
        var active = all.Where(c => !c.IsArchived).ToList();
        var archived = all.Where(c => c.IsArchived).ToList();
        // Пустая страна — страна организации; название страны приводится к коду, чтобы применение видело то же самое.
        var orgCountry = await db.Organizations.Where(o => o.Id == ctx.OrganizationId).Select(o => o.CountryCode).SingleAsync(ct);
        raw = raw.Select(r =>
        {
            var cells = Enumerable.Range(0, Columns.Count).Select(r.Cell).ToArray();
            cells[6] = cells[6].Length == 0 ? orgCountry : KnitErp.Domain.Organizations.Countries.Parse(cells[6])?.Code ?? cells[6];
            return r with { Cells = cells };
        }).ToList();
        var duplicates = TableImport.Duplicates(raw.Select(r => (Values(r) is { } v ? Key(v.Country, v.Name, v.Inn, v.Kpp) : "", r.RowNumber)));

        var rows = new List<ImportRow>(raw.Count);
        foreach (var r in raw)
        {
            var errors = new List<string>();
            var action = ImportAction.Error;
            var supplier = TableImport.ParseYesNo(r.Cell(3));
            var customer = TableImport.ParseYesNo(r.Cell(4));
            if (supplier is null)
            {
                errors.Add($"«Поставщик»: напишите «да» или «нет», а не «{r.Cell(3)}».");
            }

            if (customer is null)
            {
                errors.Add($"«Покупатель»: напишите «да» или «нет», а не «{r.Cell(4)}».");
            }

            if (KnitErp.Domain.Organizations.Countries.Find(r.Cell(6)) is null)
            {
                errors.Add($"Страна «{r.Cell(6)}» не поддерживается: Россия, Узбекистан, Казахстан или Беларусь.");
            }

            if (Values(r) is { } v)
            {
                var key = Key(v.Country, v.Name, v.Inn, v.Kpp);
                if (duplicates.TryGetValue(key, out var same))
                {
                    errors.Add(v.Inn is null
                        ? $"Наименование повторяется в строках {string.Join(", ", same)}."
                        : $"ИНН {v.Inn}{(v.Kpp is null ? "" : $" с КПП {v.Kpp}")} повторяется в строках {string.Join(", ", same)}.");
                }

                try
                {
                    // Те же правила, что при ручном вводе: проверка через доменную сущность.
                    var candidate = Counterparty.Create(ctx.OrganizationId, v.Name, v.Inn, v.Kpp, v.Supplier, v.Customer, v.Comment, v.Country);
                    if (Match(active, key) is { } current)
                    {
                        action = current.Name == candidate.Name && current.IsSupplier == candidate.IsSupplier
                                 && current.IsCustomer == candidate.IsCustomer && current.Comment == candidate.Comment
                            ? ImportAction.Unchanged
                            : ImportAction.Update;
                    }
                    else if (Match(archived, key) is { } old)
                    {
                        errors.Add($"Контрагент «{old.Name}» с этими реквизитами в архиве. Верните его из архива или уберите строку.");
                    }
                    else
                    {
                        action = ImportAction.Create;
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

    private static (string Country, string Name, string? Inn, string? Kpp, bool Supplier, bool Customer, string? Comment)? Values(ImportRow r) =>
        TableImport.ParseYesNo(r.Cell(3)) is { } supplier && TableImport.ParseYesNo(r.Cell(4)) is { } customer
        && KnitErp.Domain.Organizations.Countries.Find(r.Cell(6)) is { } country
            ? (country.Code, r.Cell(0), TableImport.Optional(r.Cell(1)), TableImport.Optional(r.Cell(2).ToUpperInvariant()), supplier, customer,
                TableImport.Optional(r.Cell(5)))
            : null;

    /// <summary>Ключ сопоставления: ИНН и КПП, а у контрагента без ИНН — наименование без учёта регистра.</summary>
    private static string Key(string country, string name, string? inn, string? kpp) =>
        inn is not null ? $"inn:{country}:{inn}/{kpp}" : name.Trim().Length == 0 ? "" : $"name:{country}:" + name.Trim().ToUpperInvariant();

    private static Counterparty? Match(IEnumerable<Counterparty> list, string key) =>
        list.FirstOrDefault(c => Key(c.CountryCode, c.Name, c.Inn, c.Kpp) == key);

    private void Audit(AccessContext ctx, string action, string entityType, string? id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entityType, id,
            before, after, reason, currentUser.CorrelationId));
}
