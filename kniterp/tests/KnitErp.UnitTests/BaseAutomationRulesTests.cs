using System.Text;
using KnitErp.Application.Finance;
using KnitErp.Application.Purchasing;
using KnitErp.Domain.Automation;
using KnitErp.Domain.Common;
using KnitErp.Domain.Finance;

namespace KnitErp.UnitTests;

/// <summary>Сессия № 1, этап 2: правила расписаний (D147), номеров напоминаний (D148), разбора XML ЦБ (D146) и УПД поставщика (D149).</summary>
public sealed class BaseAutomationRulesTests
{
    private static RecurringOperationData Data(RecurrencePeriod period, int day, DateOnly start, DateOnly? end = null) =>
        new("Аренда", MoneyOperationKind.Expense, 1, null, 100m, null, "Аренда", period, day, start, end);

    [Fact]
    public void Monthly_schedule_clamps_to_month_end_and_catches_up_limited()
    {
        var r = RecurringOperation.Create(1, Data(RecurrencePeriod.Monthly, 31, new DateOnly(2026, 1, 15)), 1, DateTime.UtcNow);
        Assert.Equal(new DateOnly(2026, 1, 31), r.NextDate);
        Assert.Equal([new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31)], r.TakeDue(new DateOnly(2026, 4, 1)));
        Assert.Equal(new DateOnly(2026, 4, 30), r.NextDate);
        Assert.Empty(r.TakeDue(new DateOnly(2026, 4, 29)));

        var old = RecurringOperation.Create(1, Data(RecurrencePeriod.Monthly, 1, new DateOnly(2020, 1, 1)), 1, DateTime.UtcNow);
        Assert.Equal(RecurringOperation.MaxCatchUp, old.TakeDue(new DateOnly(2026, 1, 1)).Count);
    }

    [Fact]
    public void Weekly_quarterly_end_date_and_restore_do_not_catch_up()
    {
        var w = RecurringOperation.Create(1, Data(RecurrencePeriod.Weekly, 1, new DateOnly(2026, 10, 7)), 1, DateTime.UtcNow); // среда → понедельник
        Assert.Equal(new DateOnly(2026, 10, 12), w.NextDate);
        var q = RecurringOperation.Create(1, Data(RecurrencePeriod.Quarterly, 10, new DateOnly(2026, 1, 11), new DateOnly(2026, 8, 1)), 1, DateTime.UtcNow);
        Assert.Equal([new DateOnly(2026, 4, 10), new DateOnly(2026, 7, 10)], q.TakeDue(new DateOnly(2027, 1, 1)));

        w.Archive();
        Assert.Empty(w.TakeDue(new DateOnly(2026, 12, 1)));
        w.Restore(new DateOnly(2026, 12, 1));
        Assert.Equal(new DateOnly(2026, 12, 7), w.NextDate);
        Assert.Throws<BusinessRuleException>(() => RecurringOperation.Create(1, Data(RecurrencePeriod.Weekly, 0, new DateOnly(2026, 1, 1)), 1, DateTime.UtcNow));
        Assert.Throws<BusinessRuleException>(() => RecurringOperation.Create(1, Data(RecurrencePeriod.Monthly, 1, new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)), 1, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(10, 10, 7, 0)]
    [InlineData(10, 11, 7, 1)]
    [InlineData(10, 17, 7, 1)]
    [InlineData(10, 18, 7, 2)]
    [InlineData(10, 40, 7, 3)]
    [InlineData(10, 40, 0, 1)]
    public void Overdue_reminder_number(int due, int today, int repeat, int expected) =>
        Assert.Equal(expected, AutomationSettings.OverdueReminderNumber(new DateOnly(2026, 10, 1).AddDays(due - 1), new DateOnly(2026, 10, 1).AddDays(today - 1), repeat));

    [Fact]
    public void Cbr_xml_in_windows_1251_is_parsed_and_bad_rows_skipped()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var xml = "<?xml version=\"1.0\" encoding=\"windows-1251\"?><ValCurs Date=\"08.10.2026\" name=\"Foreign Currency Market\">" +
                  "<Valute ID=\"R01235\"><NumCode>840</NumCode><CharCode>USD</CharCode><Nominal>1</Nominal><Name>Доллар США</Name><Value>81,5000</Value></Valute>" +
                  "<Valute ID=\"R01335\"><NumCode>398</NumCode><CharCode>KZT</CharCode><Nominal>100</Nominal><Name>Тенге</Name><Value>15,1234</Value></Valute>" +
                  "<Valute ID=\"X\"><CharCode>ZZ</CharCode><Nominal>1</Nominal><Value>1</Value></Valute></ValCurs>";
        var (date, rows) = CbrRateImporter.Parse(Encoding.GetEncoding(1251).GetBytes(xml));
        Assert.Equal(new DateOnly(2026, 10, 8), date);
        Assert.Equal([new CbrRateRow("USD", 1, 81.5m), new CbrRateRow("KZT", 100, 15.1234m)], rows);
        Assert.Throws<BusinessRuleException>(() => CbrRateImporter.Parse(Encoding.UTF8.GetBytes("<ValCurs/>")));
        Assert.Throws<BusinessRuleException>(() => CbrRateImporter.Parse([]));
    }

    [Fact]
    public void Supplier_upd_reads_501_attributes_individual_entrepreneur_and_gtin_from_marking()
    {
        var xml = "<Файл><Документ><СвСчФакт НомерСчФ=\"7\" ДатаСчФ=\"01.10.2026\">" +
                  "<СвПрод><ИдСв><СвИП ИННФЛ=\"500100732259\"><ФИО Фамилия=\"Петров\" Имя=\"Пётр\"/></СвИП></ИдСв></СвПрод>" +
                  "<СвПокуп><ИдСв><СвЮЛУч НаимОрг=\"Мы\" ИННЮЛ=\"7707083893\"/></ИдСв></СвПокуп></СвСчФакт>" +
                  "<ТаблСчФакт><СведТов НомСтр=\"1\" НаимТов=\"Свитер\" КолТов=\"2.5\" ЦенаТов=\"10\" НалСт=\"22%\">" +
                  "<ДопСведТов КодТов=\"A1\"><НомСредИдентТов><КИЗ>0104601234567886215ABC</КИЗ></НомСредИдентТов></ДопСведТов>" +
                  "<ИнфПолФХЖ2 Идентиф=\"Артикул\" Значен=\"B2\"/><ИнфПолФХЖ2 Идентиф=\"Артикул\" Значен=\"B3\"/></СведТов></ТаблСчФакт></Документ></Файл>";
        var upd = SupplierUpdXml.Parse(Encoding.UTF8.GetBytes(xml));
        Assert.Equal(("7", new DateOnly(2026, 10, 1), "ИП Петров Пётр", "500100732259"), (upd.Number, upd.Date, upd.SellerName, upd.SellerInn));
        var line = Assert.Single(upd.Lines);
        Assert.Equal(("A1", "04601234567886", 2.5m, 22m), (line.Code, line.Barcode, line.Quantity, line.VatPercent));
        Assert.Equal("purchase.upd.lines", Assert.Throws<BusinessRuleException>(() => SupplierUpdXml.Parse(Encoding.UTF8.GetBytes(xml.Replace("<СведТов", "<X").Replace("</СведТов>", "</X>")))).Code);
    }
}
