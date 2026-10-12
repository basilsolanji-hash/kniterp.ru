using System.Text;
using KnitErp.Application.Finance;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>D119 (А1): разбор выписки 1С «Клиент-банк» — кодировки, документы, отпечаток для защиты от повторной загрузки.</summary>
public sealed class ClientBankStatementTests
{
    private const string Sample = """
        1CClientBankExchange
        ВерсияФормата=1.03
        Кодировка=Windows
        ДатаНачала=01.10.2026
        ДатаКонца=11.10.2026
        РасчСчет=40702810900000000001
        СекцияРасчСчет
        ДатаНачала=01.10.2026
        РасчСчет=40702810900000000001
        КонецРасчСчет
        СекцияДокумент=Платежное поручение
        Номер=15
        Дата=05.10.2026
        Сумма=12500.50
        ПлательщикСчет=40702810500000000077
        ПлательщикИНН=7701234567
        Плательщик1=ООО "Магазин"
        ПолучательСчет=40702810900000000001
        ПолучательИНН=7707654321
        Получатель1=ООО "Фабрика"
        НазначениеПлатежа=Оплата по счёту № 12 от 01.10.2026, в т.ч. НДС
        ДатаПоступило=05.10.2026
        КонецДокумента
        КонецФайла
        """;

    [Theory]
    [InlineData(1251)]
    [InlineData(866)]
    [InlineData(65001)]
    public void Parses_documents_in_any_bank_encoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = codePage switch { 866 => Sample.Replace("Кодировка=Windows", "Кодировка=DOS"), 65001 => Sample.Replace("Кодировка=Windows\n", ""), _ => Sample };
        var statement = ClientBankStatement.Parse(Encoding.GetEncoding(codePage).GetBytes(text));

        Assert.Equal(["40702810900000000001"], statement.Accounts);
        Assert.Equal(new DateOnly(2026, 10, 1), statement.From);
        var d = Assert.Single(statement.Documents);
        Assert.Equal(("15", new DateOnly(2026, 10, 5), 12500.50m), (d.Number, d.Date, d.Amount));
        Assert.Equal(("7701234567", "ООО \"Магазин\""), (d.PayerInn, d.PayerName));
        Assert.Contains("№ 12", d.Purpose);
        Assert.Equal(new DateOnly(2026, 10, 5), d.Credited);
    }

    [Fact]
    public void Same_document_has_same_fingerprint_and_other_amount_differs()
    {
        var a = ClientBankStatement.Parse(Win1251(Sample)).Documents[0];
        var b = ClientBankStatement.Parse(Win1251(Sample.Replace("Сумма=12500.50", "Сумма=12500.51"))).Documents[0];
        Assert.Equal(ClientBankStatement.Fingerprint(a), ClientBankStatement.Fingerprint(a with { Index = 7 }));
        Assert.NotEqual(ClientBankStatement.Fingerprint(a), ClientBankStatement.Fingerprint(b));
    }

    [Fact]
    public void Rejects_foreign_format_and_broken_document()
    {
        Assert.Equal("bank.statement.format", Assert.Throws<BusinessRuleException>(() =>
            ClientBankStatement.Parse(Encoding.UTF8.GetBytes("Дата;Сумма\n05.10.2026;100"))).Code);
        Assert.Equal("bank.statement.document", Assert.Throws<BusinessRuleException>(() =>
            ClientBankStatement.Parse(Win1251(Sample.Replace("Сумма=12500.50", "Сумма=")))).Code);
    }

    private static byte[] Win1251(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251).GetBytes(text);
    }
}
