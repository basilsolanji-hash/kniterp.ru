using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Tagging;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D103: теги как в Finder — создание, отметка объектов, поиск отмеченного с учётом прав и организации.</summary>
public sealed class TagTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 780_000_000;

    [SqlFact]
    public async Task Tag_objects_find_them_and_respect_rights()
    {
        var (org, yarn, shop, item) = await SetUpAsync();
        long urgent, docYarn, docShop, supplier;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            supplier = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Нить»", null, null, true, false, null));
            var reason = await s.Db.OperationReasons.Where(r => r.OrganizationId == org.OrganizationId && r.Kind == StockOperationKind.Receipt)
                .Select(r => r.Id).FirstAsync();
            docYarn = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(yarn, null, supplier, reason, new DateOnly(2026, 10, 11), null));
            docShop = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(shop, null, supplier, reason, new DateOnly(2026, 10, 11), null));

            urgent = await s.Tags.CreateAsync(" #Срочно ", TagColor.Red);
            Assert.Equal("tag.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Tags.CreateAsync("Срочно", TagColor.Blue))).Code);
            await s.Tags.AssignAsync(TaggedEntity.Item, item, urgent);
            await s.Tags.AssignAsync(TaggedEntity.Item, item, urgent); // повтор — без дубля
            await s.Tags.AssignAsync(TaggedEntity.Counterparty, supplier, urgent);
            await s.Tags.AssignAsync(TaggedEntity.StockDocument, docYarn, urgent);
            await s.Tags.AssignAsync(TaggedEntity.StockDocument, docShop, urgent);
            // Новый тег прямо с карточки — по названию.
            await s.Tags.AssignAsync(TaggedEntity.Item, item, null, "Новый год", TagColor.Green);
            await s.Tags.AssignAsync(TaggedEntity.StockDocument, docYarn, null, "новый ГОД");

            Assert.Equal(["Новый год", "Срочно"], (await s.Tags.ForEntityAsync(TaggedEntity.Item, item)).Select(t => t.Name));
            var (tag, objects) = await s.Tags.ObjectsAsync(urgent);
            Assert.Equal((TagColor.Red, 4), (tag.Color, objects.Count));
            Assert.Equal($"catalog/items/{item}", objects.Single(o => o.Entity == TaggedEntity.Item).Href);
            Assert.Equal(2, (await s.Tags.ListAsync()).Single(t => t.Name == "Новый год").Count);

            // Быстрый поиск: «#сроч» — только теги.
            var hits = await s.Search.SearchAsync("#сроч");
            Assert.Equal("#Срочно", Assert.Single(hits).Title);

            // Чужой или несуществующий объект — «не найдено».
            await Assert.ThrowsAsync<NotFoundException>(() => s.Tags.AssignAsync(TaggedEntity.SalesOrder, 999_999_999, urgent));
        }

        // Кладовщик склада пряжи видит по тегу только документы своего склада.
        var keeper = await InviteActiveAsync(org, SystemRoles.Storekeeper, yarn);
        await using (var s = host.As(keeper, org.OrganizationId))
        {
            var (_, objects) = await s.Tags.ObjectsAsync(urgent);
            Assert.Contains(objects, o => o.Entity == TaggedEntity.StockDocument && o.EntityId == docYarn);
            Assert.DoesNotContain(objects, o => o.Entity == TaggedEntity.StockDocument && o.EntityId == docShop);
            await Assert.ThrowsAsync<NotFoundException>(() => s.Tags.RemoveAsync(TaggedEntity.StockDocument, docShop, urgent));
        }

        // Другая организация: тега не видно, объект не отметить.
        var (other, _, _, otherItem) = await SetUpAsync();
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            Assert.Empty(await s.Tags.ListAsync(true));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Tags.ObjectsAsync(urgent));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Tags.AssignAsync(TaggedEntity.Item, otherItem, urgent));
            await Assert.ThrowsAsync<NotFoundException>(() => s.Tags.AssignAsync(TaggedEntity.Item, item, null, "Чужое"));
        }

        // Снять тег, переименовать, в архив — архивный тег скрыт с объектов, отметки возвращаются при восстановлении.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Tags.RemoveAsync(TaggedEntity.Counterparty, supplier, urgent);
            var t = (await s.Tags.ListAsync()).Single(x => x.Id == urgent);
            await s.Tags.UpdateAsync(urgent, "Горит", TagColor.Orange, false, t.RowVersion);
            t = (await s.Tags.ListAsync()).Single(x => x.Id == urgent);
            Assert.Equal(("Горит", TagColor.Orange, false, 3), (t.Name, t.Color, t.IsFavorite, t.Count));
            await s.Tags.ArchiveAsync(urgent, true, t.RowVersion);
            Assert.DoesNotContain(await s.Tags.ForEntityAsync(TaggedEntity.Item, item), x => x.Id == urgent);
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.Tags.AssignAsync(TaggedEntity.Item, item, urgent));
            t = (await s.Tags.ListAsync(true)).Single(x => x.Id == urgent);
            await s.Tags.ArchiveAsync(urgent, false, t.RowVersion);
            Assert.Contains(await s.Tags.ForEntityAsync(TaggedEntity.Item, item), x => x.Id == urgent);
            Assert.True(await s.Db.AuditEntries.AnyAsync(a => a.Action == KnitErp.Domain.Audit.AuditActions.TagAssigned && a.EntityId == urgent.ToString()));
        }
    }

    private async Task<(CreatedOrganization Org, long Yarn, long Shop, long Item)> SetUpAsync()
    {
        var inn = NextValidInn();
        CreatedOrganization org;
        await using (var s = host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            var yarn = await s.Warehouses.CreateWarehouseAsync("Склад пряжи", null);
            var shop = await s.Warehouses.CreateWarehouseAsync("Цех", null);
            var item = await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа", ItemType.RawMaterial, units.Single(u => u.Symbol == "кг").Id, null));
            return (org, yarn, shop, item);
        }
    }

    private async Task<long> InviteActiveAsync(CreatedOrganization org, string roleCode, long? warehouseId)
    {
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            id = (await s.Access.InviteAsync(new InviteUserCommand($"{roleCode}-{Guid.NewGuid():N}@test.local",
                SystemRoles.NameOf(roleCode), roleCode, null, WarehouseId: warehouseId))).UserId;
        }

        await using var db = host.NewDb();
        (await db.Users.SingleAsync(u => u.Id == id)).Activate();
        await db.SaveChangesAsync();
        return id;
    }

    private static string NextValidInn()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        return body + (sum % 11 % 10);
    }
}
