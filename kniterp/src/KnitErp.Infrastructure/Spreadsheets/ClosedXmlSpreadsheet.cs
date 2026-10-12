using ClosedXML.Excel;
using KnitErp.Application.Common;
using KnitErp.Domain.Common;

namespace KnitErp.Infrastructure.Spreadsheets;

/// <summary>
/// Excel (.xlsx) через ClosedXML. Формулы не вычисляются: берётся сохранённое в файле значение.
/// Защита от «раздувающихся» файлов (аудит 10.10.2026, п. 7): до разбора проверяется распакованный объём архива,
/// после — ширина таблицы и общее число ячеек; лишнее отклоняется до обхода ячеек.
/// </summary>
public sealed class ClosedXmlSpreadsheet : ISpreadsheetFormat
{
    /// <summary>Наши шаблоны уже 30 столбцов; запас — вдвое.</summary>
    public const int MaxColumns = 64;
    public const long MaxUncompressedBytes = 50L * 1024 * 1024;
    public const int MaxZipEntries = 1000;
    public const long MaxCells = 400_000;

    public IReadOnlyList<IReadOnlyList<string>> ReadFirstSheet(Stream stream, int maxRows) => ReadSheet(stream, maxRows, null)!;

    // D1168 (сессия «Продажи»): отчёты маркетплейсов — до 200 столбцов и 2 млн ячеек (Wildberries — около 80 столбцов).
    public const int MaxWideColumns = 200;
    public const long MaxWideCells = 2_000_000;

    public IReadOnlyList<IReadOnlyList<string>> ReadWideSheet(Stream stream, int maxRows) => ReadSheet(stream, maxRows, null, MaxWideColumns, MaxWideCells)!;

    /// <summary>Лист по названию (D308: «Состав комплектов»); листа нет — null. Те же ограничения размера, что у первого листа.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? ReadNamedSheet(Stream stream, string sheetName, int maxRows) => ReadSheet(stream, maxRows, sheetName);

    private IReadOnlyList<IReadOnlyList<string>>? ReadSheet(Stream stream, int maxRows, string? sheetName, int maxColumns = MaxColumns,
        long maxCells = MaxCells)
    {
        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            stream = copy;
        }

        stream.Position = 0;
        EnsureReasonableArchive(stream);
        stream.Position = 0;
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new BusinessRuleException("import.file_invalid", "Файл не читается как таблица Excel (.xlsx). Сохраните его в формате .xlsx.");
        }

        using (workbook)
        {
            if (sheetName is not null && !workbook.Worksheets.Any(w => string.Equals(w.Name, sheetName, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var sheet = (sheetName is null
                            ? workbook.Worksheets.FirstOrDefault()
                            : workbook.Worksheets.First(w => string.Equals(w.Name, sheetName, StringComparison.OrdinalIgnoreCase)))
                        ?? throw new BusinessRuleException("import.file_empty", "В файле нет листов.");
            var used = sheet.RangeUsed();
            if (used is null)
            {
                return [];
            }

            var lastRow = used.LastRow().RowNumber();
            if (lastRow > maxRows + 1)
            {
                throw new BusinessRuleException("import.too_many_rows", $"В файле больше {maxRows} строк. Разбейте его на части.");
            }

            var lastColumn = used.LastColumn().ColumnNumber();
            if (lastColumn > maxColumns)
            {
                throw new BusinessRuleException("import.too_many_columns",
                    $"В таблице {lastColumn} столбцов — больше {maxColumns}. Удалите лишние столбцы или скачайте шаблон заново.");
            }

            if ((long)lastRow * lastColumn > maxCells)
            {
                throw new BusinessRuleException("import.too_many_cells", "Таблица слишком большая. Разбейте файл на части.");
            }

            var rows = new List<IReadOnlyList<string>>(lastRow);
            for (var r = 1; r <= lastRow; r++)
            {
                var row = sheet.Row(r);
                var cells = new string[lastColumn];
                for (var c = 1; c <= lastColumn; c++)
                {
                    cells[c - 1] = row.Cell(c).GetFormattedString().Trim();
                }

                rows.Add(cells);
            }

            return rows;
        }
    }

    /// <summary>XLSX — это ZIP: сумма распакованных размеров и число частей ограничены, иначе файл отклоняется сразу.</summary>
    private static void EnsureReasonableArchive(Stream stream)
    {
        try
        {
            using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > MaxZipEntries)
            {
                throw new BusinessRuleException("import.file_too_complex", "Файл слишком сложный для загрузки. Сохраните таблицу заново как .xlsx.");
            }

            long total = 0;
            foreach (var entry in zip.Entries)
            {
                total += entry.Length;
                if (entry.Length < 0 || total > MaxUncompressedBytes)
                {
                    throw new BusinessRuleException("import.file_too_large_unpacked",
                        "После распаковки файл больше 50 МБ. Удалите лишнее (картинки, пустое форматирование) или разбейте файл.");
                }
            }
        }
        catch (InvalidDataException)
        {
            throw new BusinessRuleException("import.file_invalid", "Файл не читается как таблица Excel (.xlsx). Сохраните его в формате .xlsx.");
        }
    }

    public byte[] Write(IReadOnlyList<SheetData> sheets)
    {
        using var workbook = new XLWorkbook();
        foreach (var data in sheets)
        {
            var sheet = workbook.Worksheets.Add(data.Name);
            var numeric = data.NumericColumns ?? new HashSet<int>();
            for (var c = 0; c < data.Header.Count; c++)
            {
                // Текстовый формат на весь столбец: и новые строки, которые впишет пользователь, не станут числами.
                sheet.Column(c + 1).Style.NumberFormat.Format = numeric.Contains(c) ? "#,##0.######" : "@";
                var cell = sheet.Cell(1, c + 1);
                cell.Value = data.Header[c];
                cell.Style.Font.Bold = true;
            }

            for (var r = 0; r < data.Rows.Count; r++)
            {
                for (var c = 0; c < data.Rows[r].Count; c++)
                {
                    var cell = sheet.Cell(r + 2, c + 1);
                    var text = data.Rows[r][c];
                    if (numeric.Contains(c) && decimal.TryParse(text, System.Globalization.NumberStyles.Number,
                            System.Globalization.CultureInfo.InvariantCulture, out var number))
                    {
                        cell.SetValue(number);
                        cell.Style.NumberFormat.Format = "#,##0.######";
                        continue;
                    }

                    // Остальное как текст: коды вида «0042» не должны превращаться в число 42.
                    cell.Style.NumberFormat.Format = "@";
                    cell.SetValue(text);
                }
            }

            sheet.SheetView.FreezeRows(1);
            sheet.Columns().AdjustToContents(1, Math.Min(data.Rows.Count + 1, 200));
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }
}
