using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>Деталь в техническом паспорте: станок, переплетение, план и заправка нитеводителей (D725).</summary>
public sealed record DevPassportDetail(string Name, int Quantity, string? Machine, string? MachineParams, string? Weave, int? PlanKnitSeconds,
    decimal? PlanWeightGrams, string? Notes, string? Program, IReadOnlyList<DevFeederRowDto> Feeders);

/// <summary>Операция технолога в паспорте — без расценок: паспорт уходит в цех и заказчику.</summary>
public sealed record DevPassportOperation(int Seq, string Name, string? Note, decimal? NormMinutes);

/// <summary>
/// Технический паспорт изделия (D725, ТЗ §9): эскиз, общие сведения, палитра и пряжа, пряжа и вязка (краска, плотность, усадка),
/// детали с заправкой, размеры деталей, табель мер и вес по размерам, обработка и ВТО,
/// маркировка и уход, упаковка. Сумм нет — паспорт видят все с правом «Разработка: просмотр».
/// </summary>
public sealed record DevPassportDto(
    string Organization, DateOnly PrintedOn, string Number, string Name, int VersionNo, string Stage, string Category, string Audience, bool ChildSafety,
    string? Collection, string? Customer, string? Description, string? SizeRange, string? ColorRange, string? Composition, string? Labeling, string? Care,
    string? PackBox, byte[]? Sketch, IReadOnlyList<DevColorDto> Palette, IReadOnlyList<DevPassportDetail> Details, IReadOnlyList<DevSizeDto> Sizes,
    IReadOnlyList<DevMeasureDto> Measures, IReadOnlyList<DevPassportOperation> Operations, IReadOnlyList<DevDetailSizeRowDto>? DetailSizes = null,
    string? DyeNumber = null, decimal? DensityLoops = null, decimal? DensityRows = null, decimal? ShrinkagePercent = null, DevPassportPattern? Pattern = null);

/// <summary>Рисунок модели в паспорте (ТЗ §9 «рисунок с раппортом и раскладкой цветов»): версия, раппорт, цвета с долями и пряжей колорита.</summary>
public sealed record DevPassportPattern(string Number, string Name, int Version, string Kind, int? RapportWidth, int? RapportHeight, decimal? MaxFloatMm,
    string? MachineClass, string? Colorway, IReadOnlyList<(int No, string Name, decimal? Share, int? Feeder, string? Yarn)> Colors);

/// <summary>PDF технического паспорта (D725) — тем же механизмом, что УПД и счёт (PDFsharp/MigraDoc, встроенный шрифт).</summary>
public interface IDevPassportRenderer
{
    byte[] Render(DevPassportDto passport);
}

/// <summary>
/// Данные технического паспорта (D725): собираются теми же сервисами, что и экраны модели, — права и своя организация проверяются там же.
/// Эскиз — закреплённый эскиз модели, если это JPEG или PNG (их понимает PDF-библиотека).
/// </summary>
public sealed class DevPassportService(IKnitErpDbContext db, IAccessGuard guard, IClock clock, DevModelService models, DevTechService tech, DevShopService shop)
{
    public const int MaxSketchBytes = 8 * 1024 * 1024;

    public async Task<DevPassportDto> BuildAsync(long modelId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.DevView, ct);
        var org = ctx.OrganizationId;
        var m = await models.GetAsync(modelId, ct);
        var organization = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.ShortName).SingleAsync(ct);
        var palette = await tech.ColorsAsync(modelId, ct);
        var details = new List<DevPassportDetail>();
        foreach (var row in await tech.DetailsAsync(modelId, ct))
        {
            var card = await tech.TechCardAsync(row.Id, ct);
            details.Add(new DevPassportDetail(card.DetailName, card.Quantity, card.MachineName, card.MachineParams, card.Weave, card.PlanKnitSeconds,
                card.PlanWeightGrams, card.Notes, card.ProgramName is null ? null : $"{card.ProgramName}, версия {card.ProgramVersion}",
                card.Feeders.Where(f => f.Left is not null || f.Right is not null).ToList()));
        }

        var sizes = await shop.SizesAsync(modelId, ct);
        var operations = (await shop.OperationsAsync(modelId, ct)).Rows
            .Select(o => new DevPassportOperation(o.Seq, o.Name, o.Note, o.NormMinutes ?? o.TypeNorm)).ToList();
        byte[]? sketch = null;
        if (m.MainSketchFileId is { } sid)
        {
            sketch = await db.DevFiles.AsNoTracking()
                .Where(f => f.Id == sid && f.OrganizationId == org && f.ModelId == modelId && (f.ContentType == "image/jpeg" || f.ContentType == "image/png")
                            && f.SizeBytes <= MaxSketchBytes)
                .Select(f => f.Content).SingleOrDefaultAsync(ct);
        }

        var b = m.Brief;
        return new DevPassportDto(organization, await DevTechService.LocalTodayAsync(db, org, clock.UtcNow, ct), m.Number, m.Name, m.VersionNo,
            DevStages.Name(m.Stage), DevModel.CategoryName(m.Category), DevModel.AudienceName(m.Audience), m.ChildSafety, m.CollectionName, m.CustomerName,
            b.Description, b.SizeRange, b.ColorRange, b.Composition, b.Labeling, b.Care, sizes.PackBox, sketch, palette, details, sizes.Sizes, sizes.Measures,
            operations, sizes.Details, m.DyeNumber, m.DensityLoops, m.DensityRows, m.ShrinkagePercent, await PatternAsync(org, modelId, ct));
    }

    private async Task<DevPassportPattern?> PatternAsync(long org, long modelId, CancellationToken ct)
    {
        var link = await db.DevModels.AsNoTracking().Where(x => x.Id == modelId && x.OrganizationId == org)
            .Select(x => new { x.PatternId, x.PatternVersion, x.ColorwayId }).SingleAsync(ct);
        if (link.PatternId is not { } pid)
        {
            return null;
        }

        var p = await db.DevPatterns.AsNoTracking().SingleAsync(x => x.Id == pid && x.OrganizationId == org, ct);
        var cls = p.MachineClassId is { } mc ? await db.DevLookups.AsNoTracking().Where(l => l.Id == mc).Select(l => l.Name).SingleOrDefaultAsync(ct) : null;
        var colorway = link.ColorwayId is { } cw ? await db.DevPatternColorways.AsNoTracking().Where(c => c.Id == cw).Select(c => c.Name).SingleOrDefaultAsync(ct) : null;
        var colors = await (from c in db.DevPatternColors.AsNoTracking()
                            join y in db.DevPatternColorwayYarns.AsNoTracking().Where(y => y.ColorwayId == link.ColorwayId) on c.Id equals y.ColorId into yj
                            from y in yj.DefaultIfEmpty()
                            join i in db.Items.AsNoTracking() on y.ItemId equals i.Id into ij
                            from i in ij.DefaultIfEmpty()
                            where c.OrganizationId == org && c.PatternId == pid
                            orderby c.No
                            select new { c.No, c.Name, c.SharePercent, c.Feeder, Yarn = i == null ? null : i.Code + " — " + i.Name }).ToListAsync(ct);
        return new DevPassportPattern(p.Number, p.Name, link.PatternVersion ?? p.CurrentVersion, DevPattern.KindName(p.Kind), p.RapportWidth, p.RapportHeight,
            p.MaxFloatMm, cls, colorway, colors.Select(c => (c.No, c.Name, c.SharePercent, c.Feeder, (string?)c.Yarn)).ToList());
    }

    /// <summary>PDF и имя файла: «pasport-М-00001.pdf».</summary>
    public async Task<(string FileName, byte[] Content)> PdfAsync(long modelId, IDevPassportRenderer renderer, CancellationToken ct = default)
    {
        var passport = await BuildAsync(modelId, ct);
        return ($"pasport-{passport.Number}.pdf", renderer.Render(passport));
    }
}

/// <summary>
/// Уменьшение фото на сервере (ТЗ разработки §13, D729): длинная сторона — не больше заданной, пропорции сохраняются, поворот по EXIF
/// применяется. Возвращает null, если картинка уже не больше предела или формат не поддерживается (тогда файл хранится как загружен).
/// </summary>
public interface IImageShrinker
{
    byte[]? Shrink(byte[] content, string contentType, int maxSide);
}
