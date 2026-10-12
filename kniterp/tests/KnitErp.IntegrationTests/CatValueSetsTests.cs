using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D321: наборы значений — готовые образцы, свои наборы, диапазон при создании модификаций, права.</summary>
public sealed class CatValueSetsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 944_000_000;

    [SqlFact]
    public async Task Presets_own_sets_and_range_in_modifications()
    {
        var org = await CreateOrgAsync();
        long accountant;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal(CharacteristicValueSet.Presets.Count, await s.ValueSets.AddPresetsAsync());
            Assert.Equal(0, await s.ValueSets.AddPresetsAsync()); // повтор ничего не дублирует
            var sets = await s.ValueSets.ListAsync();
            Assert.Equal(["42", "44", "46", "48", "50", "52", "54", "56"], sets.Single(v => v.Name == "Женские 42–56").Values);
            var size = sets.First(v => v.Characteristic == "Размер").CharacteristicId;

            var own = await s.ValueSets.CreateAsync(size, "Парные", "44-46, 48-50");
            var ownDto = (await s.ValueSets.ListAsync()).Single(v => v.Id == own);
            Assert.Equal(["44-46", "48-50"], ownDto.Values);
            Assert.Equal("catalog.value_set.range", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.ValueSets.UpdateAsync(own, "Парные", "60-40/2", ownDto.RowVersion))).Code);
            Assert.Equal(["44-46", "48-50"], (await s.ValueSets.ListAsync()).Single(v => v.Id == own).Values);

            // Модификации: диапазон в поле значений раскрывается.
            var piece = (await s.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id;
            var sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, piece, null));
            var result = await s.Modifications.CreateModificationsAsync(sweater, new Dictionary<long, string?> { [size] = "42-48/2" });
            Assert.Equal(4, result.Created);

            accountant = (await s.Access.InviteAsync(new InviteUserCommand($"acc-{Guid.NewGuid():N}@test.local", "Бухгалтер", SystemRoles.Accountant, null))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == accountant)).Activate();
            await db.SaveChangesAsync();
        }

        await using (var s = host.As(accountant, org.OrganizationId))
        {
            Assert.NotEmpty(await s.ValueSets.ListAsync());
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.ValueSets.AddPresetsAsync());
        }
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
