using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Sales;

namespace KnitErp.Application.Sales;

/// <summary>Ошибка строки файла маркетплейса: номер строки в файле, столбец и значение, которое не число (или не дата).</summary>
public sealed record MarketplaceRowError(int Row, string Column, string Value, bool IsDate);

/// <summary>Значение ячейки не читается как число или дата.</summary>
public sealed class MarketplaceCellException(string column, string value, bool isDate) : FormatException($"«{column}»: «{value}»")
{
    public string Column { get; } = column;
    public string Value { get; } = value;
    public bool IsDate { get; } = isDate;
}

/// <summary>Разобранный файл: строки (без позиций), ошибки строк, найденный период и отпечаток содержимого.</summary>
public sealed record MarketplaceParseResult(
    IReadOnlyList<MarketplaceReportLine> Lines, IReadOnlyList<MarketplaceRowError> Errors, DateOnly? From, DateOnly? To, string Fingerprint, int HeaderRow);

/// <summary>
/// Форматы отчётов о реализации маркетплейсов (D1168): заголовки столбцов по умолчанию и разбор строк. Столбцы ищутся по заголовку
/// (без учёта регистра, «ё» = «е», лишние пробелы не важны) в первых 30 строках — над таблицей бывает шапка отчёта.
/// Допущение D1168: заголовки взяты из выгрузок личных кабинетов на 2025–2026 годы; площадки их меняют, поэтому у организации
/// есть своё сопоставление (<see cref="MarketplaceColumnMap"/>). API маркетплейсов не используется — только файл.
/// </summary>
public static class MarketplaceFormats
{
    public const int HeaderSearchRows = 30;
    public const int MaxErrors = 200;

    /// <summary>
    /// Wildberries: «Еженедельный детализированный отчёт» (ЛК продавца → «Финансовые отчёты» → «Детализация», выгрузка Excel).
    /// Одна строка — одна операция: «Тип документа» (Продажа / Возврат) и «Обоснование для оплаты» (Продажа, Возврат, Логистика, Штраф…).
    /// </summary>
    public static readonly IReadOnlyDictionary<MarketplaceField, string> Wildberries = new Dictionary<MarketplaceField, string>
    {
        [MarketplaceField.Article] = "Артикул поставщика",
        [MarketplaceField.ExternalCode] = "Код номенклатуры",
        [MarketplaceField.Barcode] = "Баркод",
        [MarketplaceField.Name] = "Название",
        [MarketplaceField.DocumentType] = "Тип документа",
        [MarketplaceField.PaymentReason] = "Обоснование для оплаты",
        [MarketplaceField.Date] = "Дата продажи",
        [MarketplaceField.Quantity] = "Кол-во",
        [MarketplaceField.Amount] = "Вайлдберриз реализовал Товар (Пр)",
        [MarketplaceField.ReturnQuantity] = "",
        [MarketplaceField.ReturnAmount] = "",
        [MarketplaceField.Commission] = "Вознаграждение Вайлдберриз (ВВ), без НДС",
        [MarketplaceField.Logistics] = "Услуги по доставке товара покупателю",
        [MarketplaceField.Payout] = "К перечислению Продавцу за реализованный Товар",
    };

    /// <summary>
    /// Ozon: «Отчёт о реализации товаров» (ЛК Ozon Seller → «Финансы» → «Документы»). Одна строка — товар за период: реализовано
    /// и возвращено в разных столбцах (двухуровневая шапка «Реализовано» / «Возвращено клиентом» сведена в одну строку заголовков).
    /// </summary>
    public static readonly IReadOnlyDictionary<MarketplaceField, string> Ozon = new Dictionary<MarketplaceField, string>
    {
        [MarketplaceField.Article] = "Артикул",
        [MarketplaceField.ExternalCode] = "SKU",
        [MarketplaceField.Barcode] = "Штрихкод",
        [MarketplaceField.Name] = "Наименование товара",
        [MarketplaceField.DocumentType] = "",
        [MarketplaceField.PaymentReason] = "",
        [MarketplaceField.Date] = "Дата",
        [MarketplaceField.Quantity] = "Реализовано, шт.",
        [MarketplaceField.Amount] = "Реализовано на сумму, руб.",
        [MarketplaceField.ReturnQuantity] = "Возвращено клиентом, шт.",
        [MarketplaceField.ReturnAmount] = "Возвращено на сумму, руб.",
        [MarketplaceField.Commission] = "Вознаграждение Ozon, руб.",
        [MarketplaceField.Logistics] = "Логистика, руб.",
        [MarketplaceField.Payout] = "Итого к начислению, руб.",
    };

    /// <summary>Иная площадка: простая таблица (тип операции, количество, сумма) — столбцы настраиваются.</summary>
    public static readonly IReadOnlyDictionary<MarketplaceField, string> Other = new Dictionary<MarketplaceField, string>
    {
        [MarketplaceField.Article] = "Артикул",
        [MarketplaceField.ExternalCode] = "Код товара",
        [MarketplaceField.Barcode] = "Штрихкод",
        [MarketplaceField.Name] = "Наименование",
        [MarketplaceField.DocumentType] = "Тип операции",
        [MarketplaceField.PaymentReason] = "",
        [MarketplaceField.Date] = "Дата",
        [MarketplaceField.Quantity] = "Количество",
        [MarketplaceField.Amount] = "Сумма",
        [MarketplaceField.ReturnQuantity] = "",
        [MarketplaceField.ReturnAmount] = "",
        [MarketplaceField.Commission] = "Комиссия",
        [MarketplaceField.Logistics] = "Логистика",
        [MarketplaceField.Payout] = "К перечислению",
    };

    public static IReadOnlyDictionary<MarketplaceField, string> Defaults(MarketplaceKind kind) => kind switch
    {
        MarketplaceKind.Wildberries => Wildberries,
        MarketplaceKind.Ozon => Ozon,
        _ => Other,
    };

    /// <summary>Внешняя система каталога, по связям с которой ищутся позиции; у иной площадки — нет (только штрихкод).</summary>
    public static ExternalSystem? SystemOf(MarketplaceKind kind) => kind switch
    {
        MarketplaceKind.Wildberries => ExternalSystem.Wildberries,
        MarketplaceKind.Ozon => ExternalSystem.Ozon,
        _ => null,
    };

    public static string FieldName(MarketplaceField field) => field switch
    {
        MarketplaceField.Article => "Артикул продавца",
        MarketplaceField.ExternalCode => "Код товара на площадке",
        MarketplaceField.Barcode => "Штрихкод",
        MarketplaceField.Name => "Наименование",
        MarketplaceField.DocumentType => "Тип документа (продажа или возврат)",
        MarketplaceField.PaymentReason => "Обоснование (логистика, штраф)",
        MarketplaceField.Date => "Дата продажи",
        MarketplaceField.Quantity => "Количество",
        MarketplaceField.Amount => "Сумма реализации",
        MarketplaceField.ReturnQuantity => "Возвращено, количество",
        MarketplaceField.ReturnAmount => "Возвращено, сумма",
        MarketplaceField.Commission => "Комиссия",
        MarketplaceField.Logistics => "Логистика",
        _ => "К перечислению",
    };

    public static string Normalize(string? header) =>
        string.Join(' ', (header ?? string.Empty).Replace(' ', ' ').Replace('ё', 'е').Replace('Ё', 'Е').ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Разбор листа: строка заголовков — первая (из 30), где есть столбец товара (артикул, код или штрихкод) и количества или суммы.
    /// Ошибка числа или даты — ошибка строки (строка не загружается); пустые строки и строки без товара и денег пропускаются.
    /// </summary>
    public static MarketplaceParseResult Parse(MarketplaceKind kind, IReadOnlyList<IReadOnlyList<string>> sheet,
        IReadOnlyDictionary<MarketplaceField, string> headers, long organizationId)
    {
        var (headerRow, columns) = FindHeader(sheet, headers);
        var lines = new List<MarketplaceReportLine>();
        var errors = new List<MarketplaceRowError>();
        DateOnly? from = null, to = null;
        var hasReturnColumns = columns.ContainsKey(MarketplaceField.ReturnQuantity) || columns.ContainsKey(MarketplaceField.ReturnAmount);
        for (var r = headerRow + 1; r < sheet.Count; r++)
        {
            var row = sheet[r];
            var number = r + 1;
            string? Text(MarketplaceField f) => columns.TryGetValue(f, out var c) && c < row.Count && !string.IsNullOrWhiteSpace(row[c]) ? row[c].Trim() : null;
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            try
            {
                decimal Num(MarketplaceField f) => ParseNumber(Text(f), headers[f]);
                var article = Text(MarketplaceField.Article);
                var code = Text(MarketplaceField.ExternalCode);
                var barcode = Text(MarketplaceField.Barcode);
                var name = Text(MarketplaceField.Name);
                var docType = Text(MarketplaceField.DocumentType);
                var reason = Text(MarketplaceField.PaymentReason);
                var date = ParseDate(Text(MarketplaceField.Date), headers[MarketplaceField.Date]);
                var quantity = Num(MarketplaceField.Quantity);
                var amount = Num(MarketplaceField.Amount);
                var commission = Num(MarketplaceField.Commission);
                var logistics = Num(MarketplaceField.Logistics);
                var payout = Num(MarketplaceField.Payout);
                var returnQuantity = Num(MarketplaceField.ReturnQuantity);
                var returnAmount = Num(MarketplaceField.ReturnAmount);
                if (article is null && code is null && barcode is null && quantity == 0 && amount == 0 && commission == 0 && logistics == 0 && payout == 0
                    && returnQuantity == 0 && returnAmount == 0)
                {
                    continue; // итоговые и пояснительные строки отчёта
                }

                if (date is { } d)
                {
                    from = from is null || d < from ? d : from;
                    to = to is null || d > to ? d : to;
                }

                MarketplaceReportLine Line(MarketplaceLineKind k, decimal q, decimal a, decimal c, decimal l, decimal p, string? note) =>
                    MarketplaceReportLine.Create(organizationId, number, k, article, code, barcode, name, note, date, Math.Abs(q), Math.Abs(a), c, l, p);

                if (hasReturnColumns)
                {
                    // Ozon: продажа и возврат товара — в одной строке, комиссия и логистика — к продаже (или к возврату, если продаж нет).
                    var sale = quantity != 0 || amount != 0;
                    var back = returnQuantity != 0 || returnAmount != 0;
                    if (sale)
                    {
                        lines.Add(Line(MarketplaceLineKind.Sale, quantity, amount, commission, logistics, payout, null));
                    }

                    if (back)
                    {
                        lines.Add(sale
                            ? Line(MarketplaceLineKind.Return, returnQuantity, returnAmount, 0, 0, 0, null)
                            : Line(MarketplaceLineKind.Return, returnQuantity, returnAmount, -commission, logistics, -payout, null));
                    }

                    if (!sale && !back)
                    {
                        lines.Add(Line(MarketplaceLineKind.Service, 0, 0, commission, logistics, payout, reason ?? docType));
                    }

                    continue;
                }

                var kindOf = KindOf(docType, reason);
                if (kindOf == MarketplaceLineKind.Service)
                {
                    lines.Add(Line(MarketplaceLineKind.Service, quantity, 0, commission, logistics, payout, reason ?? docType));
                }
                else
                {
                    // Суммы возврата в отчёте — положительные; в итогах возврат вычитается (MarketplaceReport.Recalculate).
                    lines.Add(Line(kindOf, quantity, amount, Math.Abs(commission), logistics, Math.Abs(payout), null));
                }
            }
            catch (MarketplaceCellException ex)
            {
                if (errors.Count < MaxErrors)
                {
                    errors.Add(new MarketplaceRowError(number, ex.Column, ex.Value, ex.IsDate));
                }
            }
        }

        return new MarketplaceParseResult(lines, errors, from, to, Fingerprint(kind, lines), headerRow + 1);
    }

    private static MarketplaceLineKind KindOf(string? docType, string? reason)
    {
        var r = Normalize(reason);
        if (r.Length > 0 && !r.Contains("продаж", StringComparison.Ordinal) && !r.Contains("возврат", StringComparison.Ordinal))
        {
            return MarketplaceLineKind.Service; // логистика, штраф, хранение, удержание
        }

        var t = Normalize(docType);
        if (t.Contains("возврат", StringComparison.Ordinal) || r.Contains("возврат", StringComparison.Ordinal))
        {
            return MarketplaceLineKind.Return;
        }

        return t.Length == 0 || t.Contains("продаж", StringComparison.Ordinal) || r.Contains("продаж", StringComparison.Ordinal)
            ? MarketplaceLineKind.Sale
            : MarketplaceLineKind.Service;
    }

    private static (int Row, Dictionary<MarketplaceField, int> Columns) FindHeader(IReadOnlyList<IReadOnlyList<string>> sheet,
        IReadOnlyDictionary<MarketplaceField, string> headers)
    {
        var wanted = headers.Where(h => !string.IsNullOrWhiteSpace(h.Value)).ToDictionary(h => h.Key, h => Normalize(h.Value));
        for (var r = 0; r < Math.Min(HeaderSearchRows, sheet.Count); r++)
        {
            var cells = sheet[r].Select((v, i) => (Text: Normalize(v), Index: i)).Where(x => x.Text.Length > 0).ToList();
            var columns = new Dictionary<MarketplaceField, int>();
            foreach (var (field, text) in wanted)
            {
                var hit = cells.FirstOrDefault(c => c.Text == text);
                if (hit.Text is not null)
                {
                    columns[field] = hit.Index;
                }
            }

            var item = columns.ContainsKey(MarketplaceField.Article) || columns.ContainsKey(MarketplaceField.ExternalCode)
                                                                     || columns.ContainsKey(MarketplaceField.Barcode);
            var money = columns.ContainsKey(MarketplaceField.Quantity) || columns.ContainsKey(MarketplaceField.Amount);
            if (item && money)
            {
                return (r, columns);
            }
        }

        var expected = string.Join(", ", new[] { MarketplaceField.Article, MarketplaceField.ExternalCode, MarketplaceField.Barcode, MarketplaceField.Quantity, MarketplaceField.Amount }
            .Where(f => wanted.ContainsKey(f)).Select(f => $"«{headers[f]}»"));
        throw new KnitErp.Domain.Common.BusinessRuleException("marketplace.header",
            $"В файле не найдена строка заголовков со столбцами товара и количества или суммы ({expected}). Проверьте, что выбран отчёт нужной площадки, или поправьте сопоставление столбцов.");
    }

    /// <summary>Число из ячейки: «1 234,56», «1,234.56», «-12.5», «12,5 ₽»; пусто — 0. Иначе MarketplaceCellException с подписью столбца.</summary>
    public static decimal ParseNumber(string? text, string column)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var t = new string(text.Where(c => char.IsDigit(c) || c is ',' or '.' or '-' or '−').ToArray()).Replace('−', '-');
        var lastComma = t.LastIndexOf(',');
        var lastDot = t.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            var dec = Math.Max(lastComma, lastDot);
            t = new string(t.Where((c, i) => c is not (',' or '.') || i == dec).ToArray()).Replace(',', '.');
        }
        else if (lastComma >= 0)
        {
            t = t.Count(c => c == ',') > 1 ? t.Replace(",", string.Empty, StringComparison.Ordinal) : t.Replace(',', '.');
        }
        else if (t.Count(c => c == '.') > 1)
        {
            t = t.Replace(".", string.Empty, StringComparison.Ordinal);
        }

        if (t.Length == 0 || !decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
                          || Math.Abs(v) > 999_999_999_999m)
        {
            throw new MarketplaceCellException(column, Short(text), false);
        }

        return v;
    }

    private static readonly string[] DateFormats =
    [
        "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "yyyy-MM-dd", "dd/MM/yyyy", "M/d/yyyy", "dd.MM.yyyy H:mm", "dd.MM.yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt",
        "M/d/yyyy H:mm",
    ];

    public static DateOnly? ParseDate(string? text, string column)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (t.Length >= 10 && t[4] == '-' && t[7] == '-')
        {
            t = t[..10]; // 2026-10-05T12:00:00
        }

        if (DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) && d.Year is > 2000 and < 2100)
        {
            return DateOnly.FromDateTime(d);
        }

        throw new MarketplaceCellException(column, Short(text), true);
    }

    private static string Short(string text) => text.Length > 30 ? text[..30] + "…" : text;

    /// <summary>Отпечаток содержимого (D1170): площадка и разобранные строки — тот же отчёт, сохранённый заново, узнаётся.</summary>
    public static string Fingerprint(MarketplaceKind kind, IReadOnlyList<MarketplaceReportLine> lines)
    {
        var text = new StringBuilder(((byte)kind).ToString(CultureInfo.InvariantCulture));
        foreach (var l in lines)
        {
            text.Append('\n').Append((byte)l.Kind).Append('|').Append(l.ExternalCode).Append('|').Append(l.Article).Append('|').Append(l.Barcode).Append('|')
                .Append(l.SaleDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append('|').Append(l.Note).Append('|')
                .Append(l.Quantity.ToString("0.######", CultureInfo.InvariantCulture)).Append('|').Append(l.Amount.ToString("0.####", CultureInfo.InvariantCulture))
                .Append('|').Append(l.Commission.ToString("0.####", CultureInfo.InvariantCulture)).Append('|')
                .Append(l.Logistics.ToString("0.####", CultureInfo.InvariantCulture)).Append('|').Append(l.Payout.ToString("0.####", CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
