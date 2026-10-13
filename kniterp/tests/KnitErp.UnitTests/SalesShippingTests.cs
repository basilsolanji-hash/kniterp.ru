using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;

namespace KnitErp.UnitTests;

/// <summary>D1108, D1150, D1153: разбор кодов маркировки, сборка по строкам, качество возврата.</summary>
public sealed class SalesShippingTests
{
    private const string Gtin = "04601234567886";

    [Theory]
    [InlineData("0104601234567886215ABCDEFGHIJKL\u001d91EE10\u001d92xyz", "5ABCDEFGHIJKL")]
    [InlineData("]d20104601234567886215ABCDEFGHIJKL<GS>93ABCD", "5ABCDEFGHIJKL")]
    [InlineData("0104601234567886215ABCDEFGHIJKL91EE1092abcdef", "5ABCDEFGHIJKL")]
    [InlineData("0104601234567886215ABCDEFGHIJKL93ABCD", "5ABCDEFGHIJKL")]
    [InlineData("010460123456788621abc", "abc")]
    public void Parses_gs1_datamatrix(string raw, string serial)
    {
        var parts = MarkingCodes.Parse(raw);
        Assert.Equal((Gtin, serial), (parts.Gtin, parts.Serial));
        Assert.Equal("01" + Gtin + "21" + serial, parts.Cis);
    }

    [Theory]
    [InlineData("", "sales.marking.empty")]
    [InlineData("4601234567886", "sales.marking.format")]
    [InlineData("0104601234567887215ABC", "sales.marking.gtin_check")]
    [InlineData("0104601234567886215ABCDEFGHIJKLMNOPQRSTUVWXYZ", "sales.marking.separator")]
    [InlineData("01046012345678862159 ABC", "sales.marking.serial")]
    public void Rejects_bad_codes(string raw, string code) =>
        Assert.Equal(code, Assert.Throws<BusinessRuleException>(() => MarkingCodes.Parse(raw)).Code);

    [Fact]
    public void Pick_list_is_picked_when_all_lines_are_picked()
    {
        var list = SalesPickList.Create(1, "СБ-1", new DateOnly(2026, 10, 9), 5, null, [(10, 100, 3m, " А-1 "), (11, 101, 2m, null)], 7, DateTime.UtcNow);
        Assert.Equal("А-1", list.Lines.First().Location);
        list.PickAll(7, DateTime.UtcNow);
        Assert.Equal(PickListStatus.Picked, list.Status);
        Assert.Throws<BusinessRuleException>(() => SalesPickList.Create(1, "СБ-2", new DateOnly(2026, 10, 9), 5, null, [], 7, DateTime.UtcNow));
        Assert.Throws<BusinessRuleException>(() => list.Cancel()); // собранное задание не отменяется
        var open = SalesPickList.Create(1, "СБ-3", new DateOnly(2026, 10, 9), 5, null, [(10, 100, 1m, null)], 7, DateTime.UtcNow);
        open.Cancel();
        Assert.Equal(PickListStatus.Cancelled, open.Status);
        Assert.Throws<BusinessRuleException>(() => open.PickAll(7, DateTime.UtcNow));
    }

    [Fact]
    public void Return_defects_need_quarantine_and_write_off_reason()
    {
        var details = SalesReturnDetails.Create(1, 2);
        details.SetLine(100, ReturnQuality.Quarantine, 2);
        details.SetLine(101, ReturnQuality.WriteOff, 1);
        var lines = new Dictionary<long, decimal> { [100] = 2, [101] = 3 };
        Assert.Equal("sales.return.quarantine_required", Assert.Throws<BusinessRuleException>(() => details.Validate(9, lines)).Code);
        details.SetHeader(null, null, 9, null);
        Assert.Equal("sales.return.quarantine_same", Assert.Throws<BusinessRuleException>(() => details.Validate(9, lines)).Code);
        Assert.Equal("sales.return.writeoff_reason", Assert.Throws<BusinessRuleException>(() => details.Validate(8, lines)).Code);
        details.SetHeader(null, null, 9, 4);
        details.Validate(8, lines);
        details.SetLine(101, ReturnQuality.Good, 0);
        Assert.Single(details.Lines);
        Assert.Equal("sales.return.quality_exceeds", Assert.Throws<BusinessRuleException>(() =>
            details.Validate(8, new Dictionary<long, decimal> { [100] = 1 })).Code);
    }
}
