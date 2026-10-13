using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D338: сортировка списка номенклатуры на сервере и личный вид списка (у каждого пользователя свой).</summary>
public sealed class CatListViewTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 952_000_000;

    [SqlFact]
    public async Task List_sorts_on_server_and_view_is_personal()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
        foreach (var (code, name) in new[] { ("Б-2", "Альфа"), ("А-1", "Вега"), ("В-3", "Бета") })
        {
            await s.Catalog.CreateItemAsync(new ItemCommand(code, name, ItemType.Finished, piece, null));
        }

        var withArticle = (await s.Catalog.ListItemsAsync(new ItemFilter(Search: "В-3"))).Items.Single().Id;
        var card = await s.Nomenclature.GetItemAsync(withArticle);
        await s.Nomenclature.SaveItemDetailsAsync(withArticle, card.Details with { Article = "ART-1" }, new Dictionary<long, string?>(), card.RowVersion);

        async Task<string[]> Codes(ItemSort sort, bool desc) =>
            (await s.Catalog.ListItemsAsync(new ItemFilter(Sort: sort, Descending: desc, Take: 2, Skip: 0))).Items.Select(i => i.Code).ToArray();
        Assert.Equal(["А-1", "Б-2"], await Codes(ItemSort.Code, false));       // страница — уже отсортированного списка
        Assert.Equal(["В-3", "Б-2"], await Codes(ItemSort.Code, true));
        Assert.Equal(["Б-2", "В-3"], await Codes(ItemSort.Name, false));       // Альфа, Бета
        Assert.Equal("В-3", (await Codes(ItemSort.Article, false))[0]);        // с артикулом — первой, без артикула — в конце
        Assert.Equal("В-3", (await Codes(ItemSort.Article, true))[0]);

        // Вид: сохраняется, мусор отбрасывается; другой пользователь видит свой (по умолчанию).
        var saved = await s.CatalogViews.SaveAsync(new CatalogViewDto(ItemSort.Name, true, ["vat", "price", "<script>"]));
        Assert.Equal(["vat", "price"], saved.Hidden);
        Assert.Equal((ItemSort.Name, true), ((await s.CatalogViews.GetAsync()).Sort, (await s.CatalogViews.GetAsync()).Descending));

        long other;
        await using (var o = host.As(org.OwnerUserId, org.OrganizationId))
        {
            other = (await o.Access.InviteAsync(new InviteUserCommand($"v-{Guid.NewGuid():N}@test.local", "Кладовщик", SystemRoles.Storekeeper, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == other)).Activate();
            await db.SaveChangesAsync();
        }

        await using var k = host.As(other, org.OrganizationId);
        var theirs = await k.CatalogViews.GetAsync();
        Assert.Equal((ItemSort.Default, false), (theirs.Sort, theirs.Descending));
        Assert.True(theirs.Shows("vat"));
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
