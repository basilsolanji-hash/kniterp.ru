using System.Globalization;
using System.Text;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>КП: PriceTypeId — цены этого вида (нет цены у позиции — цена из сделки); null — цены сделки. Terms null — из настроек.</summary>
public sealed record CrmOfferCommand(long DealId, long? PriceTypeId = null, int? ValidDays = null, string? Terms = null);

public sealed record CrmOfferLineDto(int LineNo, string Code, string Name, string UnitSymbol, decimal Quantity, decimal Price, decimal Amount);

/// <summary>Печатная форма КП (D624): продавец, покупатель, контакт, позиции, итог, условия, срок действия.</summary>
public sealed record CrmOfferDto(
    long Id, string Number, DateOnly Date, DateOnly ValidUntil, long DealId, string DealNumber, string DealTitle, string Seller, string? SellerInn,
    string? SellerAddress, string? SellerDirector, string Customer, string? CustomerInn, string? Contact, string? ContactEmail, string? Terms, string CurrencyCode,
    decimal Amount, string AmountInWords, bool PricesIncludeVat, IReadOnlyList<CrmOfferLineDto> Lines, string CreatedBy);

public sealed record CrmOfferRowDto(long Id, string Number, DateOnly Date, DateOnly ValidUntil, decimal? Amount, string CurrencyCode);

/// <summary>
/// Коммерческие предложения (D624, Б5): КП одной кнопкой из позиций сделки и вида цен, номер КП-000001, снимок позиций, печать
/// (HTML, «Сохранить как PDF» — тем же механизмом печати, что УПД), отправка письмом контакту. Создание КП фиксируется активностью
/// «КП отправлено» и (если включено) двигает сделку на этап «КП отправлено». Нужны crm.edit и право «Цены и суммы».
/// </summary>
public sealed class CrmOfferService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IEmailSender mail)
{
    public async Task<IReadOnlyList<LookupDto>> PriceTypesAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        return await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId && !t.IsArchived)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name).Select(t => new LookupDto(t.Id, t.Name)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CrmOfferRowDto>> ListAsync(long dealId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        _ = await CrmAccess.Deals(db, ctx).AsNoTracking().SingleOrDefaultAsync(d => d.Id == dealId, ct) ?? throw new NotFoundException("Сделка");
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        return await db.CrmOffers.AsNoTracking().Where(o => o.OrganizationId == ctx.OrganizationId && o.DealId == dealId).OrderByDescending(o => o.Id)
            .Select(o => new CrmOfferRowDto(o.Id, o.Number, o.OfferDate, o.ValidUntil, amounts ? o.Amount : null, o.CurrencyCode)).ToListAsync(ct);
    }

    public async Task<long> CreateAsync(CrmOfferCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var deal = await CrmAccess.Deals(db, ctx).Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == cmd.DealId, ct) ?? throw new NotFoundException("Сделка");
        if (deal.Outcome == CrmDealOutcome.Lost)
        {
            throw new BusinessRuleException("crm.deal.lost", "Сделка проиграна — заказ по ней не создаётся.");
        }

        var settings = await CrmEvents.SettingsAsync(db, ctx.OrganizationId, ct);
        var tz = await CrmEvents.TimeZoneAsync(db, ctx.OrganizationId, ct);
        var today = CrmEvents.Today(clock.UtcNow, tz);
        Dictionary<long, decimal> prices = [];
        if (cmd.PriceTypeId is { } pt)
        {
            if (!await db.PriceTypes.AnyAsync(t => t.Id == pt && t.OrganizationId == ctx.OrganizationId && !t.IsArchived, ct))
            {
                throw new NotFoundException("Вид цен");
            }

            prices = await db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && p.PriceTypeId == pt)
                .ToDictionaryAsync(p => p.ItemId, p => p.Price, ct);
        }

        var itemIds = deal.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol }).ToDictionaryAsync(x => x.Id, ct);
        var lines = deal.Lines.Select(l => (l.ItemId, items[l.ItemId].Code, items[l.ItemId].Name, items[l.ItemId].Symbol, l.Quantity,
            prices.TryGetValue(l.ItemId, out var p) ? p : l.Price)).ToList();
        CrmOffer.Create(ctx.OrganizationId, "КП-0", deal.Id, today, today.AddDays(cmd.ValidDays ?? settings.OfferValidDays), cmd.Terms ?? settings.OfferTerms,
            cmd.PriceTypeId, deal.CurrencyCode, lines, ctx.UserId, clock.UtcNow); // проверка до выдачи номера
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CrmOffer.NumberPrefix, ct);
        var offer = CrmOffer.Create(ctx.OrganizationId, number, deal.Id, today, today.AddDays(cmd.ValidDays ?? settings.OfferValidDays),
            cmd.Terms ?? settings.OfferTerms, cmd.PriceTypeId, deal.CurrencyCode, lines, ctx.UserId, clock.UtcNow);
        db.CrmOffers.Add(offer);
        await db.SaveChangesAsync(ct);
        db.CrmActivities.Add(CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.OfferSent, CrmTarget.Deal, deal.Id, $"КП {offer.Number}",
            $"Сумма {offer.Amount:N2} {offer.CurrencyCode}, действует до {offer.ValidUntil:dd.MM.yyyy}", null, ctx.UserId, ctx.UserId, clock.UtcNow, done: true,
            offerId: offer.Id));
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmOfferCreated, nameof(CrmOffer), offer.Id.ToString(),
            null, $"{offer.Number}: {offer.Amount:0.00} {offer.CurrencyCode}", deal.Number, currentUser.CorrelationId));
        await CrmEvents.AdvanceToAsync(db, ctx, deal, "КП отправлено", clock.UtcNow, currentUser.CorrelationId, $"КП {offer.Number}", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return offer.Id;
    }

    public async Task<CrmOfferDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var o = await db.CrmOffers.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("КП");
        var deal = await CrmAccess.Deals(db, ctx).AsNoTracking().SingleOrDefaultAsync(d => d.Id == o.DealId, ct) ?? throw new NotFoundException("КП");
        var seller = await db.LegalEntities.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && e.IsDefault)
            .Select(e => new { e.ShortName, e.Inn, e.LegalAddress, e.DirectorPosition, e.DirectorName }).FirstOrDefaultAsync(ct);
        var customer = await db.Counterparties.AsNoTracking().Where(c => c.Id == deal.CounterpartyId).Select(c => new { c.Name, c.Inn }).SingleAsync(ct);
        var contact = deal.ContactId is { } cid
            ? await db.CrmContacts.AsNoTracking().Where(c => c.Id == cid).Select(c => new { c.FullName, c.Email }).SingleOrDefaultAsync(ct)
            : null;
        var includesVat = o.PriceTypeId is { } pt
            ? await db.PriceTypes.AsNoTracking().Where(t => t.Id == pt).Select(t => t.IncludesVat).SingleAsync(ct)
            : true;
        var author = await db.Users.AsNoTracking().Where(u => u.Id == o.CreatedByUserId).Select(u => u.DisplayName).SingleAsync(ct);
        return new CrmOfferDto(o.Id, o.Number, o.OfferDate, o.ValidUntil, deal.Id, deal.Number, deal.Title, seller?.ShortName ?? "—", seller?.Inn,
            seller?.LegalAddress, seller is { DirectorName: { } dn } ? $"{seller.DirectorPosition ?? "Руководитель"} {dn}" : null, customer.Name, customer.Inn,
            contact?.FullName, contact?.Email, o.Terms, o.CurrencyCode, o.Amount, AmountInWords.Format(o.Amount, o.CurrencyCode), includesVat,
            o.Lines.OrderBy(l => l.LineNo).Select(l => new CrmOfferLineDto(l.LineNo, l.Code, l.Name, l.UnitSymbol, l.Quantity, l.Price, l.Amount)).ToList(), author);
    }

    /// <summary>
    /// Отправить КП письмом (D626): текст письма с таблицей позиций и итогом; активность «Письмо» на сделке; адрес в журнале — маской.
    /// </summary>
    public async Task SendAsync(long id, string? to, string? message, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var offer = await GetAsync(id, ct);
        var address = CrmText.NormalizeEmail(to) ?? throw new BusinessRuleException("crm.email", "Почта указана неверно: нужен адрес вида name@example.org.");
        if (!mail.IsConfigured)
        {
            throw new BusinessRuleException("crm.mail.not_configured", "Почта сервера не настроена — обратитесь к администратору.");
        }

        var subject = $"Коммерческое предложение {offer.Number} от {offer.Date:dd.MM.yyyy} — {offer.Seller}";
        var text = Render(offer, message);
        await CrmEmailTemplateService.EnsureEmailQuotaAsync(db, ctx, clock.UtcNow, ct);
        var (replyTo, replyName) = await CrmEmailTemplateService.ReplyToAsync(db, ctx.UserId, ct);
        if (!await mail.SendAsync(new EmailMessage(address, subject, text, CrmEmailHtml.FromText(text, $"Ответ на это письмо придёт менеджеру: {replyName}."), replyTo,
                replyName), ct))
        {
            throw new BusinessRuleException("crm.mail.failed", "Письмо не отправлено: почтовый сервер не принял его. Попробуйте позже.");
        }

        var a = CrmActivity.Create(ctx.OrganizationId, CrmActivityKind.Email, CrmTarget.Deal, offer.DealId, subject, text, null, ctx.UserId, ctx.UserId, clock.UtcNow,
            done: true, result: $"отправлено на {CrmText.MaskEmail(address)}", source: CrmActivitySource.Mail, offerId: offer.Id);
        db.CrmActivities.Add(a);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmEmailSent, nameof(CrmOffer), id.ToString(),
            null, $"{offer.Number} → {CrmText.MaskEmail(address)}", offer.DealNumber, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Текст КП для письма — моноширинной таблицей, читается в любом почтовом клиенте.</summary>
    public static string Render(CrmOfferDto o, string? message)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var sb = new StringBuilder();
        sb.AppendLine(string.IsNullOrWhiteSpace(message) ? $"Здравствуйте{(o.Contact is null ? "" : ", " + o.Contact)}!" : message.Trim());
        sb.AppendLine();
        sb.AppendLine($"Коммерческое предложение {o.Number} от {o.Date:dd.MM.yyyy}");
        sb.AppendLine($"Поставщик: {o.Seller}{(o.SellerInn is null ? "" : ", ИНН " + o.SellerInn)}");
        sb.AppendLine($"Покупатель: {o.Customer}{(o.CustomerInn is null ? "" : ", ИНН " + o.CustomerInn)}");
        sb.AppendLine();
        foreach (var l in o.Lines)
        {
            sb.AppendLine($"{l.LineNo}. {l.Name} ({l.Code}) — {l.Quantity.ToString("0.###", ru)} {l.UnitSymbol} × {l.Price.ToString("N2", ru)} = {l.Amount.ToString("N2", ru)}");
        }

        sb.AppendLine();
        sb.AppendLine($"Итого: {o.Amount.ToString("N2", ru)} {o.CurrencyCode}{(o.PricesIncludeVat ? ", в т. ч. НДС" : ", без НДС")} ({o.AmountInWords})");
        sb.AppendLine($"Предложение действует до {o.ValidUntil:dd.MM.yyyy}.");
        if (o.Terms is not null)
        {
            sb.AppendLine();
            sb.AppendLine(o.Terms);
        }

        sb.AppendLine();
        sb.AppendLine($"С уважением, {o.CreatedBy}");
        sb.AppendLine(o.Seller);
        return sb.ToString().Replace('\u00A0', ' ').Replace('\u202F', ' '); // обычные пробелы — читается в любом почтовом клиенте
    }
}
