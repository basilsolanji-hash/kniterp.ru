using KnitErp.Application.Sales;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using Xunit;

namespace KnitErp.UnitTests;

/// <summary>Разбор отчётов маркетплейсов (D1168): числа, даты, заголовки, виды строк, отпечаток; план продаж (D1162).</summary>
public sealed class MarketplaceFormatsTests
{
    [Theory]
    [InlineData("1 234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("12,5 ₽", 12.5)]
    [InlineData("-300", -300)]
    [InlineData("−42.1", -42.1)]
    [InlineData("1 000 000", 1000000)]
    [InlineData("", 0)]
    public void Numbers_in_russian_and_invariant_notation(string text, double expected) =>
        Assert.Equal((decimal)expected, MarketplaceFormats.ParseNumber(text, "Сумма"));

    [Fact]
    public void Bad_number_and_date_name_the_column()
    {
        var ex = Assert.Throws<MarketplaceCellException>(() => MarketplaceFormats.ParseNumber("abc", "Кол-во"));
        Assert.Equal(("Кол-во", "abc", false), (ex.Column, ex.Value, ex.IsDate));
        Assert.True(Assert.Throws<MarketplaceCellException>(() => MarketplaceFormats.ParseDate("вчера", "Дата")).IsDate);
        Assert.Equal(new DateOnly(2026, 10, 5), MarketplaceFormats.ParseDate("2026-10-05T12:30:00", "Дата"));
        Assert.Equal(new DateOnly(2026, 10, 5), MarketplaceFormats.ParseDate("05.10.2026", "Дата"));
        Assert.Null(MarketplaceFormats.ParseDate(" ", "Дата"));
    }

    [Fact]
    public void Wildberries_rows_become_sales_returns_and_services_with_same_fingerprint_for_same_content()
    {
        IReadOnlyList<IReadOnlyList<string>> sheet =
        [
            ["Еженедельный отчёт", "", "", "", "", "", ""],
            ["Артикул  поставщика", "КОД НОМЕНКЛАТУРЫ", "Тип документа", "Обоснование для оплаты", "Кол-во", "Вайлдберриз реализовал Товар (Пр)",
                "Услуги по доставке товара покупателю"],
            ["A1", "1", "Продажа", "Продажа", "2", "100", "0"],
            ["A1", "1", "Возврат", "Возврат", "1", "50", "0"],
            ["A1", "1", "", "Логистика", "0", "0", "30"],
            ["", "", "", "", "", "", ""],
        ];
        var result = MarketplaceFormats.Parse(MarketplaceKind.Wildberries, sheet, MarketplaceFormats.Wildberries, 1);
        Assert.Equal(2, result.HeaderRow);
        Assert.Equal([MarketplaceLineKind.Sale, MarketplaceLineKind.Return, MarketplaceLineKind.Service], result.Lines.Select(l => l.Kind));
        Assert.Equal(30m, result.Lines[2].Logistics);
        Assert.Equal(result.Fingerprint, MarketplaceFormats.Parse(MarketplaceKind.Wildberries, sheet, MarketplaceFormats.Wildberries, 1).Fingerprint);
        Assert.NotEqual(result.Fingerprint, MarketplaceFormats.Parse(MarketplaceKind.Ozon, sheet, MarketplaceFormats.Wildberries, 1).Fingerprint);
        Assert.Equal("marketplace.header", Assert.Throws<BusinessRuleException>(() =>
            MarketplaceFormats.Parse(MarketplaceKind.Ozon, sheet, MarketplaceFormats.Ozon, 1)).Code);
    }

    [Fact]
    public void Plan_amount_and_month()
    {
        var plan = SalesPlan.Create(1, new DateOnly(2026, 10, 17), 5);
        Assert.Equal(new DateOnly(2026, 10, 1), plan.Month);
        Assert.Equal("sales.plan.amount", Assert.Throws<BusinessRuleException>(() => plan.Set(0.001m, 1, DateTime.UtcNow)).Code);
        plan.Set(1000.5m, 1, DateTime.UtcNow);
        Assert.Equal(1000.5m, plan.Amount);
    }
}
