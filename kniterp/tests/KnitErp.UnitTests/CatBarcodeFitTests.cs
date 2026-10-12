using KnitErp.Domain.Catalog;

namespace KnitErp.UnitTests;

/// <summary>
/// D316: порог читаемости штрихкода сверен с декодером zxing-cpp: 33 кода × 6 образцов этикеток × 203 и 300 dpi (сессия 4, 12.10.2026).
/// Не прочитались только длинные Code 128 на 30×20 и 43×25 при 203 dpi — их предупреждение обязано ловить; EAN-13 читался везде.
/// </summary>
public sealed class CatBarcodeFitTests
{
    // Ширина содержимого = ширина − 2 × поля 2 мм; высота штрихкода — из образца.
    [Theory]
    [InlineData("HAT/2026-10", 26, 11)] // 30×20 — не прочитался
    [InlineData("MS-7F3A", 26, 11)]     // 30×20 — не прочитался
    [InlineData("HAT/2026-10", 39, 10)] // 43×25 — не прочитался
    public void Unreadable_cases_from_decoder_are_flagged(string code, decimal width, decimal height) =>
        Assert.False(BarcodeFit.Readable(code, width, height));

    [Theory]
    [InlineData("5260181590836", 26, 11)]  // EAN-13 на самой маленькой — читался
    [InlineData("4600000000008", 39, 10)]
    [InlineData("HAT/2026-10", 54, 11)]    // 58×40 — читался
    [InlineData("HAT/2026-10", 101, 22)]   // A4 105×74 — читался
    [InlineData("KN-00042", 36, 14)]       // 40×30 — читался
    public void Readable_cases_are_not_flagged(string code, decimal width, decimal height) =>
        Assert.True(BarcodeFit.Readable(code, width, height));

    [Fact]
    public void Required_width_hint()
    {
        Assert.True(BarcodeFit.RequiredWidthMm("HAT/2026-10") > 39);
        Assert.True(BarcodeFit.RequiredWidthMm("4600000000008") <= 26);
    }
}
