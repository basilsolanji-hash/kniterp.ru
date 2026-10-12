using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>D316: правила шаблона этикетки; все образцы — корректные шаблоны.</summary>
public sealed class CatLabelRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static LabelElement El(LabelField f, string? text = null, byte font = 8, byte barcode = 0) =>
        new(f, text, font, false, LabelAlign.Left, false, barcode);

    [Fact]
    public void Every_preset_is_a_valid_template_and_round_trips_elements()
    {
        foreach (var p in LabelTemplate.Presets)
        {
            var t = LabelTemplate.Create(1, p.Name, p.WidthMm, p.HeightMm, p.Sheet, p.Columns, p.Rows, 2, null, p.Elements, Now);
            Assert.Equal(p.Elements, t.Elements);
        }
    }

    [Theory]
    [InlineData(10, 40, LabelSheet.Roll, 1, 1, "label.size")]
    [InlineData(58, 151, LabelSheet.Roll, 1, 1, "label.size")]
    [InlineData(70, 37, LabelSheet.A4, 4, 8, "label.a4")]   // 4 × 70 = 280 мм > 210
    [InlineData(70, 37, LabelSheet.A4, 3, 9, "label.a4")]   // 9 × 37 = 333 мм > 297
    public void Size_and_sheet_limits(decimal w, decimal h, LabelSheet sheet, byte cols, byte rows, string code) =>
        Assert.Equal(code, Assert.Throws<BusinessRuleException>(() =>
            LabelTemplate.Create(1, "Т", w, h, sheet, cols, rows, 2, null, [El(LabelField.Name)], Now)).Code);

    [Fact]
    public void Element_rules_are_checked_before_change()
    {
        var t = LabelTemplate.Create(1, "Ценник", 58, 40, LabelSheet.Roll, 3, 9, 2, null, [El(LabelField.Name)], Now);
        Assert.Equal((1, 1), ((int)t.Columns, (int)t.Rows)); // у рулона сетки нет

        Assert.Equal("label.custom_text", Assert.Throws<BusinessRuleException>(() => t.Set("Ценник", 58, 40, LabelSheet.Roll, 1, 1, 2, null,
            [El(LabelField.CustomText, " ")], Now)).Code);
        Assert.Equal("label.barcode_height", Assert.Throws<BusinessRuleException>(() => t.Set("Ценник", 58, 40, LabelSheet.Roll, 1, 1, 2, null,
            [El(LabelField.Barcode, barcode: 40)], Now)).Code);
        Assert.Equal("label.font", Assert.Throws<BusinessRuleException>(() => t.Set("Ценник", 58, 40, LabelSheet.Roll, 1, 1, 2, null,
            [El(LabelField.Name, font: 2)], Now)).Code);
        Assert.Equal("label.elements", Assert.Throws<BusinessRuleException>(() => t.Set("Ценник", 58, 40, LabelSheet.Roll, 1, 1, 2, null, [], Now)).Code);
        Assert.Equal("label.padding", Assert.Throws<BusinessRuleException>(() => t.Set("Ценник", 20, 15, LabelSheet.Roll, 1, 1, 8, null,
            [El(LabelField.Name)], Now)).Code);
        Assert.Equal(("Ценник", 58m), (t.Name, t.WidthMm));
        Assert.Equal([El(LabelField.Name)], t.Elements);

        // Высота штрихкода хранится только у строки «Штрихкод»; подпись — как есть (с пробелом), «свой текст» — без пробелов по краям.
        t.Set("Ценник 2", 58, 40, LabelSheet.Roll, 1, 1, 2, null,
            [El(LabelField.Price, "Цена: ", barcode: 30), El(LabelField.Barcode, barcode: 12), El(LabelField.CustomText, "  Ручная работа ")], Now);
        Assert.Equal([El(LabelField.Price, "Цена: "), El(LabelField.Barcode, barcode: 12), El(LabelField.CustomText, "Ручная работа")], t.Elements);
    }
}
