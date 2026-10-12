using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D318: массовое изменение — по выбору и по отбору; неприменимое пропускается с причиной; журнал; права; чужие позиции.</summary>
public sealed class CatBulkEditTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 942_000_000;

    [SqlFact]
    public async Task Bulk_change_applies_to_selection_and_filter_and_skips_inapplicable()
    {
        var org = await CreateOrgAsync();
        long accountant;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            var piece = units.Single(u => u.Code == "796").Id;
            var vat = (await s.VatRates.ActiveAsync()).First(r => r.Kind == VatRateKind.Standard).Id;
            var hats = await s.Nomenclature.CreateGroupAsync(null, "Шапки");
            var a = await s.Catalog.CreateItemAsync(new ItemCommand("ША-1", "Шапка А", ItemType.Finished, piece, null));
            var b = await s.Catalog.CreateItemAsync(new ItemCommand("ША-2", "Шапка Б", ItemType.Finished, piece, null));
            var service = await s.Catalog.CreateItemAsync(new ItemCommand("УС-1", "Доставка", ItemType.Service, units.Single(u => u.Code == "876").Id, null, vat));
            var old = await s.Catalog.CreateItemAsync(new ItemCommand("ША-3", "Шапка старая", ItemType.Finished, piece, null));
            var oldRow = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "ША-3"))).Items.Single();
            await s.Catalog.ArchiveItemAsync(old, oldRow.RowVersion);

            // Выбор: группа, НДС, ТН ВЭД, маркировка; услуга — без маркировки, архивная — пропуск.
            var r = await s.Bulk.ApplyAsync([a, b, service, old], new BulkItemChanges(SetGroup: true, GroupId: hats, SetVat: true, VatRateId: vat,
                SetTnVed: true, TnVedCode: "6505009000", SetTracking: true, Tracking: TrackingType.Clothes));
            Assert.Equal((4, 2), (r.Selected, r.Changed));
            Assert.Equal(["в архиве", "услугу и комплект не маркируют"], r.Skipped.Select(x => x.Reason).Order());
            var card = await s.Nomenclature.GetItemAsync(a);
            Assert.Equal((hats, vat, "6505009000", true), (card.Details.GroupId, card.VatRateId, card.Details.TnVedCode, (await s.Trade.GetAsync(a)).Trade.IsMarked));

            // Повтор — без изменений; очистка НДС у услуги — пропуск.
            r = await s.Bulk.ApplyAsync([a, b], new BulkItemChanges(SetGroup: true, GroupId: hats));
            Assert.Equal((0, 2), (r.Changed, r.Unchanged));
            r = await s.Bulk.ApplyAsync([service], new BulkItemChanges(SetVat: true, VatRateId: null));
            Assert.Equal("ставка НДС обязательна", r.Skipped.Single().Reason);

            // По отбору: «ША» в группе — страна России всем двум.
            var ids = await s.Catalog.ListIdsAsync(new ItemFilter(Search: "ША", GroupId: hats), BulkEditService.MaxItems);
            Assert.Equal(2, ids.Count);
            r = await s.Bulk.ApplyAsync(ids, new BulkItemChanges(SetCountry: true, OriginCountryCode: "643", OriginCountryName: "Россия"));
            Assert.Equal(2, r.Changed);
            Assert.Equal("643", (await s.Nomenclature.GetItemAsync(b)).Details.OriginCountryCode);

            // Неверные значения и пустой набор — отказ до изменений.
            Assert.Equal("catalog.bulk.nothing", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Bulk.ApplyAsync([a], new BulkItemChanges()))).Code);
            Assert.Equal("catalog.group_defaults.tnved", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Bulk.ApplyAsync([a], new BulkItemChanges(SetTnVed: true, TnVedCode: "12")))).Code);

            await using (var db = host.NewDb())
            {
                Assert.True(await db.AuditEntries.CountAsync(e => e.OrganizationId == org.OrganizationId && e.Reason!.StartsWith("Массовое изменение")) >= 9);
            }

            accountant = (await s.Access.InviteAsync(new InviteUserCommand($"acc-{Guid.NewGuid():N}@test.local", "Бухгалтер", SystemRoles.Accountant, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == accountant)).Activate();
            await db.SaveChangesAsync();
        }

        await using (var s = host.As(accountant, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Bulk.ApplyAsync([1], new BulkItemChanges(SetTnVed: true, TnVedCode: "6505")));
        }

        // Чужие позиции не меняются — «не найдена».
        var other = await CreateOrgAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            var foreign = (await s.Bulk.ApplyAsync(await ForeignIdsAsync(org.OrganizationId), new BulkItemChanges(SetTnVed: true, TnVedCode: "6505")));
            Assert.Equal(0, foreign.Changed);
            Assert.All(foreign.Skipped, x => Assert.Equal("не найдена", x.Reason));
        }
    }

    private async Task<List<long>> ForeignIdsAsync(long org)
    {
        await using var db = host.NewDb();
        return await db.Items.Where(i => i.OrganizationId == org).Select(i => i.Id).ToListAsync();
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
