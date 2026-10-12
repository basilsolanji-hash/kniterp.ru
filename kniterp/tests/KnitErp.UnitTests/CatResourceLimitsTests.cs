using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>Ревью безопасности: ввод пользователя не может занять сервер бесконечным циклом или перебором миллионов сочетаний.</summary>
public sealed class CatResourceLimitsTests
{
    [Fact(Timeout = 5000)]
    public async Task Tiny_range_step_is_rejected_instead_of_looping_forever()
    {
        await Task.Yield();
        Assert.Equal("catalog.value_set.step", Assert.Throws<BusinessRuleException>(() => CharacteristicValueSet.Expand("10-10/0.0000000000000000000000000001")).Code);
        Assert.Equal("catalog.value_set.step", Assert.Throws<BusinessRuleException>(() => CharacteristicValueSet.Expand("1-2/0.001")).Code);
        Assert.Equal(["1", "1.5", "2"], CharacteristicValueSet.Expand("1-2/0.5"));
        Assert.Equal(["10"], CharacteristicValueSet.Expand("10-10/0.01"));
        Assert.Equal(["42", "44", "46"], CharacteristicValueSet.Expand("42-46/2"));
    }

    [Fact(Timeout = 5000)]
    public async Task Combinations_are_counted_before_they_are_built()
    {
        await Task.Yield();
        var hundred = Enumerable.Range(1, 100).Select(i => i.ToString()).ToList();
        var axes = Enumerable.Range(1, 4).ToDictionary(i => (long)i, _ => (IReadOnlyList<string>)hundred);
        var ex = Assert.Throws<BusinessRuleException>(() => Variants.Matrix(axes));
        Assert.Equal("catalog.variant.too_many", ex.Code);
        Assert.Contains("100000000", ex.Message);
        Assert.Equal(4, Variants.Matrix(new Dictionary<long, IReadOnlyList<string>> { [1] = ["a", "b"], [2] = ["46", "48"] }).Count);
    }
}
