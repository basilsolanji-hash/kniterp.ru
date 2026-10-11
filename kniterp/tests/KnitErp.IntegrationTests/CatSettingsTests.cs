using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Общие настройки каталога организации (D312): виды позиций, автокод, тип по умолчанию; права, аудит, конфликт версий, изоляция организаций.
/// </summary>
public sealed class CatSettingsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 937_000_000;

    [SqlFact]
    public async Task Auto_code_skips_taken_numbers_and_survives_failed_create()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var defaults = await s.CatalogSettings.GetAsync();
        Assert.Equal((true, true, null, ItemType.Finished, true), (defaults.ServicesEnabled, defaults.BundlesEnabled, defaults.NextCode, defaults.DefaultItemType,
            defaults.CanEdit));

        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        // Без автокода пустой код — ошибка, как раньше.
        await Assert.ThrowsAsync<BusinessRuleException>(() => s.Catalog.CreateItemAsync(new ItemCommand("", "Без кода", ItemType.Finished, piece, null)));

        await s.CatalogSettings.SaveAsync(new CatalogSettingsCommand(true, true, " тв- ", 4, 7, ItemType.RawMaterial), defaults.RowVersion);
        var saved = await s.CatalogSettings.GetAsync();
        Assert.Equal(("ТВ-", "ТВ-0007", ItemType.RawMaterial), (saved.AutoCodePrefix, saved.NextCode, saved.DefaultItemType));

        await s.Catalog.CreateItemAsync(new ItemCommand("ТВ-0008", "Занят вручную", ItemType.Finished, piece, null));
        var a = await s.Catalog.CreateItemAsync(new ItemCommand(" ", "Первая", ItemType.Finished, piece, null));
        var b = await s.Catalog.CreateItemAsync(new ItemCommand("", "Вторая", ItemType.Finished, piece, null));
        var codes = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ТВ-"))).Items.ToDictionary(i => i.Id, i => i.Code);
        Assert.Equal(("ТВ-0007", "ТВ-0009"), (codes[a], codes[b]));

        // Отказ после сдвига номера (услуга без НДС) не оставляет изменений в контексте: следующий номер в базе прежний.
        await Assert.ThrowsAsync<BusinessRuleException>(() => s.Catalog.CreateItemAsync(new ItemCommand("", "Услуга без НДС", ItemType.Service,
            piece, null)));
        await using (var db = host.NewDb())
        {
            Assert.Equal(10, (await db.CatalogSettings.SingleAsync(x => x.OrganizationId == org.OrganizationId)).NextNumber);
        }

        Assert.Equal("ТВ-0010", (await s.CatalogSettings.GetAsync()).NextCode);
        var c = await s.Catalog.CreateItemAsync(new ItemCommand("", "Третья", ItemType.Finished, piece, null));
        var after = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ТВ-"))).Items;
        Assert.Equal("ТВ-0010", after.Single(i => i.Id == c).Code);
        Assert.DoesNotContain(after, i => i.Name == "Услуга без НДС");
    }

    [SqlFact]
    public async Task Disabled_kinds_cannot_be_created_or_assigned_and_settings_validate_before_change()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var piece = units.Single(u => u.Code == "796").Id;
        var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
        var service = await s.Catalog.CreateItemAsync(new ItemCommand("УСЛ-1", "Доставка", ItemType.Service, piece, null, vat));
        var goods = await s.Catalog.CreateItemAsync(new ItemCommand("ТОВ-1", "Шапка", ItemType.Finished, piece, null));

        var rv = (await s.CatalogSettings.GetAsync()).RowVersion;
        // Тип по умолчанию из выключенного вида — отказ, настройки не меняются.
        Assert.Equal("catalog.settings.default_type", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.CatalogSettings.SaveAsync(new CatalogSettingsCommand(false, true, null, 5, 1, ItemType.Service), rv))).Code);
        Assert.True((await s.CatalogSettings.GetAsync()).ServicesEnabled);
        Assert.Equal("catalog.settings.prefix", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.CatalogSettings.SaveAsync(new CatalogSettingsCommand(true, true, "А Б", 5, 1, ItemType.Finished), rv))).Code);
        Assert.Equal("catalog.settings.next", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.CatalogSettings.SaveAsync(new CatalogSettingsCommand(true, true, "А-", 3, 1000, ItemType.Finished), rv))).Code);

        await s.CatalogSettings.SaveAsync(new CatalogSettingsCommand(false, false, null, 5, 1, ItemType.Finished), rv);
        Assert.Equal("catalog.kind_disabled", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.CreateItemAsync(new ItemCommand("УСЛ-2", "Монтаж", ItemType.Service, piece, null, vat)))).Code);
        Assert.Equal("catalog.kind_disabled", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.CreateItemAsync(new ItemCommand("НБ-1", "Набор", ItemType.Bundle, piece, null)))).Code);

        // Уже созданная услуга остаётся и редактируется; товар в услугу не превращается.
        var row = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "УСЛ-1"))).Items.Single();
        await s.Catalog.UpdateItemAsync(service, new ItemCommand(row.Code, "Доставка по городу", row.Type, row.UnitId, null, row.VatRateId), row.RowVersion);
        var g = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ТОВ-1"))).Items.Single();
        Assert.Equal("catalog.kind_disabled", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateItemAsync(goods, new ItemCommand(g.Code, g.Name, ItemType.Service, g.UnitId, null, vat), g.RowVersion))).Code);

        // Устаревшая версия — конфликт.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            s.CatalogSettings.SaveAsync(new CatalogSettingsCommand(true, true, null, 5, 1, ItemType.Finished), rv));

        await using var db = host.NewDb();
        Assert.True(await db.AuditEntries.AnyAsync(e => e.OrganizationId == org.OrganizationId && e.EntityType == nameof(CatalogSettings)));
    }

    [SqlFact]
    public async Task Only_catalog_editors_change_settings_and_organizations_are_isolated()
    {
        var org = await CreateOrgAsync();
        long accountant;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.CatalogSettings.SaveAsync(new CatalogSettingsCommand(false, true, "А-", 3, 1, ItemType.Finished), []);
            accountant = (await s.Access.InviteAsync(new InviteUserCommand($"acc-{Guid.NewGuid():N}@test.local", "Бухгалтер", SystemRoles.Accountant, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == accountant)).Activate();
            await db.SaveChangesAsync();
        }

        await using (var s = host.As(accountant, org.OrganizationId))
        {
            var view = await s.CatalogSettings.GetAsync();
            Assert.Equal((false, false), (view.ServicesEnabled, view.CanEdit));
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                s.CatalogSettings.SaveAsync(new CatalogSettingsCommand(true, true, null, 5, 1, ItemType.Finished), view.RowVersion));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            var view = await s.CatalogSettings.GetAsync();
            Assert.Equal((true, null), (view.ServicesEnabled, view.AutoCodePrefix));
        }
    }

    [SqlFact]
    public async Task Required_group_and_vat_are_checked_on_server_and_hidden_blocks_are_saved()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
        var group = await s.Nomenclature.CreateGroupAsync(null, "Шапки");
        var old = await s.Catalog.CreateItemAsync(new ItemCommand("СТАР-1", "Позиция до настройки", ItemType.Finished, piece, null));

        await s.CatalogSettings.SaveAsync(new CatalogSettingsCommand(true, true, null, 5, 1, ItemType.Finished, true, true,
            CardBlocks.Packs | CardBlocks.Suppliers), (await s.CatalogSettings.GetAsync()).RowVersion);
        var view = await s.CatalogSettings.GetAsync();
        Assert.Equal((true, true, false, false, true), (view.RequireGroup, view.RequireVat, view.Shows(CardBlocks.Packs), view.Shows(CardBlocks.Suppliers),
            view.Shows(CardBlocks.Trade)));

        Assert.Equal("catalog.required.group", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.CreateItemAsync(new ItemCommand("НОВ-1", "Без группы", ItemType.Finished, piece, null, vat)))).Code);
        Assert.Equal("catalog.required.vat", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.CreateItemAsync(new ItemCommand("НОВ-1", "Без НДС", ItemType.Finished, piece, null, null, group)))).Code);
        await s.Catalog.CreateItemAsync(new ItemCommand("НОВ-1", "Полная", ItemType.Finished, piece, null, vat, group));

        // Старая позиция без НДС: изменить можно, только указав ставку; группу у позиции не убрать.
        var row = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "СТАР-1"))).Items.Single();
        Assert.Equal("catalog.required.vat", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Catalog.UpdateItemAsync(old, new ItemCommand(row.Code, "Новое имя", row.Type, row.UnitId, null), row.RowVersion))).Code);
        await s.Catalog.UpdateItemAsync(old, new ItemCommand(row.Code, "Новое имя", row.Type, row.UnitId, null, vat), row.RowVersion);
        var card = await s.Nomenclature.GetItemAsync(old);
        Assert.Equal("catalog.required.group", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Nomenclature.SaveItemDetailsAsync(old, card.Details with { Article = "A-1" }, new Dictionary<long, string?>(), card.RowVersion))).Code);
        await s.Nomenclature.SaveItemDetailsAsync(old, card.Details with { GroupId = group }, new Dictionary<long, string?>(), card.RowVersion);
        Assert.Equal(group, (await s.Nomenclature.GetItemAsync(old)).Details.GroupId);
    }

    private async Task<CreatedOrganization> CreateOrgAsync()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        var inn = body + (sum % 11 % 10);
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}-{Guid.NewGuid():N}@test.local", $"Владелец {inn}", "RU"));
    }
}
