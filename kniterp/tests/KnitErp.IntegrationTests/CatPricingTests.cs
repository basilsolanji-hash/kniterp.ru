using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Торговые сведения (D305): предмет расчёта, маркировка, GTIN, минимальная цена; история цен (только вставка);
/// массовая переоценка с предпросмотром, журналом и правом catalog.price.edit (D306).
/// </summary>
public sealed class CatPricingTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 932_000_000;

    [SqlFact]
    public async Task Trade_details_marking_gtin_and_min_price()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var trade = await s.Trade.GetAsync(f.Sweater);
            Assert.Equal((PaymentSubject.Goods, false), (trade.Trade.PaymentSubject, trade.Trade.IsMarked));
            await s.Trade.SaveAsync(f.Sweater, new ItemTradeDetails(PaymentSubject.Goods, true, TrackingType.Clothes, "4601234567893", 1500m), trade.RowVersion);
            trade = await s.Trade.GetAsync(f.Sweater);
            Assert.Equal((true, TrackingType.Clothes, "04601234567893", 1500m),
                (trade.Trade.IsMarked, trade.Trade.TrackingType, trade.Trade.Gtin, trade.Trade.MinPrice!.Value));

            // GTIN уникален в организации; у услуги маркировки нет, предмет расчёта — «услуга».
            Assert.Equal("catalog.gtin.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Trade.SaveAsync(f.Scarf, new ItemTradeDetails(PaymentSubject.Goods, true, TrackingType.Clothes, "04601234567893", null),
                    (await s.Trade.GetAsync(f.Scarf)).RowVersion))).Code);
            Assert.Equal(PaymentSubject.Service, (await s.Trade.GetAsync(f.Service)).Trade.PaymentSubject);
            Assert.Equal("catalog.service.marking", (await Assert.ThrowsAsync<BusinessRuleException>(async () =>
                await s.Trade.SaveAsync(f.Service, new ItemTradeDetails(PaymentSubject.Service, true, TrackingType.Clothes, null, null),
                    (await s.Trade.GetAsync(f.Service)).RowVersion))).Code);
        }

        // Без права «Цены и суммы» минимальная цена не видна и не меняется (Старший кладовщик меняет справочник в своей области).
        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            var trade = await s.Trade.GetAsync(f.Sweater);
            Assert.Null(trade.Trade.MinPrice);
            Assert.False(trade.CanSeePrices);
            Assert.Empty(trade.History);
        }

        await using (var db = host.NewDb())
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [kniterp].[items] SET [IsMarked] = 1, [TrackingType] = 0 WHERE [Id] = {f.Scarf}"));
            Assert.Contains("ck_items_marking", ex.Message);
            ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [kniterp].[items] SET [PaymentSubject] = 1 WHERE [Id] = {f.Service}"));
            Assert.Contains("ck_items_payment_subject", ex.Message);
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Trade.GetAsync(f.Sweater));
        }
    }

    [SqlFact]
    public async Task Price_history_and_mass_revaluation()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Ручная правка цен пишет историю «было → стало».
            await s.Nomenclature.SetPricesAsync(f.Sweater, new Dictionary<long, decimal?> { [f.PriceType] = 1000 });
            await s.Nomenclature.SetPricesAsync(f.Sweater, new Dictionary<long, decimal?> { [f.PriceType] = 1200 });
            await s.Nomenclature.SetPricesAsync(f.Scarf, new Dictionary<long, decimal?> { [f.PriceType] = 700 });
            var history = (await s.Trade.GetAsync(f.Sweater)).History;
            Assert.Equal([(1000m, 1200m), ((decimal?)null, 1000m)], history.Select(h => (h.OldPrice, h.NewPrice!.Value)).Select(x => ((decimal?)x.OldPrice, x.Item2)));
            Assert.All(history, h => Assert.Equal(PriceChangeSource.Manual, h.Source));

            // Минимальная цена свитера 1 300 — переоценка −10 % опустит его ниже.
            var trade = await s.Trade.GetAsync(f.Sweater);
            await s.Trade.SaveAsync(f.Sweater, trade.Trade with { MinPrice = 1300m }, trade.RowVersion);

            // Предпросмотр: +10 % к основной цене, округление до рубля; у услуги без цены — пропуск.
            var request = new RevaluationRequest(f.PriceType, RevaluationMode.Percent, 10, PriceRounding.Ruble);
            var preview = await s.Revaluations.PreviewAsync(request);
            Assert.Equal((3, 2, 1, 0), (preview.Total, preview.ToChange, preview.Skipped, preview.BelowMinimum));
            Assert.Equal(1320m, preview.Rows.Single(r => r.ItemId == f.Sweater).NewPrice);
            Assert.Equal(770m, preview.Rows.Single(r => r.ItemId == f.Scarf).NewPrice);
            Assert.Equal(1, (await s.Revaluations.PreviewAsync(request with { Value = -10 })).BelowMinimum);
            // Отбор по группе и виду.
            Assert.Single((await s.Revaluations.PreviewAsync(request with { GroupId = f.Group })).Rows);
            Assert.Single((await s.Revaluations.PreviewAsync(request with { Kind = ItemKind.Service })).Rows);

            Assert.Equal(2, await s.Revaluations.ApplyAsync(request));
            Assert.Equal(1320m, (await s.Nomenclature.GetItemAsync(f.Sweater)).Prices.Single(p => p.PriceTypeId == f.PriceType).Price);
            var journal = Assert.Single(await s.Revaluations.JournalAsync());
            Assert.Equal((2, "+10 % к текущей цене, округление до рубля"), (journal.Items, journal.Rule));
            Assert.Equal(PriceChangeSource.Revaluation, (await s.Trade.GetAsync(f.Sweater)).History[0].Source);

            // Наценка от закупки: закупочная 500 × 1,8 = 900 у свитера; у шарфа закупочной цены нет — пропуск.
            var card = await s.Nomenclature.GetItemAsync(f.Sweater);
            await s.Nomenclature.SaveItemDetailsAsync(f.Sweater, card.Details with { PurchasePrice = 500 }, new Dictionary<long, string?>(), card.RowVersion);
            var markup = await s.Revaluations.PreviewAsync(new RevaluationRequest(f.PriceType, RevaluationMode.FromPurchase, 1.8m));
            Assert.Equal(900m, markup.Rows.Single(r => r.ItemId == f.Sweater).NewPrice);
            Assert.Null(markup.Rows.Single(r => r.ItemId == f.Scarf).NewPrice);
            Assert.Equal(1, markup.BelowMinimum);

            Assert.Equal("catalog.revaluation.percent", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Revaluations.PreviewAsync(request with { Value = -150 }))).Code);
            Assert.Equal("catalog.revaluation.nothing", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Revaluations.ApplyAsync(request with { Kind = ItemKind.Service }))).Code);
        }

        // Право: Старший кладовщик переоценку не проводит; чужая организация не видит вид цены.
        await using (var s = host.As(f.Senior, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Revaluations.PreviewAsync(new RevaluationRequest(f.PriceType, RevaluationMode.Percent, 5)));
        }

        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.Revaluations.PreviewAsync(new RevaluationRequest(f.PriceType, RevaluationMode.Percent, 5)));
        }

        // История цен и журнал переоценок — только вставка.
        await using (var db = host.NewDb())
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [kniterp].[item_price_history] SET [NewPrice] = 1 WHERE [ItemId] = {f.Sweater}"));
            Assert.Equal(51301, ex.Number);
            ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [kniterp].[price_revaluations] SET [Items] = 0 WHERE [OrganizationId] = {f.Org.OrganizationId}"));
            Assert.Equal(51301, ex.Number);
        }
    }

    private sealed record Fixture(CreatedOrganization Org, long Sweater, long Scarf, long Service, long PriceType, long Group, long Senior);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var piece = units.Single(u => u.Code == "796").Id;
        var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
        var group = await s.Nomenclature.CreateGroupAsync(null, "Свитеры");
        var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, piece, null, vat, group));
        var scarf = await s.Catalog.CreateItemAsync(new ItemCommand("ШФ-1", "Шарф", ItemType.Finished, piece, null, vat));
        var service = await s.Catalog.CreateItemAsync(new ItemCommand("УС-1", "Доставка", ItemType.Service, units.Single(u => u.Code == "876").Id, null, vat));
        var priceType = (await s.Nomenclature.PriceTypesAsync()).Single(t => t.IsDefault).Id;
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var senior = (await s.Access.InviteAsync(new InviteUserCommand($"senior-{Guid.NewGuid():N}@test.local", "Старший кладовщик",
            SystemRoles.SeniorStorekeeper, null, WarehouseId: store))).UserId;
        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == senior)).Activate();
            await db.SaveChangesAsync();
        }

        return new Fixture(org, sweater, scarf, service, priceType, group, senior);
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
