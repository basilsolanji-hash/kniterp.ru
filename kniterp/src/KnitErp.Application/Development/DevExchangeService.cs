using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>
/// Excel моделей (D794): выгрузка списка и загрузка новых моделей коллекции одним файлом. Файл проверяется целиком, ошибки —
/// по строкам, загрузка — всё или ничего. Строка с номером существующей модели пропускается — выгрузку можно загрузить обратно.
/// Незнакомая коллекция создаётся, если у пользователя есть право настроек разработки; иначе — ошибка строки.
/// </summary>
public sealed class DevExchangeService(IKnitErpDbContext db, IAccessGuard guard, DevModelService models, ISpreadsheetFormat spreadsheet, IClock clock)
{
    public const int MaxRows = 500;

    /// <summary>Столбцы загрузки; первые 12 обязательны в заголовке, «Номер» — необязательный (есть в выгрузке).</summary>
    public static readonly IReadOnlyList<string> Columns =
    [
        "Название", "Категория", "Аудитория", "Коллекция", "Заказчик", "Плановый запуск", "Приоритет", "Есть рисунок",
        "Размерный ряд", "Цветовая гамма", "Тираж", "Описание", "Номер",
    ];

    private const int Required = 12;
    private static readonly string[] Info = ["Стадия", "На стадии с", "Ответственный", "Просрочено"];

    public async Task<byte[]> TemplateAsync(CancellationToken ct = default)
    {
        await guard.DemandAsync(Permissions.DevEdit, ct);
        var example = new[]
        {
            "Свитер «Пример»", DevModel.CategoryName(DevCategory.Sweater), DevModel.AudienceName(DevAudience.Women), "Осень-зима 2026", "",
            DateOnly.FromDateTime(clock.UtcNow).AddDays(60).ToString("dd.MM.yyyy"), DevModel.PriorityName(DevPriority.Normal), "да", "42–52",
            "молочный, графит", "300", "Пустое название — по категории и коллекции; пустой запуск — по нормативам стадий",
        };
        return spreadsheet.Write([new SheetData("Модели", Columns.Take(Required).ToList(), [example], new HashSet<int> { 10 })]);
    }

    public async Task<byte[]> ExportAsync(DevModelFilter filter, CancellationToken ct = default)
    {
        await guard.DemandAsync(Permissions.DevView, ct);
        var rows = new List<IReadOnlyList<string>>();
        var page = 1;
        DevModelPageDto chunk;
        do
        {
            chunk = await models.ListAsync(filter, page++, 200, ct);
            var ids = chunk.Rows.Select(r => r.Id).ToList();
            var details = await db.DevModels.AsNoTracking().Where(m => ids.Contains(m.Id))
                .Select(m => new { m.Id, m.Audience, m.HasPattern, m.SizeRange, m.ColorRange, m.RunQuantity, m.Description, m.StageSinceUtc })
                .ToDictionaryAsync(m => m.Id, ct);
            foreach (var r in chunk.Rows)
            {
                var d = details[r.Id];
                rows.Add([
                    r.Name, r.CategoryName, DevModel.AudienceName(d.Audience), r.CollectionName ?? "", r.CustomerName ?? "",
                    r.PlannedLaunch?.ToString("dd.MM.yyyy") ?? "", r.PriorityName, d.HasPattern ? "да" : "нет", d.SizeRange ?? "", d.ColorRange ?? "",
                    d.RunQuantity?.ToString(CultureInfo.InvariantCulture) ?? "", d.Description ?? "", r.Number,
                    r.StageName, d.StageSinceUtc.ToString("dd.MM.yyyy"), r.ResponsibleName ?? "", r.IsOverdue ? "да" : "",
                ]);
            }
        }
        while (chunk.Page * chunk.PageSize < chunk.Total && page < 100);

        return spreadsheet.Write([new SheetData("Модели", [.. Columns, .. Info], rows, new HashSet<int> { 10 })]);
    }

    public async Task<ImportPlan> PreviewAsync(Stream file, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        return await CheckAsync(ctx, TableImport.Read(spreadsheet, file, Columns, Required), ct);
    }

    public async Task<ImportResult> ApplyAsync(IReadOnlyList<ImportRow> rows, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevEdit, ct);
        TableImport.EnsureApplicable(rows);
        // Строки проверяются заново: между проверкой и загрузкой данные могли измениться.
        var plan = await CheckAsync(ctx, rows.Select(r => r with { Errors = [] }).ToList(), ct);
        if (plan.ErrorRows > 0)
        {
            throw TableImport.HasErrors(plan);
        }

        var created = 0;
        await using var tx = await db.BeginTransactionAsync(ct);
        try
        {
            var collections = await db.DevLookups.Where(l => l.OrganizationId == ctx.OrganizationId && l.Kind == DevLookupKind.Collection)
                .ToDictionaryAsync(l => l.Name.ToLower(), l => l.Id, ct);
            foreach (var row in plan.Rows.Where(r => r.Action == ImportAction.Create))
            {
                var parsed = Parse(row, collections, null, out _);
                long? collection = null;
                if (row.Cell(3).Length > 0)
                {
                    var key = row.Cell(3).ToLower();
                    if (!collections.TryGetValue(key, out var id))
                    {
                        var lookup = DevLookup.Create(ctx.OrganizationId, DevLookupKind.Collection, row.Cell(3), null, collections.Count + 1);
                        db.DevLookups.Add(lookup);
                        await db.SaveOrConflictAsync(ct);
                        id = lookup.Id;
                        collections[key] = id;
                    }

                    collection = id;
                }

                var customer = row.Cell(4).Length == 0
                    ? null
                    : await db.Counterparties.AsNoTracking()
                        .Where(c => c.OrganizationId == ctx.OrganizationId && c.IsCustomer && !c.IsArchived && c.Name == row.Cell(4))
                        .Select(c => (long?)c.Id).FirstOrDefaultAsync(ct);
                await models.CreateCoreAsync(ctx, parsed!.Header with { CollectionId = collection, CustomerId = customer }, parsed.Brief,
                    $"загрузка из Excel, строка {row.RowNumber}", ct);
                created++;
            }

            await tx.CommitAsync(ct);
            return new ImportResult(created, 0);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private sealed record Parsed(DevModelHeader Header, DevBrief Brief);

    private async Task<ImportPlan> CheckAsync(AccessContext ctx, IReadOnlyList<ImportRow> rows, CancellationToken ct)
    {
        if (rows.Count > MaxRows)
        {
            throw new BusinessRuleException("dev.import.rows", $"За один раз — не больше {MaxRows} моделей.");
        }

        var org = ctx.OrganizationId;
        var collections = await db.DevLookups.AsNoTracking().Where(l => l.OrganizationId == org && l.Kind == DevLookupKind.Collection)
            .Select(l => new { l.Id, l.Name, l.IsArchived }).ToListAsync(ct);
        var active = collections.Where(c => !c.IsArchived).ToDictionary(c => c.Name.ToLower(), c => c.Id);
        var archived = collections.Where(c => c.IsArchived).Select(c => c.Name.ToLower()).ToHashSet();
        var customerNames = rows.Select(r => r.Cell(4)).Where(n => n.Length > 0).Distinct().ToList();
        var customers = await db.Counterparties.AsNoTracking()
            .Where(c => c.OrganizationId == org && c.IsCustomer && !c.IsArchived && customerNames.Contains(c.Name))
            .Select(c => c.Name).ToListAsync(ct);
        var numbers = rows.Select(r => r.Cell(12)).Where(n => n.Length > 0).Distinct().ToList();
        var existing = await db.DevModels.AsNoTracking().Where(m => m.OrganizationId == org && numbers.Contains(m.Number))
            .Select(m => m.Number).ToListAsync(ct);
        var canCreateCollections = ctx.Permissions.Has(Permissions.DevSettings);

        var checkedRows = new List<ImportRow>();
        foreach (var row in rows)
        {
            if (row.Cell(12).Length > 0)
            {
                checkedRows.Add(existing.Contains(row.Cell(12))
                    ? row with { Action = ImportAction.Unchanged, Errors = [] }
                    : row with { Action = ImportAction.Error, Errors = ["Модели с таким номером нет: для новой модели оставьте «Номер» пустым."] });
                continue;
            }

            Parse(row, active, null, out var errors);
            var collection = row.Cell(3).ToLower();
            if (collection.Length > 0 && !active.ContainsKey(collection))
            {
                if (archived.Contains(collection))
                {
                    errors.Add("Коллекция в архиве.");
                }
                else if (!canCreateCollections)
                {
                    errors.Add("Такой коллекции нет — добавьте её в настройках разработки.");
                }
            }

            if (row.Cell(4).Length > 0 && !customers.Contains(row.Cell(4)))
            {
                errors.Add("Заказчик не найден среди покупателей.");
            }

            checkedRows.Add(row with { Action = errors.Count == 0 ? ImportAction.Create : ImportAction.Error, Errors = errors });
        }

        return new ImportPlan(Columns, checkedRows);
    }

    /// <summary>Разбор значений строки без обращения к базе; ошибки — понятными фразами для протокола.</summary>
    private static Parsed? Parse(ImportRow row, IReadOnlyDictionary<string, long> collections, long? customer, out List<string> errors)
    {
        errors = [];
        var category = Find(Enum.GetValues<DevCategory>(), DevModel.CategoryName, row.Cell(1), DevCategory.Other);
        if (category is null)
        {
            errors.Add("Неизвестная категория.");
        }

        var audience = Find(Enum.GetValues<DevAudience>(), DevModel.AudienceName, row.Cell(2), DevAudience.Women);
        if (audience is null)
        {
            errors.Add("Неизвестная аудитория.");
        }

        var priority = Find(Enum.GetValues<DevPriority>(), DevModel.PriorityName, row.Cell(6), DevPriority.Normal);
        if (priority is null)
        {
            errors.Add("Неизвестный приоритет.");
        }

        var launch = TableImport.ParseDate(row.Cell(5));
        if (row.Cell(5).Length > 0 && launch is null)
        {
            errors.Add("Дата запуска — в виде 15.12.2026.");
        }

        var hasPattern = row.Cell(7).Length == 0 ? true : TableImport.ParseYesNo(row.Cell(7));
        if (hasPattern is null)
        {
            errors.Add("«Есть рисунок» — да или нет.");
        }

        int? run = null;
        if (row.Cell(10).Length > 0)
        {
            if (decimal.TryParse(row.Cell(10).Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var q) && q == decimal.Truncate(q)
                && q is >= 1 and <= DevModel.MaxRun)
            {
                run = (int)q;
            }
            else
            {
                errors.Add("Тираж — целое число от 1 до 10 000 000.");
            }
        }

        if (row.Cell(0).Length > DevModel.NameMaxLength)
        {
            errors.Add($"Название длиннее {DevModel.NameMaxLength} символов.");
        }

        if (new[] { 8, 9 }.Any(i => row.Cell(i).Length > DevModel.ShortTextMaxLength) || row.Cell(11).Length > DevModel.TextMaxLength)
        {
            errors.Add("Слишком длинный текст в задании.");
        }

        if (errors.Count > 0)
        {
            return null;
        }

        var header = new DevModelHeader(row.Cell(0), row.Cell(3).Length > 0 && collections.TryGetValue(row.Cell(3).ToLower(), out var c) ? c : null,
            category!.Value, audience!.Value, false, hasPattern!.Value, customer, null, launch, priority!.Value, new Dictionary<DevRole, long?>());
        var brief = new DevBrief(TableImport.Optional(row.Cell(11)), TableImport.Optional(row.Cell(8)), TableImport.Optional(row.Cell(9)), null, null, run,
            null, null, null, null);
        return new Parsed(header, brief);
    }

    /// <summary>Значение справочника по названию без учёта регистра; пустая ячейка — значение по умолчанию.</summary>
    private static T? Find<T>(IEnumerable<T> values, Func<T, string> name, string text, T empty)
        where T : struct
    {
        if (text.Length == 0)
        {
            return empty;
        }

        foreach (var v in values)
        {
            if (string.Equals(name(v), text, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }
}
