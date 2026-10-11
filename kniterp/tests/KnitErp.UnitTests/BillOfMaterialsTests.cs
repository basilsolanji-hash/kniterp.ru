using KnitErp.Domain.Production;

namespace KnitErp.UnitTests;

/// <summary>Многоуровневая спецификация: раскрытие полуфабрикатов с отходом на каждом уровне и поиск циклов.</summary>
public sealed class BillOfMaterialsTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    private static TechCard Card(long item, decimal output, params (long Material, decimal Qty, decimal Waste)[] lines)
    {
        var card = TechCard.Create(1, item, 1, output, null, 1, Now);
        foreach (var (material, qty, waste) in lines)
        {
            card.SetLine(material, qty, waste);
        }

        card.Activate(1, Now);
        return card;
    }

    [Fact]
    public void Explode_goes_through_levels_with_waste()
    {
        // Кофта (100): 0,3 кг пряжи (200) и 2 рукава (300) с отходом 5%; рукав: 0,2 кг пряжи с отходом 10%.
        var jacket = Card(100, 1, (200, 0.3m, 0), (300, 2, 5));
        var sleeve = Card(300, 1, (200, 0.2m, 10));
        var need = BillOfMaterials.Explode(jacket, 10, new Dictionary<long, TechCard> { [300] = sleeve }, _ => 3);
        // Рукавов 21 (20 + 5%), пряжи на них 21 × 0,2 × 1,1 = 4,62; итого 3 + 4,62.
        Assert.Equal(7.62m, need[200]);
        Assert.False(need.ContainsKey(300));
        Assert.Equal(21m, BillOfMaterials.Explode(jacket, 10, new Dictionary<long, TechCard>(), _ => 3)[300]);
    }

    [Fact]
    public void Contains_finds_cycles_at_any_depth()
    {
        var graph = new Dictionary<long, IReadOnlyCollection<long>> { [1] = [2], [2] = [3], [3] = [4] };
        Assert.True(BillOfMaterials.Contains(1, 4, id => graph.GetValueOrDefault(id) ?? []));
        Assert.False(BillOfMaterials.Contains(3, 1, id => graph.GetValueOrDefault(id) ?? []));
    }
}
