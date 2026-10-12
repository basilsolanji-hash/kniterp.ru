using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>D321: раскрытие значений набора — списки, диапазоны с шагом, парные размеры, ограничения.</summary>
public sealed class CatValueSetTests
{
    [Theory]
    [InlineData("42-56/2", "42, 44, 46, 48, 50, 52, 54, 56")]
    [InlineData("98–164/6", "98, 104, 110, 116, 122, 128, 134, 140, 146, 152, 158, 164")]
    [InlineData("44-46, 48-50", "44-46, 48-50")]                 // парные размеры трикотажа — одно значение
    [InlineData("XS, S, m, M", "XS, S, m")]                      // повтор без учёта регистра убран
    [InlineData("1-2/0.5", "1, 1.5, 2")]                         // дробный шаг — через точку (запятая разделяет значения)
    [InlineData("красный; синий\nбелый", "красный, синий, белый")]
    public void Expand(string text, string expected) => Assert.Equal(expected, string.Join(", ", CharacteristicValueSet.Expand(text)));

    [Theory]
    [InlineData("", "catalog.value_set.empty")]
    [InlineData("56-42/2", "catalog.value_set.range")]
    [InlineData("1-500/1", "catalog.value_set.range")]
    [InlineData("42-56/0", "catalog.value_set.step")]
    public void Invalid(string text, string code) =>
        Assert.Equal(code, Assert.Throws<BusinessRuleException>(() => CharacteristicValueSet.Expand(text)).Code);

    [Fact]
    public void Presets_are_valid() =>
        Assert.All(CharacteristicValueSet.Presets, p => Assert.NotEmpty(CharacteristicValueSet.Create(1, 1, p.Name, p.Values).Values));
}
