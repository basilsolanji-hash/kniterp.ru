using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;

namespace KnitErp.UnitTests;

/// <summary>Расценки, доплаты, маска ПДн (D210–D212).</summary>
public sealed class HrPayrollRulesTests
{
    private static readonly DateOnly Dec1 = new(2026, 12, 1);
    private static readonly DateTime Now = new(2026, 12, 1, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Rate_on_date_prefers_item_and_latest_valid_from()
    {
        PieceRate[] rates =
        [
            PieceRate.Create(1, 10, "Вязка", null, "шт", 100m, Dec1, 1, Now),
            PieceRate.Create(1, 10, "Вязка", null, "шт", 150m, Dec1.AddDays(14), 1, Now),
            PieceRate.Create(1, 10, "Вязка модели 7", 7, "шт", 200m, Dec1, 1, Now),
        ];
        Assert.Equal(100m, PieceRate.Find(rates, 10, null, Dec1.AddDays(5))!.Price);
        Assert.Equal(150m, PieceRate.Find(rates, 10, 3, Dec1.AddDays(20))!.Price);
        Assert.Equal(200m, PieceRate.Find(rates, 10, 7, Dec1.AddDays(20))!.Price);
        Assert.Null(PieceRate.Find(rates, 10, null, Dec1.AddDays(-1)));
        Assert.Null(PieceRate.Find(rates, 11, null, Dec1));
    }

    [Fact]
    public void Rate_validation()
    {
        Assert.Equal("hr.rate.unit", Assert.Throws<BusinessRuleException>(() => PieceRate.Create(1, 1, "x", null, " . ", 1, Dec1, 1, Now)).Code);
        Assert.Equal("пар", PieceRate.Create(1, 1, "Вязка носков", null, "Пар.", 1, Dec1, 1, Now).Unit);
        Assert.Equal("hr.rate.price", Assert.Throws<BusinessRuleException>(() => PieceRate.Create(1, 1, "x", null, "кг", 0, Dec1, 1, Now)).Code);
    }

    [Theory]
    [InlineData("шт", "шт")]
    [InlineData(" Шт. ", "шт")]
    [InlineData("ПАР", "пар")]
    [InlineData(null, "")]
    public void Unit_is_normalized(string? unit, string expected) => Assert.Equal(expected, PieceRate.NormalizeUnit(unit));

    [Theory]
    [InlineData(1, 300)]   // 1 ч × 200 × 1,5
    [InlineData(2, 600)]
    [InlineData(3, 1000)]  // 2 × 1,5 + 1 × 2
    [InlineData(5, 1800)]
    public void Overtime_first_two_hours_one_and_half_then_double(double hours, double expected) =>
        Assert.Equal((decimal)expected, PayrollRules.OvertimeDay((decimal)hours, 200m));

    [Fact]
    public void Salary_part_is_proportional_and_capped_by_norm()
    {
        Assert.Equal(18300m, PayrollRules.SalaryPart(36600m, 91.5m, 183m));
        Assert.Equal(36600m, PayrollRules.SalaryPart(36600m, 200m, 183m));
        Assert.Equal(0m, PayrollRules.SalaryPart(36600m, 10m, 0m));
    }

    [Fact]
    public void Run_is_approved_only_with_closed_timesheets()
    {
        var run = PayrollRun.Create(1, 2026, 12, 1, Now);
        Assert.Equal("hr.payroll.timesheets_open", Assert.Throws<BusinessRuleException>(() => run.Approve(1, Now)).Code);
        run.Recalculated(true, new byte[32], 1, Now);
        run.Approve(1, Now);
        Assert.Equal("hr.payroll.approved", Assert.Throws<BusinessRuleException>(run.EnsureDraft).Code);
    }

    [Theory]
    [InlineData("СНИЛС 112-233-445 95", "СНИЛС [скрыто]")]
    [InlineData("паспорт 4500 000001 выдан", "паспорт [скрыто] выдан")]
    [InlineData("звоните +7 (900) 000-00-00", "звоните [скрыто]")]
    [InlineData("Заказ № 12345, 40 шт по 1 500 руб.", "Заказ № 12345, 40 шт по 1 500 руб.")]
    public void Mask_hides_long_numbers_only(string text, string expected) => Assert.Equal(expected, PersonalDataMask.Mask(text));

    [Fact]
    public void Output_fingerprint_ignores_order_and_unit_spelling_but_sees_quantity()
    {
        var a = new KnitErp.Application.Hr.WorkOutputDto(1, new DateOnly(2026, 12, 3), 1, "Вязание", null, 10, "шт");
        var b = new KnitErp.Application.Hr.WorkOutputDto(2, new DateOnly(2026, 12, 4), 2, "Сборка", 7, 1.5m, "пар");
        var f = KnitErp.Application.Hr.WorkOutputFingerprint.Of([a, b]);
        Assert.Equal(f, KnitErp.Application.Hr.WorkOutputFingerprint.Of([b with { Unit = "Пар." }, a with { OperationName = "вязание" }]));
        Assert.NotEqual(f, KnitErp.Application.Hr.WorkOutputFingerprint.Of([a with { Quantity = 10.000001m }, b]));
        Assert.NotEqual(f, KnitErp.Application.Hr.WorkOutputFingerprint.Of([a]));
        Assert.Equal(32, f.Length);
    }
}
