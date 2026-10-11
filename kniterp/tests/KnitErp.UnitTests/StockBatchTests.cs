using KnitErp.Domain.Common;
using KnitErp.Domain.Warehousing;

namespace KnitErp.UnitTests;

/// <summary>D89: партия, строка разбивки и пересчёт кг ↔ шт ↔ места.</summary>
public sealed class StockBatchTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Batch_requires_number_and_validates_package_weight()
    {
        Assert.Throws<BusinessRuleException>(() => StockBatch.Create(1, 2, new StockBatchData("  ", null, null, null, null, null), Now));
        Assert.Throws<BusinessRuleException>(() => StockBatch.Create(1, 2, new StockBatchData("П-1", null, null, null, 0m, null), Now));
        Assert.Throws<BusinessRuleException>(() => StockBatch.Create(1, 2, new StockBatchData("П-1", null, null, null, 1.2345m, null), Now));

        var batch = StockBatch.Create(1, 2, new StockBatchData(" П-1 ", " бордо ", "2231", null, 1.25m, null), Now);
        Assert.Equal("П-1", batch.Number);
        Assert.Equal("бордо", batch.Color);
        Assert.Equal("П-1 · бордо · кр. 2231", StockBatch.Describe(batch.Number, batch.Color, batch.DyeLot));
        Assert.Equal("П-1", StockBatch.Describe("П-1", null, null));

        var changes = batch.Update(new StockBatchData("П-1", "синий", "2231", null, 1.25m, null));
        Assert.Equal("Цвет", Assert.Single(changes).Field);
    }

    [Fact]
    public void Batch_with_balance_cannot_be_archived_and_archived_is_read_only()
    {
        var batch = StockBatch.Create(1, 2, new StockBatchData("П-1", null, null, null, null, null), Now);
        Assert.Equal("stock.batch.has_balance", Assert.Throws<BusinessRuleException>(() => batch.Archive(0.5m)).Code);
        batch.Archive(0);
        Assert.True(batch.IsArchived);
        Assert.Throws<BusinessRuleException>(() => batch.Update(new StockBatchData("П-2", null, null, null, null, null)));
        batch.Restore();
        Assert.False(batch.IsArchived);
    }

    [Fact]
    public void Batch_line_checks_quantity_packages_and_gross()
    {
        Assert.Throws<BusinessRuleException>(() => StockDocumentBatchLine.Create(1, 1, 1, 1, 0m, null, null));
        Assert.Throws<BusinessRuleException>(() => StockDocumentBatchLine.Create(1, 1, 1, 1, 5m, 0, null));
        Assert.Throws<BusinessRuleException>(() => StockDocumentBatchLine.Create(1, 1, 1, 1, 5m, 4, -1m));
        var line = StockDocumentBatchLine.Create(1, 1, 1, 1, 5m, 4, 5.3m);
        Assert.Equal(4, line.Packages);
    }

    [Fact]
    public void Movement_cannot_be_zero()
    {
        Assert.Throws<BusinessRuleException>(() =>
            StockBatchMovement.Create(1, 1, 1, 1, 0m, new DateOnly(2026, 10, 11), StockSource.StockDocument, 1, Now));
    }

    [Theory]
    [InlineData("166", null, 12.5, 12.5)]
    [InlineData("163", null, 2500, 2.5)]
    [InlineData("168", null, 0.25, 250)]
    [InlineData("796", 0.35, 10, 3.5)]
    public void Quantity_converts_to_kilograms(string unitCode, double? weight, double quantity, double kg) =>
        Assert.Equal((decimal)kg, WeightConversion.ToKg((decimal)quantity, unitCode, (decimal?)weight));

    [Fact]
    public void Conversion_needs_item_weight_for_pieces_and_rounds_back_to_unit_precision()
    {
        Assert.Null(WeightConversion.ToKg(10m, "796", null));
        Assert.Null(WeightConversion.ToKg(10m, "796", 0m));
        Assert.Equal(29m, WeightConversion.FromKg(10m, "796", 0.35m, 0)); // 28,57… шт → 29 шт
        Assert.Equal(2500m, WeightConversion.FromKg(2.5m, "163", null, 1));
        Assert.True(WeightConversion.IsWeightUnit("166"));
        Assert.False(WeightConversion.IsWeightUnit("796"));
    }

    [Fact]
    public void Kilograms_convert_to_packages_by_package_weight()
    {
        Assert.Equal(16m, WeightConversion.Packages(20m, 1.25m));
        Assert.Equal(3.3m, WeightConversion.Packages(4m, 1.2m));
        Assert.Null(WeightConversion.Packages(4m, null));
    }
}
