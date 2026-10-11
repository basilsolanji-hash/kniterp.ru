using KnitErp.Application.Common;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Warehousing;

/// <summary>Общее для строк складских документов: выбор позиций, точность количества, загрузка строк из Excel.</summary>
internal static class StockEntry
{
    public const int MaxImportRows = 5000;
    public static readonly IReadOnlyList<string> ImportColumns = ["Код", "Количество"];

    /// <summary>
    /// Действующие позиции для ввода; фильтр и сортировка — до проекции, чтобы запрос переводился в SQL.
    /// stockedOnly — только то, что хранится на складе (без услуг, D300): для выбора строк складских документов.
    /// </summary>
    public static IQueryable<EntryItemDto> ActiveItems(IKnitErpDbContext db, long organizationId, long? itemId = null, bool stockedOnly = false) =>
        db.Items.AsNoTracking().Where(i => i.OrganizationId == organizationId && !i.IsArchived && (itemId == null || i.Id == itemId))
            .Where(i => !stockedOnly || !ItemTypes.NotStocked.Contains(i.Type))
            .OrderBy(i => i.Code)
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new EntryItemDto(i.Id, i.Code, i.Name, u.Symbol, u.Precision));

    /// <summary>
    /// D300 (сессия 4, точечная проверка): услуга не хранится на складе — её нельзя провести складским документом
    /// (поступление, списание, перемещение, отгрузка, инвентаризация, начальные остатки, партии).
    /// </summary>
    public static async Task EnsureStockedAsync(IKnitErpDbContext db, long organizationId, IEnumerable<long> itemIds, CancellationToken ct)
    {
        var ids = itemIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var codes = await db.Items.AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && ids.Contains(i.Id) && ItemTypes.NotStocked.Contains(i.Type))
            .OrderBy(i => i.Code).Select(i => i.Code).ToListAsync(ct);
        if (codes.Count > 0)
        {
            throw ServiceRules.NotStocked(codes);
        }
    }

    /// <summary>Позиции из списка, которые не хранятся на складе (услуги, D300).</summary>
    public static async Task<HashSet<long>> NotStockedAsync(IKnitErpDbContext db, long organizationId, IEnumerable<long> itemIds, CancellationToken ct)
    {
        var ids = itemIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : (await db.Items.AsNoTracking()
                .Where(i => i.OrganizationId == organizationId && ids.Contains(i.Id) && ItemTypes.NotStocked.Contains(i.Type))
                .Select(i => i.Id).ToListAsync(ct)).ToHashSet();
    }

    public static void EnsurePrecision(EntryItemDto item, decimal quantity)
    {
        if (decimal.Round(quantity, item.Precision) != quantity)
        {
            throw new BusinessRuleException("stock.quantity.precision", PrecisionMessage(item));
        }
    }

    public static string PrecisionMessage(EntryItemDto item) => item.Precision == 0
        ? $"Количество в «{item.UnitSymbol}» должно быть целым."
        : $"Для «{item.UnitSymbol}» допустимо не больше {item.Precision} знаков после запятой.";

    /// <summary>
    /// Разбор файла строк (столбцы «Код», «Количество»). Возвращает протокол по каждой строке и строки без ошибок;
    /// применять их можно, только если ошибок нет нигде.
    /// </summary>
    public static async Task<(IReadOnlyList<LineImportRow> Rows, IReadOnlyList<(long ItemId, decimal Quantity)> Lines)> ParseLinesAsync(
        IKnitErpDbContext db, ISpreadsheetFormat spreadsheet, long organizationId, Stream file, CancellationToken ct, bool allowZero = false)
    {
        var sheet = spreadsheet.ReadFirstSheet(file, MaxImportRows);
        if (sheet.Count == 0)
        {
            throw new BusinessRuleException("import.file_empty", "Файл пустой.");
        }

        for (var c = 0; c < ImportColumns.Count; c++)
        {
            var actual = c < sheet[0].Count ? sheet[0][c].Trim() : "";
            if (!string.Equals(actual, ImportColumns[c], StringComparison.OrdinalIgnoreCase))
            {
                throw new BusinessRuleException("import.header", $"Столбец {c + 1} должен называться «{ImportColumns[c]}», а в файле «{actual}».");
            }
        }

        var items = await ActiveItems(db, organizationId).ToDictionaryAsync(i => i.Code, ct);
        var archived = await db.Items.AsNoTracking().Where(i => i.OrganizationId == organizationId && i.IsArchived)
            .Select(i => i.Code).ToListAsync(ct);
        var notStocked = (await db.Items.AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && ItemTypes.NotStocked.Contains(i.Type)).Select(i => i.Code).ToListAsync(ct)).ToHashSet();
        var rows = new List<LineImportRow>();
        var lines = new List<(long ItemId, decimal Quantity)>();
        var seen = new Dictionary<string, int>();
        for (var r = 1; r < sheet.Count; r++)
        {
            var cells = sheet[r];
            var code = Item.NormalizeCode(cells.Count > 0 ? cells[0] : "");
            var qtyText = cells.Count > 1 ? cells[1].Trim() : "";
            if (code.Length == 0 && qtyText.Length == 0)
            {
                continue;
            }

            var errors = new List<string>();
            if (seen.TryGetValue(code, out var firstRow))
            {
                errors.Add($"Позиция {code} уже есть в строке {firstRow}.");
            }
            else
            {
                seen[code] = r + 1;
            }

            if (!items.TryGetValue(code, out var item))
            {
                errors.Add(archived.Contains(code) ? $"Позиция {code} в архиве." : $"Позиции с кодом «{code}» нет в номенклатуре.");
            }
            else if (notStocked.Contains(code))
            {
                errors.Add($"Позиция {code} — услуга: на складе не хранится.");
            }

            if (!Quantities.TryParse(qtyText, out var qty) || qty < 0 || (qty == 0 && !allowZero))
            {
                errors.Add(allowZero ? $"Количество «{qtyText}» — нужно число не меньше 0." : $"Количество «{qtyText}» — нужно положительное число.");
            }
            else if (item is not null && decimal.Round(qty, item.Precision) != qty)
            {
                errors.Add(PrecisionMessage(item));
            }

            rows.Add(new LineImportRow(r + 1, code, qtyText, errors));
            if (errors.Count == 0)
            {
                lines.Add((item!.Id, qty));
            }
        }

        if (rows.Count == 0)
        {
            throw new BusinessRuleException("import.file_empty", "В файле нет строк с данными.");
        }

        return (rows, lines);
    }

    /// <summary>Шаблон строк: «Код», «Количество» и лист с действующей номенклатурой для подсказки.</summary>
    public static async Task<byte[]> LinesTemplateAsync(
        IKnitErpDbContext db, ISpreadsheetFormat spreadsheet, long organizationId, string sheetName, CancellationToken ct)
    {
        var items = await ActiveItems(db, organizationId, stockedOnly: true).ToListAsync(ct);
        return spreadsheet.Write(
        [
            new SheetData(sheetName, ImportColumns, [["ПР-0001", "12,5"]]),
            new SheetData("Номенклатура", ["Код", "Наименование", "Единица", "Знаков после запятой"],
                items.Select(i => (IReadOnlyList<string>)[i.Code, i.Name, i.UnitSymbol, i.Precision.ToString()]).ToList()),
        ]);
    }
}
