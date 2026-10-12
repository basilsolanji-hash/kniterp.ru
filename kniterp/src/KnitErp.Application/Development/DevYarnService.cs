using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>Строка списания пряжи образца: позиция, граммы по факту цеха и количество в единице позиции.</summary>
public sealed record DevYarnLineDto(long ItemId, string ItemCode, string ItemName, string Unit, decimal Grams, decimal Quantity);

/// <summary>Что спишется на образец (D721) и куда: склады и причины списания из области пользователя; документ, если уже создан.</summary>
public sealed record DevYarnWriteOffDto(
    long SampleId, int SampleNo, IReadOnlyList<DevYarnLineDto> Lines, IReadOnlyList<string> Warnings, IReadOnlyList<LookupWarehouseDto> Warehouses,
    IReadOnlyList<ReasonLookupDto> Reasons, long? DocumentId, string? DocumentNumber, StockDocumentStatus? DocumentStatus, string? Refusal);

/// <summary>
/// Списание пряжи на образец (D721): по факту цеха (вес детали × штук в изделии × доли цветов палитры) создаётся черновик складского
/// документа «Списание» через сервис склада — склад проверяет и проводит его своим правом, остаток в минус не уходит (D40).
/// Пишет — разработка или цех (`dev.edit` / `dev.sample`) с правом склада на черновик; второй раз не списываем, пока документ не отменён.
/// </summary>
public sealed class DevYarnService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, StockDocumentService stock)
{
    public async Task<DevYarnWriteOffDto> PreviewAsync(long sampleId, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var s = await db.DevSamples.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Образец");
        var (lines, warnings) = await LinesAsync(ctx.OrganizationId, s, ct);
        var doc = s.YarnWriteOffDocumentId is { } did
            ? await db.StockDocuments.AsNoTracking().Where(d => d.Id == did).Select(d => new { d.Id, d.Number, d.Status }).SingleOrDefaultAsync(ct)
            : null;
        var canCreate = ctx.Permissions.Has(Permissions.WarehouseDocumentCreate);
        var options = canCreate ? await stock.GetOptionsAsync(StockOperationKind.WriteOff, ct) : null;
        var refusal = doc is not null && doc.Status != StockDocumentStatus.Cancelled ? "Пряжа на этот образец уже списана документом."
            : !canCreate ? "Нужно право склада создавать документы — списание оформит кладовщик."
            : lines.Count == 0 ? "Нечего списывать: внесите факт образца (вес и доли цветов) и выберите пряжу в палитре."
            : null;
        return new DevYarnWriteOffDto(s.Id, s.No, lines, warnings, options?.Warehouses ?? [], options?.Reasons ?? [], doc?.Id, doc?.Number, doc?.Status, refusal);
    }

    /// <summary>Создать черновик списания; возвращает номер документа склада.</summary>
    public async Task<long> WriteOffAsync(long sampleId, long warehouseId, long? reasonId, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            var s = await db.DevSamples.SingleOrDefaultAsync(x => x.Id == sampleId && x.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Образец");
            if (s.YarnWriteOffDocumentId is { } did
                && await db.StockDocuments.AnyAsync(d => d.Id == did && d.Status != StockDocumentStatus.Cancelled, ct))
            {
                throw new BusinessRuleException("dev.yarn.done", "Пряжа на этот образец уже списана документом.");
            }

            var model = await db.DevModels.AsNoTracking().SingleAsync(m => m.Id == s.ModelId && m.OrganizationId == ctx.OrganizationId, ct);
            var (lines, warnings) = await LinesAsync(ctx.OrganizationId, s, ct);
            if (lines.Count == 0)
            {
                throw new BusinessRuleException("dev.yarn.empty", "Нечего списывать: внесите факт образца (вес и доли цветов) и выберите пряжу в палитре.");
            }

            var comment = $"Пряжа на образец № {s.No} модели {model.Number} «{model.Name}» (по факту цеха)"
                          + (warnings.Count == 0 ? "" : $"; не учтено: {warnings.Count} замеч.");
            var docId = await stock.CreateWithLinesAsync(StockOperationKind.WriteOff,
                new StockDocumentHeader(warehouseId, null, null, reasonId, DateOnly.FromDateTime(clock.UtcNow), comment.Length > 500 ? comment[..500] : comment),
                lines.Select(l => (l.ItemId, l.Quantity)).ToList(), ct);
            s.LinkYarnWriteOff(docId);
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.DevSampleRecorded, nameof(DevModel),
                model.Id.ToString(), null, $"черновик списания пряжи №{docId}, строк {lines.Count}", $"{model.Number}: образец № {s.No}",
                currentUser.CorrelationId));
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
            return docId;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.DevEdit) && !ctx.Permissions.Has(Permissions.DevSample))
        {
            await guard.DemandAsync(Permissions.DevSample, ct);
        }

        return ctx;
    }

    /// <summary>Граммы пряжи образца по факту цеха → количество в единице позиции (кг или г, с округлением вверх по точности единицы).</summary>
    private async Task<(List<DevYarnLineDto> Lines, List<string> Warnings)> LinesAsync(long org, DevSample s, CancellationToken ct)
    {
        var facts = await (from f in db.DevSampleDetails.AsNoTracking()
                           join d in db.DevDetails.AsNoTracking() on f.DetailId equals d.Id
                           where f.OrganizationId == org && f.SampleId == s.Id
                           select new { f.Id, d.Name, d.Quantity, f.WeightGrams }).ToListAsync(ct);
        var factIds = facts.Select(f => f.Id).ToList();
        var shares = await db.DevSampleColorShares.AsNoTracking().Where(x => x.OrganizationId == org && factIds.Contains(x.SampleDetailId)).ToListAsync(ct);
        var colors = await (from c in db.DevModelColors.AsNoTracking()
                            join i in db.Items.AsNoTracking() on c.ItemId equals i.Id
                            join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                            where c.OrganizationId == org && c.ModelId == s.ModelId
                            select new { c.Id, ItemId = i.Id, i.Code, i.Name, Unit = u.Code, u.Symbol, u.Precision }).ToListAsync(ct);
        var result = DevNorms.Compute(
            facts.Select(f => new DevNormDetail(f.Name, f.Quantity, f.WeightGrams,
                shares.Where(x => x.SampleDetailId == f.Id).ToDictionary(x => x.ColorId, x => x.Percent))).ToList(),
            colors.Select(c => c.Id).ToList());
        var warnings = result.Warnings.ToList();
        if (facts.Count == 0)
        {
            warnings.Add("По образцу ещё нет факта цеха.");
        }

        var lines = new List<DevYarnLineDto>();
        foreach (var g in colors.Where(c => result.GramsByColor.ContainsKey(c.Id)).GroupBy(c => c.ItemId))
        {
            var c = g.First();
            var grams = g.Sum(x => result.GramsByColor[x.Id]);
            if (DevNorms.ToUnit(grams, c.Unit, c.Precision) is { } qty)
            {
                lines.Add(new DevYarnLineDto(c.ItemId, c.Code, c.Name, c.Symbol, grams, qty));
            }
            else
            {
                warnings.Add($"«{c.Name}»: единица «{c.Symbol}» — не кг и не г, количество введёт склад.");
            }
        }

        return (lines, warnings);
    }
}
