using System.Globalization;
using System.Xml.Linq;
using KnitErp.Application.Common;
using KnitErp.Domain.Common;

namespace KnitErp.Application.Purchasing;

/// <summary>Строка УПД поставщика: товар, его код у поставщика (КодТов), штрихкод (GTIN из ИнфПолФХЖ2 или кода маркировки), количество, цена, суммы.</summary>
public sealed record SupplierUpdLine(
    int No, string Name, string? Code, string? Barcode, string? Unit, decimal Quantity, decimal? Price, decimal? AmountWithoutVat, decimal? VatPercent,
    decimal? Amount);

public sealed record SupplierUpd(
    string Number, DateOnly Date, string SellerName, string SellerInn, string? SellerKpp, string BuyerName, string BuyerInn, string? CurrencyCode,
    decimal? Total, IReadOnlyList<SupplierUpdLine> Lines);

/// <summary>
/// D149: разбор XML УПД поставщика (формат ФНС 5.03, ON_NSCHFDOPPR; читаются и реквизиты 5.01 — НомерСчФ/ДатаСчФ). Только то, что нужно
/// для поступления: номер и дата счёта-фактуры, продавец и покупатель (ИНН), строки таблицы. Без DTD и внешних сущностей, до 5 МБ
/// (<see cref="SafeXml"/>). Чужие файлы (не УПД) — понятный отказ.
/// </summary>
public static class SupplierUpdXml
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxLines = 2000;

    public static SupplierUpd Parse(byte[] content)
    {
        var doc = SafeXml.Load(content, MaxBytes);
        var file = doc.Root;
        var document = file?.Element("Документ");
        var invoice = document?.Element("СвСчФакт");
        if (file is null || file.Name.LocalName != "Файл" || invoice is null)
        {
            throw new BusinessRuleException("purchase.upd.format", "Это не XML УПД (формат ФНС 5.03): нет элементов Файл → Документ → СвСчФакт.");
        }

        var number = ((string?)invoice.Attribute("НомерДок") ?? (string?)invoice.Attribute("НомерСчФ"))?.Trim();
        var dateText = (string?)invoice.Attribute("ДатаДок") ?? (string?)invoice.Attribute("ДатаСчФ");
        if (string.IsNullOrEmpty(number) || !DateOnly.TryParseExact(dateText, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new BusinessRuleException("purchase.upd.number", "В УПД нет номера или даты счёта-фактуры.");
        }

        var (sellerName, sellerInn, sellerKpp) = Party(invoice.Element("СвПрод"));
        var (buyerName, buyerInn, _) = Party(invoice.Element("СвПокуп"));
        if (sellerInn is null || buyerInn is null)
        {
            throw new BusinessRuleException("purchase.upd.inn", "В УПД нет ИНН продавца или покупателя.");
        }

        var currency = (string?)invoice.Element("ДенИзм")?.Attribute("КодОКВ") ?? (string?)invoice.Attribute("КодОКВ");
        var table = document!.Element("ТаблСчФакт");
        var rows = table?.Elements("СведТов").ToList() ?? [];
        if (rows.Count == 0)
        {
            throw new BusinessRuleException("purchase.upd.lines", "В УПД нет строк с товарами.");
        }

        if (rows.Count > MaxLines)
        {
            throw new BusinessRuleException("purchase.upd.too_many", $"В УПД больше {MaxLines} строк — загрузите его частями.");
        }

        var lines = new List<SupplierUpdLine>();
        foreach (var (row, index) in rows.Select((r, i) => (r, i)))
        {
            var no = int.TryParse((string?)row.Attribute("НомСтр"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : index + 1;
            var extra = row.Element("ДопСведТов");
            var info = row.Elements("ИнфПолФХЖ2").GroupBy(e => ((string?)e.Attribute("Идентиф") ?? string.Empty).Trim().ToLowerInvariant(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => ((string?)g.First().Attribute("Значен"))?.Trim(), StringComparer.Ordinal);
            var code = Text((string?)extra?.Attribute("КодТов")) ?? Text(info.GetValueOrDefault("артикул")) ?? Text(info.GetValueOrDefault("код"));
            var barcode = Text(info.GetValueOrDefault("штрихкод")) ?? Text(info.GetValueOrDefault("gtin")) ?? Text((string?)extra?.Attribute("ГТИН"))
                          ?? GtinOf(extra?.Element("НомСредИдентТов")?.Elements().Select(e => e.Value).FirstOrDefault());
            var quantity = Number((string?)row.Attribute("КолТов"));
            if (quantity is not { } q || q <= 0)
            {
                throw new BusinessRuleException("purchase.upd.quantity", $"Строка {no} УПД: нет количества.");
            }

            var rate = (string?)row.Attribute("НалСт");
            lines.Add(new SupplierUpdLine(no, Text((string?)row.Attribute("НаимТов")) ?? $"Строка {no}", code, barcode,
                Text((string?)extra?.Attribute("НаимЕдИзм")), decimal.Round(q, 6), Number((string?)row.Attribute("ЦенаТов")),
                Number((string?)row.Attribute("СтТовБезНДС")), Rate(rate), Number((string?)row.Attribute("СтТовУчНал"))));
        }

        var total = Number((string?)table!.Element("ВсегоОпл")?.Attribute("СтТовУчНалВсего"));
        return new SupplierUpd(number, date, sellerName ?? sellerInn, sellerInn, sellerKpp, buyerName ?? buyerInn, buyerInn, currency, total, lines);
    }

    private static (string? Name, string? Inn, string? Kpp) Party(XElement? e)
    {
        var id = e?.Element("ИдСв");
        if (id?.Element("СвЮЛУч") is { } org)
        {
            return (Text((string?)org.Attribute("НаимОрг")), Digits((string?)org.Attribute("ИННЮЛ")), Text((string?)org.Attribute("КПП")));
        }

        if (id?.Element("СвИП") is { } ip)
        {
            var fio = ip.Element("ФИО");
            var name = fio is null ? null : string.Join(' ', new[] { "Фамилия", "Имя", "Отчество" }.Select(a => (string?)fio.Attribute(a)).Where(x => !string.IsNullOrWhiteSpace(x)));
            return (string.IsNullOrWhiteSpace(name) ? null : "ИП " + name, Digits((string?)ip.Attribute("ИННФЛ")), null);
        }

        return (null, null, null);
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Digits(string? value) =>
        value?.Trim() is { Length: 10 or 12 } v && v.All(char.IsAsciiDigit) ? v : null;

    private static decimal? Number(string? value) =>
        decimal.TryParse(value?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static decimal? Rate(string? value)
    {
        var v = value?.Trim().TrimEnd('%');
        return v is null || v.StartsWith("без", StringComparison.OrdinalIgnoreCase) ? null
            : decimal.TryParse(v.Split('/')[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var r) ? r : null;
    }

    /// <summary>GTIN из кода маркировки «01» + 14 цифр.</summary>
    private static string? GtinOf(string? code) =>
        code is { Length: >= 16 } c && c.StartsWith("01", StringComparison.Ordinal) && c[2..16].All(char.IsAsciiDigit) ? c[2..16] : null;
}
