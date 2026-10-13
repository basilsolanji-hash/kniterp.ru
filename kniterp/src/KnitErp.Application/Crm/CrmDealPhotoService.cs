using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

public sealed record CrmDealPhotoDto(long Id, string? Caption, string ContentType, int SizeBytes, DateTime CreatedAtUtc, string Author, byte[] RowVersion)
{
    /// <summary>Адрес картинки: отдаёт эндпоинт с проверкой прав сделки (D692).</summary>
    public string Href => $"crm/deal-photos/{Id}";
}

public sealed record CrmDealPhotosDto(IReadOnlyList<CrmDealPhotoDto> Items, bool CanEdit)
{
    public int Left => Math.Max(0, CrmDealPhoto.MaxPerDeal - Items.Count);
}

/// <summary>
/// Фото к сделке (D692): просмотр — crm.view, загрузка, подпись и удаление — crm.edit; сделка должна быть видна пользователю
/// (<see cref="CrmAccess.Deals"/>), иначе — «не найдено», как чужая организация. Не больше 30 действующих фото на сделку: счёт идёт
/// под блокировкой сделки, чтобы две одновременные загрузки не превысили лимит. Удаление — архивом. Всё пишется в журнал аудита.
/// </summary>
public sealed class CrmDealPhotoService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<CrmDealPhotosDto> ListAsync(long dealId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        await EnsureDealAsync(ctx, dealId, ct);
        var items = await (from p in db.CrmDealPhotos.AsNoTracking()
                           join u in db.Users.AsNoTracking() on p.CreatedByUserId equals u.Id
                           where p.OrganizationId == ctx.OrganizationId && p.DealId == dealId && !p.IsArchived
                           orderby p.Id descending
                           select new CrmDealPhotoDto(p.Id, p.Caption, p.ContentType, p.SizeBytes, p.CreatedAtUtc, u.DisplayName, p.RowVersion))
            .ToListAsync(ct);
        return new CrmDealPhotosDto(items, ctx.Permissions.Has(Permissions.CrmEdit));
    }

    public async Task<long> UploadAsync(long dealId, byte[] content, string? caption, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var title = await EnsureDealAsync(ctx, dealId, ct);
        // Сначала проверка файла, потом db.Add (D678): неудачная попытка не оставит строку в контексте.
        var photo = CrmDealPhoto.Create(ctx.OrganizationId, dealId, content, caption, ctx.UserId, clock.UtcNow);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.crm.deal-photos.{dealId}", ct);
        var count = await db.CrmDealPhotos.CountAsync(p => p.OrganizationId == ctx.OrganizationId && p.DealId == dealId && !p.IsArchived, ct);
        if (count >= CrmDealPhoto.MaxPerDeal)
        {
            throw new BusinessRuleException("crm.photo.limit", "К сделке можно приложить не больше 30 фото. Удалите лишние.");
        }

        db.CrmDealPhotos.Add(photo);
        await db.SaveChangesAsync(ct);
        Audit(ctx, AuditActions.CrmDealPhotoAdded, photo.Id, null, $"{photo.ContentType}, {photo.SizeBytes} байт{(photo.Caption is null ? "" : $", «{photo.Caption}»")}", title);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return photo.Id;
    }

    public async Task SetCaptionAsync(long id, string? caption, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, photo, title) = await LoadAsync(id, rowVersion, ct);
        var before = photo.Caption;
        photo.SetCaption(caption);
        Audit(ctx, AuditActions.CrmDealPhotoChanged, id, before, photo.Caption, title);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Удалить из сделки — в архив: снимок остаётся в базе, в сделке и по ссылке больше не отдаётся.</summary>
    public async Task ArchiveAsync(long id, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, photo, title) = await LoadAsync(id, rowVersion, ct);
        photo.Archive(ctx.UserId, clock.UtcNow);
        Audit(ctx, AuditActions.CrmDealPhotoArchived, id, photo.Caption, "в архиве", title);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Картинка для эндпоинта: только действующее фото видимой сделки своей организации.</summary>
    public async Task<(string ContentType, byte[] Content)> ContentAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var deals = CrmAccess.Deals(db, ctx).Select(d => d.Id);
        var photo = await db.CrmDealPhotos.AsNoTracking()
                        .Where(p => p.Id == id && p.OrganizationId == ctx.OrganizationId && !p.IsArchived && deals.Contains(p.DealId))
                        .Select(p => new { p.ContentType, p.Content }).SingleOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Фото");
        return (photo.ContentType, photo.Content);
    }

    private async Task<string> EnsureDealAsync(AccessContext ctx, long dealId, CancellationToken ct) =>
        await CrmAccess.Deals(db, ctx).AsNoTracking().Where(d => d.Id == dealId).Select(d => d.Number + " " + d.Title).SingleOrDefaultAsync(ct)
        ?? throw new NotFoundException("Сделка");

    private async Task<(AccessContext, CrmDealPhoto, string)> LoadAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var dealId = await db.CrmDealPhotos.AsNoTracking().Where(p => p.Id == id && p.OrganizationId == ctx.OrganizationId && !p.IsArchived)
                         .Select(p => (long?)p.DealId).SingleOrDefaultAsync(ct)
                     ?? throw new NotFoundException("Фото");
        var title = await CrmAccess.Deals(db, ctx).AsNoTracking().Where(d => d.Id == dealId).Select(d => d.Number + " " + d.Title).SingleOrDefaultAsync(ct)
                    ?? throw new NotFoundException("Фото");
        var photo = await db.CrmDealPhotos.SingleAsync(p => p.Id == id && p.OrganizationId == ctx.OrganizationId, ct);
        return (ctx, photo.EnsureVersion(photo.RowVersion, rowVersion), title);
    }

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(CrmDealPhoto), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
