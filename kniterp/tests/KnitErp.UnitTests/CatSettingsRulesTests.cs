using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>D312: правила общих настроек каталога — автокод, виды позиций, проверка до изменения.</summary>
public sealed class CatSettingsRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Defaults_enable_everything_and_require_manual_code()
    {
        var s = CatalogSettings.Default(1);
        Assert.True(s.IsEnabled(ItemType.Service) && s.IsEnabled(ItemType.Bundle));
        Assert.Null(s.PreviewCode());
        Assert.Equal("catalog.code.required", Assert.Throws<BusinessRuleException>(() => s.TakeCode()).Code);
    }

    [Fact]
    public void Auto_code_pads_number_and_advances()
    {
        var s = CatalogSettings.Default(1);
        s.Set(true, true, " тв- ", 4, 98, ItemType.Finished, Now);
        Assert.Equal(("ТВ-0098", "ТВ-0098", "ТВ-0099", "ТВ-0100"), (s.PreviewCode(), s.TakeCode(), s.TakeCode(), s.PreviewCode()));
    }

    [Theory]
    [InlineData("А Б", 5, 1, "catalog.settings.prefix")]
    [InlineData("ОЧЕНЬ-ДЛИННЫЙ", 5, 1, "catalog.settings.prefix")]
    [InlineData("А-", 2, 1, "catalog.settings.digits")]
    [InlineData("А-", 9, 1, "catalog.settings.digits")]
    [InlineData("А-", 3, 0, "catalog.settings.next")]
    [InlineData("А-", 3, 1000, "catalog.settings.next")]
    public void Invalid_settings_are_rejected_without_change(string prefix, int digits, int next, string code)
    {
        var s = CatalogSettings.Default(1);
        Assert.Equal(code, Assert.Throws<BusinessRuleException>(() => s.Set(false, false, prefix, (byte)digits, next, ItemType.Finished, Now)).Code);
        Assert.True(s.ServicesEnabled && s.BundlesEnabled && s.AutoCodePrefix is null);
    }

    [Fact]
    public void Disabled_kind_blocks_creation_and_default_type()
    {
        var s = CatalogSettings.Default(1);
        Assert.Equal("catalog.settings.default_type",
            Assert.Throws<BusinessRuleException>(() => s.Set(false, true, null, 5, 1, ItemType.Service, Now)).Code);
        Assert.True(s.ServicesEnabled);
        s.Set(false, false, null, 5, 1, ItemType.Finished, Now);
        Assert.Equal("catalog.kind_disabled", Assert.Throws<BusinessRuleException>(() => s.EnsureEnabled(ItemType.Service)).Code);
        Assert.Equal("catalog.kind_disabled", Assert.Throws<BusinessRuleException>(() => s.EnsureEnabled(ItemType.Bundle)).Code);
        s.EnsureEnabled(ItemType.Finished);
    }
}
