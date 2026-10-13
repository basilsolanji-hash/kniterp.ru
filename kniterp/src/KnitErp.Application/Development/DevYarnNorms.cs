using KnitErp.Application.Common;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>Граммы пряжи на изделие по позиции каталога и единица позиции (код ОКЕИ, символ, точность).</summary>
internal sealed record DevYarnNorm(long ItemId, string Code, string Name, string UnitCode, string Symbol, byte Precision, decimal Grams);

/// <summary>
/// Нормы пряжи модели (D751) — общие для себестоимости (D740) и передачи в производство (D750): вес детали — из факта принятого образца,
/// иначе последнего неотклонённого, иначе план; доли цветов — из того же факта; одна пряжа в нескольких цветах палитры — одна строка.
/// </summary>
internal static class DevYarnNorms
{
    public static async Task<(List<DevYarnNorm> Norms, List<DevNote> Warnings)> ForModelAsync(IKnitErpDbContext db, long org, long modelId, CancellationToken ct)
    {
        var details = await db.DevDetails.AsNoTracking().Where(d => d.OrganizationId == org && d.ModelId == modelId).OrderBy(d => d.SortOrder).ThenBy(d => d.Id)
            .ToListAsync(ct);
        var colors = await (from c in db.DevModelColors.AsNoTracking()
                            join i in db.Items.AsNoTracking() on c.ItemId equals i.Id
                            join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                            where c.OrganizationId == org && c.ModelId == modelId
                            select new { c.Id, ItemId = i.Id, i.Code, i.Name, Unit = u.Code, u.Symbol, u.Precision }).ToListAsync(ct);
        var facts = await (from f in db.DevSampleDetails.AsNoTracking()
                           join s in db.DevSamples.AsNoTracking() on f.SampleId equals s.Id
                           where f.OrganizationId == org && s.ModelId == modelId && s.Status != DevSampleStatus.Rejected
                           select new { f.Id, f.DetailId, f.WeightGrams, s.No, Accepted = s.Status == DevSampleStatus.Accepted }).ToListAsync(ct);
        var best = facts.Where(f => f.WeightGrams is not null).GroupBy(f => f.DetailId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.Accepted).ThenByDescending(f => f.No).First());
        var factIds = best.Values.Select(f => f.Id).ToList();
        var shares = await db.DevSampleColorShares.AsNoTracking().Where(s => s.OrganizationId == org && factIds.Contains(s.SampleDetailId)).ToListAsync(ct);
        var input = details.Select(d => best.TryGetValue(d.Id, out var f)
            ? new DevNormDetail(d.Name, d.Quantity, f.WeightGrams, shares.Where(s => s.SampleDetailId == f.Id).ToDictionary(s => s.ColorId, s => s.Percent))
            : new DevNormDetail(d.Name, d.Quantity, d.PlanWeightGrams, new Dictionary<long, decimal>())).ToList();
        var warnings = new List<DevNote>();
        if (details.Count == 0)
        {
            warnings.Add(DevNote.Of(DevNotes.NoDetails));
        }

        var result = DevNorms.Compute(input, colors.Select(c => c.Id).ToList());
        warnings.AddRange(result.Warnings);
        var norms = colors.Where(c => result.GramsByColor.ContainsKey(c.Id)).GroupBy(c => c.ItemId)
            .Select(g => new DevYarnNorm(g.Key, g.First().Code, g.First().Name, g.First().Unit, g.First().Symbol, g.First().Precision,
                g.Sum(x => result.GramsByColor[x.Id]))).ToList();
        return (norms, warnings);
    }

    /// <summary>Граммы в единицу позиции без округления вверх (для расчёта денег): кг или г; иначе — null.</summary>
    public static decimal? ExactQuantity(DevYarnNorm n) => n.UnitCode switch
    {
        "166" => decimal.Round(n.Grams / 1000m, 6, MidpointRounding.AwayFromZero),
        "163" => n.Grams,
        _ => null,
    };
}
