using System.Text;
using KnitErp.Application.Crm;

namespace KnitErp.UnitTests;

/// <summary>D697: CSV для импорта CRM — разделители, кавычки, переносы в поле, кодировки.</summary>
public sealed class CrmCsvTests
{
    [Fact]
    public void Semicolon_quotes_and_multiline_fields()
    {
        var csv = "Название;Компания;Комментарий\r\n\"Опт; свитера\";\"ООО \"\"Ромашка\"\"\";\"строка 1\nстрока 2\"\r\n\r\nШапки;ООО «Альфа»;\n";
        var rows = CrmCsv.Parse(Encoding.UTF8.GetBytes(csv), 100);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Опт; свитера", "ООО \"Ромашка\"", "строка 1\nстрока 2"], rows[1]);
        Assert.Equal(["Шапки", "ООО «Альфа»", ""], rows[2]);
    }

    [Theory]
    [InlineData("a,b,c\n1,2,3", ',')]
    [InlineData("a\tb\tc\n1\t2\t3", '\t')]
    [InlineData("a;b;c\n1;2;3", ';')]
    public void Delimiter_is_detected_from_header(string csv, char _) =>
        Assert.Equal(["1", "2", "3"], CrmCsv.Parse(Encoding.UTF8.GetBytes(csv), 10)[1]);

    [Fact]
    public void Windows_1251_and_utf8_bom_are_decoded()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1251 = Encoding.GetEncoding(1251).GetBytes("Имя;Телефон\nОльга;+7 900 000-00-01");
        Assert.Equal("Ольга", CrmCsv.Parse(cp1251, 10)[1][0]);
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("Имя;Телефон\nПётр;1")).ToArray();
        Assert.Equal("Имя", CrmCsv.Parse(bom, 10)[0][0]);
    }

    [Fact]
    public void Row_limit_and_xlsx_signature()
    {
        var csv = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"a{i};b"));
        Assert.Equal(6, CrmCsv.Parse(Encoding.UTF8.GetBytes(csv), 5).Count);
        Assert.True(CrmCsv.IsXlsx([0x50, 0x4B, 0x03, 0x04, 0]));
        Assert.False(CrmCsv.IsXlsx("a;b"u8));
    }
}
