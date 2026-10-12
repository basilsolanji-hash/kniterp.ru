using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KnitErp.Domain.Common;
using KnitErp.Domain.Organizations;

namespace KnitErp.Application.Printing;

/// <summary>Файл XML УПД: имя по правилам ФНС и содержимое в windows-1251.</summary>
public sealed record UpdXmlFile(string FileName, byte[] Content);

/// <summary>
/// D1140 (сессия «Продажи», запрос владельца «скачать УПД для ЭДО»): XML УПД — счёт-фактура и документ об отгрузке (функция «СЧФДОП»)
/// в формате ФНС версии 5.03 (приказ ФНС России от 19.12.2023 № ЕД-7-26/970@, КНД 1115131) для ручной загрузки в оператора ЭДО.
/// Данные — те же, что у печатной формы УПД (D68): продавец и покупатель, строки с НДС, документ об отгрузке, подписант.
/// Допущение D1140: проверка по официальной XSD ФНС в среде разработки не выполнялась (сайт ФНС недоступен); структура собрана по
/// описанию формата; перед рабочей отправкой файл проверяется загрузкой в оператора ЭДО.
/// </summary>
public static class UpdXml
{
    public const string FormatVersion = "5.03";
    public const string Knd = "1115131";

    static UpdXml() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Идентификаторы участников ЭДО неизвестны до подключения оператора — в имени файла ставятся ИНН/КПП сторон (допущение D1140);
    /// оператор при загрузке переименовывает файл по своим идентификаторам.
    /// </summary>
    public static UpdXmlFile Build(UpdPrintDto u, DateTime nowLocal, Guid fileGuid, IReadOnlyDictionary<int, IReadOnlyList<string>>? markingCodes = null)
    {
        if (u.Reversed)
        {
            throw new BusinessRuleException("sales.upd_xml.reversed", "Отгрузка сторнирована — УПД для ЭДО не формируется.");
        }

        if (string.IsNullOrWhiteSpace(u.Seller.Inn) || string.IsNullOrWhiteSpace(u.Buyer.Inn))
        {
            throw new BusinessRuleException("sales.upd_xml.inn", "Для УПД в ЭДО нужны ИНН продавца и покупателя — заполните реквизиты.");
        }

        var sender = Party(u.Seller);
        var receiver = Party(u.Buyer);
        var id = $"ON_NSCHFDOPPR_{receiver}_{sender}_{nowLocal:yyyyMMdd}_{fileGuid}";
        var vatExempt = u.Requisites.VatExempt;

        var doc = new XDocument(new XDeclaration("1.0", "windows-1251", null),
            new XElement("Файл",
                new XAttribute("ИдФайл", id),
                new XAttribute("ВерсФорм", FormatVersion),
                new XAttribute("ВерсПрог", "knitERP"),
                new XElement("Документ",
                    new XAttribute("КНД", Knd),
                    new XAttribute("Функция", "СЧФДОП"),
                    new XAttribute("ПоФактХЖ", "Документ об отгрузке товаров (выполнении работ), передаче имущественных прав (документ об оказании услуг)"),
                    new XAttribute("НаимДокОпр", "Счет-фактура и документ об отгрузке товаров (выполнении работ), передаче имущественных прав (документ об оказании услуг)"),
                    new XAttribute("ДатаИнфПр", nowLocal.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)),
                    new XAttribute("ВремИнфПр", nowLocal.ToString("HH.mm.ss", CultureInfo.InvariantCulture)),
                    new XAttribute("НаимЭконСубСост", Clip(u.Seller.Name, 1000)),
                    new XElement("СвСчФакт",
                        new XAttribute("НомерДок", Clip(u.Number, 1000)),
                        new XAttribute("ДатаДок", u.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)),
                        PartyElement("СвПрод", u.Seller),
                        new XElement("ГрузОт", new XElement("ОнЖе", "он же")),
                        PartyElement("ГрузПолуч", u.Buyer),
                        u.PaymentDocuments is { Length: > 0 } pay ? new XElement("СвПРД", new XAttribute("НомерПРД", Clip(pay, 30)),
                            new XAttribute("ДатаПРД", u.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))) : null,
                        new XElement("ДокПодтвОтгрНом",
                            new XAttribute("РеквНаимДок", "Универсальный передаточный документ"),
                            new XAttribute("РеквНомерДок", Clip(u.ShipmentNumber, 255)),
                            new XAttribute("РеквДатаДок", u.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))),
                        PartyElement("СвПокуп", u.Buyer),
                        new XElement("ДенИзм", new XAttribute("КодОКВ", CurrencyCode(u.Currency)), new XAttribute("НаимОКВ", CurrencyName(u.Currency)))),
                    new XElement("ТаблСчФакт",
                        u.Lines.Select(l => LineElement(l, vatExempt, markingCodes?.GetValueOrDefault(l.No))),
                        new XElement("ВсегоОпл",
                            new XAttribute("СтТовБезНДСВсего", Money(u.TotalWithoutVat)),
                            new XAttribute("СтТовУчНалВсего", Money(u.Total)),
                            new XElement("СумНалВсего", vatExempt || u.VatTotal == 0 && u.Lines.All(l => l.VatPercent is null)
                                ? new XElement("БезНДС", "без НДС")
                                : new XElement("СумНал", Money(u.VatTotal))))),
                    new XElement("СвПродПер",
                        new XElement("СвПер",
                            new XAttribute("СодОпер", "Товары переданы"),
                            new XAttribute("ДатаПер", u.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)),
                            new XElement("ОснПер", new XAttribute("РеквНаимДок", Clip(u.Basis, 255)), new XAttribute("РеквДатаДок", u.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))))),
                    Signer(u))));

        var settings = new XmlWriterSettings { Encoding = Encoding.GetEncoding(1251), Indent = true };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            doc.Save(writer);
        }

        return new UpdXmlFile(id + ".xml", stream.ToArray());
    }

    private static XElement LineElement(PrintLineDto l, bool vatExempt, IReadOnlyList<string>? codes)
    {
        var noVat = vatExempt || l.VatPercent is null;
        var extra = new XElement("ДопСведТов",
            new XAttribute("ПрТовРаб", "1"),
            new XAttribute("КодТов", Clip(l.Code, 100)),
            new XAttribute("НаимЕдИзм", Clip(l.UnitSymbol, 255)),
            l.OriginCountryCode is { Length: > 0 } cc && cc != "643" ? new XAttribute("КрНаимСтрПр", Clip(l.OriginCountryName ?? cc, 255)) : null);
        if (codes is { Count: > 0 })
        {
            // D1108: коды маркировки строки (КИЗ) — для вывода из оборота через ЭДО.
            extra.Add(new XElement("НомСредИдентТов", codes.Select(c => new XElement("КИЗ", c))));
        }

        return new XElement("СведТов",
            new XAttribute("НомСтр", l.No),
            new XAttribute("НаимТов", Clip(l.Name, 1000)),
            string.IsNullOrWhiteSpace(l.UnitCode) ? null : new XAttribute("ОКЕИ_Тов", l.UnitCode),
            new XAttribute("КолТов", Quantity(l.Quantity)),
            new XAttribute("ЦенаТов", Price(l.Price)),
            new XAttribute("СтТовБезНДС", Money(l.AmountWithoutVat)),
            new XAttribute("НалСт", noVat ? "без НДС" : $"{l.VatPercent!.Value.ToString("0.##", CultureInfo.InvariantCulture)}%"),
            new XAttribute("СтТовУчНал", Money(l.Amount)),
            new XElement("Акциз", new XElement("БезАкциз", "без акциза")),
            new XElement("СумНал", noVat ? new XElement("БезНДС", "без НДС") : new XElement("СумНал", Money(l.VatAmount))),
            l.OriginCountryCode is { Length: > 0 } cc2 && cc2 != "643" && l.CustomsDeclaration is { Length: > 0 } gtd
                ? new XElement("СвДТ", new XAttribute("КодПроисх", cc2), new XAttribute("НомерДТ", Clip(gtd, 29)))
                : null,
            extra);
    }

    private static XElement PartyElement(string name, PrintPartyDto p)
    {
        var inn = p.Inn ?? string.Empty;
        XElement idSv = inn.Length == 12
            ? new XElement("СвИП", new XAttribute("ИННФЛ", inn), new XElement("ФИО", FioAttributes(StripSoleProprietor(p.Name))))
            : new XElement("СвЮЛУч", new XAttribute("НаимОрг", Clip(p.Name, 1000)), new XAttribute("ИННЮЛ", inn),
                string.IsNullOrWhiteSpace(p.Kpp) ? null : new XAttribute("КПП", p.Kpp));
        return new XElement(name,
            new XElement("ИдСв", idSv),
            new XElement("Адрес", new XElement("АдрИнф", new XAttribute("КодСтр", "643"), new XAttribute("НаимСтран", "Россия"),
                new XAttribute("АдрТекст", Clip(p.Address ?? "-", 1000)))));
    }

    private static XElement Signer(UpdPrintDto u)
    {
        var name = u.Requisites.DirectorName ?? u.Seller.Name;
        var position = u.Requisites.SoleProprietor ? "Индивидуальный предприниматель" : u.Requisites.DirectorPosition ?? "Руководитель организации";
        return new XElement("Подписант",
            new XAttribute("СпосПодтПолном", "1"),
            new XAttribute("Должн", Clip(position, 128)),
            new XElement("ФИО", FioAttributes(name)));
    }

    private static IEnumerable<XAttribute> FioAttributes(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        yield return new XAttribute("Фамилия", Clip(parts.ElementAtOrDefault(0) ?? "-", 60));
        yield return new XAttribute("Имя", Clip(parts.ElementAtOrDefault(1) ?? "-", 60));
        if (parts.Length > 2)
        {
            yield return new XAttribute("Отчество", Clip(string.Join(' ', parts.Skip(2)), 60));
        }
    }

    private static string StripSoleProprietor(string name) =>
        name.Replace("Индивидуальный предприниматель", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("ИП ", string.Empty).Trim();

    private static string Party(PrintPartyDto p) => (p.Inn ?? "0") + (p.Kpp ?? string.Empty);

    private static string Money(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Price(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Quantity(decimal v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    // Валюта печатной формы — «Российский рубль, 643»: название и код по ОКВ.
    private static string CurrencyCode(string currency) => currency.Split(", ") is [_, var code] ? code : currency;

    private static string CurrencyName(string currency) => currency.Split(", ")[0];
}
