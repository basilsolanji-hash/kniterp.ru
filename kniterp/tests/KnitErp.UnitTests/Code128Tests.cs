using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;

namespace KnitErp.UnitTests;

/// <summary>D101: Code 128 сверен с независимой реализацией (python-barcode 0.16): строки модулей ниже получены ею.</summary>
public sealed class Code128Tests
{
    [Theory]
    [InlineData("KB00000123", "1101001000010110001110100010110001011101111011011001100110110011001100110110011101101110110010000101100011101011")]
    [InlineData("KB00004567", "1101001000010110001110100010110001011101111011011001100110110011001011101100010000101100101110011001100011101011")]
    [InlineData("12345678", "1101001110010110011100100010110001110001011011000010100100011101101100011101011")]
    [InlineData("AB12", "1101001000010100011000100010110001001110011011001110010110010111001100011101011")]
    [InlineData("KB12345678", "1101001000010110001110100010110001011101111010110011100100010110001110001011011000010100100001011001100011101011")]
    public void Encodes_like_reference_implementation(string text, string modules)
    {
        var bits = string.Concat(Code128.Widths(text).Select((w, i) => new string(i % 2 == 0 ? '1' : '0', w)));
        Assert.Equal(modules, bits);
    }

    [Fact]
    public void Every_symbol_is_eleven_modules_and_stop_is_thirteen()
    {
        Assert.Equal(107, Code128.Patterns.Count);
        Assert.All(Code128.Patterns.Take(106), p => Assert.Equal(11, p.Sum(c => c - '0')));
        Assert.Equal(13, Code128.Patterns[106].Sum(c => c - '0'));
        Assert.Equal(107, Code128.Patterns.Distinct().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Партия")]
    public void Rejects_non_ascii_or_empty(string text) => Assert.Throws<BusinessRuleException>(() => Code128.Symbols(text));

    [Fact]
    public void Batch_code_round_trips_and_ignores_other_codes()
    {
        Assert.Equal("KB00000123", StockBatch.Code(123));
        Assert.Equal(123, StockBatch.ParseCode(" kb00000123 "));
        Assert.Equal(123456789, StockBatch.ParseCode(StockBatch.Code(123456789)));
        Assert.Null(StockBatch.ParseCode("4600000000000"));
        Assert.Null(StockBatch.ParseCode("KB"));
        Assert.Null(StockBatch.ParseCode("KBx1"));
    }
}
