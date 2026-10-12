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
/// Технический паспорт изделия (D725, ТЗ §9): эскиз, общие сведения, палитра и пряжа, детали с заправкой, табель мер, обработка и ВТО,
/// маркировка и уход, упаковка. Сумм нет — паспорт видят все с правом «Разработка: просмотр».
/// </summary>
public sealed record DevPassportDto(
    string Organization, DateOnly PrintedOn, string Number, string Name, int VersionNo, string Stage, string Category, string Audience, bool ChildSafety,
    string? Collection, string? Customer, string? Description, string? SizeRange, string? ColorRange, string? Composition, string? Labeling, string? Care,
    string? PackBox, byte[]? Sketch, IReadOnlyList<DevColorDto> Palette, IReadOnlyList<DevPassportDetail> Details, IReadOnlyList<DevSizeDto> Sizes,
    IReadOnlyList<DevMeasureDto> Measures, IReadOnlyList<DevPassportOperation> Operations);

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
            operations);
    }

    /// <summary>PDF и имя файла: «pasport-М-00001.pdf».</summary>
    public async Task<(string FileName, byte[] Content)> PdfAsync(long modelId, IDevPassportRenderer renderer, CancellationToken ct = default)
    {
        var passport = await BuildAsync(modelId, ct);
        return ($"pasport-{passport.Number}.pdf", renderer.Render(passport));
    }
}
