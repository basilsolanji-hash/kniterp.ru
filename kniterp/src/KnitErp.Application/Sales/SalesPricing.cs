using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Tagging;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Откуда цена строки (D1102): соглашение, вид цены клиента, основной вид цены; None — цены нет ни в одном виде.</summary>
public enum PriceSource : byte
{
    None = 0,
    Agreement = 1,
    Customer = 2,
    Base = 3,
}

/// <summary>
/// Цена позиции для заказа. Price — в базе документа (с НДС или без — как цены заказа), до скидки; DiscountPercent — скидка
/// соглашения; Net — цена со скидкой. PriceType — название вида цены, из которого взята цена.
/// </summary>
/// <remarks>D151: цена в валюте переводится в валюту организации по курсу на дату; Conversion — «12 $ по 92,15», NoRate — курса нет (цены нет),
/// StaleRate — курс не на дату документа.</remarks>
public sealed record PriceQuoteDto(long ItemId, decimal? Price, decimal DiscountPercent, decimal? VatPercent, PriceSource Source, string? PriceType,
    string? Conversion = null, bool NoRate = false, bool StaleRate = false)
{
    public decimal? Net => Price is { } p ? p * (1 - DiscountPercent / 100m) : null;

    public static string SourceName(PriceSource source) => source switch
    {
        PriceSource.Agreement => "по соглашению",
        PriceSource.Customer => "вид цены клиента",
        PriceSource.Base => "основной вид цены",
        _ => "цена не задана",
    };
}

/// <summary>Цены для заказа: действующее соглашение (или null) и цена каждой позиции.</summary>
public sealed record PriceQuoteSetDto(
    long? AgreementId, string? Agreement, decimal AgreementDiscount, string? CustomerPriceType, string? BasePriceType,
    IReadOnlyDictionary<long, PriceQuoteDto> Items);

public enum PriceIssueKind : byte
{
    BelowMinimum = 1,
    DiscountTooHigh = 2,
}

/// <summary>Нарушение ограничения цены в строке заказа (D1104): цена ниже допустимой или ручная скидка больше допустимой.</summary>
public sealed record PriceIssueDto(long ItemId, string Code, PriceIssueKind Kind, decimal Actual, decimal Limit);

/// <summary>
/// Подстановка цены и проверка ограничений (D1102–D1104). Порядок: соглашение с покупателем → вид цены клиента (условия продаж
/// в карточке) → основной вид цены; если в виде цены у позиции нет цены — берётся следующий по порядку. Скидка соглашения
/// ставится в строку. Ограничения: минимальная цена позиции (каталог, D305), вид цены — нижняя граница роли, максимальная ручная
/// скидка роли от цены по соглашению. Цены сравниваются с НДС.
/// </summary>
public static class SalesPricing
{
    /// <summary>Действующее на дату соглашение покупателя: своё → группы (тег контрагента) → типовое; при равенстве — позднее начало.</summary>
    public static async Task<SalesAgreement?> AgreementForAsync(IKnitErpDbContext db, long org, long customerId, DateOnly date, CancellationToken ct)
    {
        var tags = await db.TagLinks.AsNoTracking()
            .Where(l => l.OrganizationId == org && l.Entity == TaggedEntity.Counterparty && l.EntityId == customerId).Select(l => l.TagId).ToListAsync(ct);
        var candidates = await db.SalesAgreements.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Status == SalesAgreementStatus.Active && a.ValidFrom <= date && (a.ValidTo == null || a.ValidTo >= date)
                        && (a.CounterpartyId == customerId || (a.CounterpartyId == null && (a.TagId == null || tags.Contains(a.TagId.Value)))))
            .ToListAsync(ct);
        return candidates.OrderBy(a => a.Scope).ThenByDescending(a => a.ValidFrom).ThenByDescending(a => a.Id).FirstOrDefault();
    }

    /// <summary>
    /// Цены позиций для покупателя на дату в базе документа (docIncludesVat). agreementId — соглашение заказа (если оно ещё
    /// действует на дату), иначе — подбирается само.
    /// </summary>
    public static async Task<PriceQuoteSetDto> QuoteAsync(
        IKnitErpDbContext db, long org, long customerId, DateOnly date, bool docIncludesVat, IReadOnlyCollection<long>? itemIds, long? agreementId,
        CancellationToken ct)
    {
        SalesAgreement? agreement = null;
        if (agreementId is { } aid)
        {
            agreement = await db.SalesAgreements.AsNoTracking().SingleOrDefaultAsync(a => a.Id == aid && a.OrganizationId == org, ct);
            if (agreement is not null && !agreement.IsValidOn(date))
            {
                agreement = null;
            }
        }

        agreement ??= await AgreementForAsync(db, org, customerId, date, ct);
        var profileType = await db.CustomerSalesProfiles.AsNoTracking().Where(p => p.OrganizationId == org && p.CounterpartyId == customerId)
            .Select(p => p.PriceTypeId).SingleOrDefaultAsync(ct);
        var types = await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == org).ToDictionaryAsync(t => t.Id, ct);
        var baseType = types.Values.FirstOrDefault(t => t.IsDefault);
        var chain = new List<(long TypeId, PriceSource Source)>();
        void Add(long? id, PriceSource source)
        {
            if (id is { } t && types.TryGetValue(t, out var type) && !type.IsArchived && chain.All(c => c.TypeId != t))
            {
                chain.Add((t, source));
            }
        }

        Add(agreement?.PriceTypeId, PriceSource.Agreement);
        Add(profileType, PriceSource.Customer);
        Add(baseType?.Id, PriceSource.Base);
        var typeIds = chain.Select(c => c.TypeId).ToList();
        var priceQuery = db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == org && typeIds.Contains(p.PriceTypeId));
        var itemQuery = db.Items.AsNoTracking().Where(i => i.OrganizationId == org && !i.IsArchived);
        if (itemIds is not null)
        {
            priceQuery = priceQuery.Where(p => itemIds.Contains(p.ItemId));
            itemQuery = itemQuery.Where(i => itemIds.Contains(i.Id));
        }

        var prices = (await priceQuery.Select(p => new { p.ItemId, p.PriceTypeId, p.Price, p.CurrencyCode }).ToListAsync(ct))
            .ToDictionary(p => (p.ItemId, p.PriceTypeId), p => (p.Price, p.CurrencyCode));
        var conversion = new KnitErp.Application.Finance.PriceConversion(db, org, date); // D151
        var items = await itemQuery.Select(i => new { i.Id, i.VatRateId }).ToListAsync(ct);
        var vat = await VatAsync(db, org, date, ct);
        var discount = agreement?.DiscountPercent ?? 0;
        var result = new Dictionary<long, PriceQuoteDto>();
        foreach (var item in items)
        {
            var itemVat = vat(item.VatRateId);
            PriceQuoteDto quote = new(item.Id, null, discount, itemVat, PriceSource.None, null);
            foreach (var (typeId, source) in chain)
            {
                if (prices.TryGetValue((item.Id, typeId), out var price))
                {
                    var type = types[typeId];
                    if (await conversion.ToOrganizationAsync(price.Price, price.CurrencyCode, ct) is not { } converted)
                    {
                        // D151: курса нет вовсе — подстановка запрещена (цену вводят вручную), следующий вид цены не берётся.
                        quote = quote with { Source = source, PriceType = type.Name, NoRate = true, Conversion = price.CurrencyCode };
                        break;
                    }

                    quote = quote with
                    {
                        Price = Reprice(converted.Price, type.IncludesVat, docIncludesVat, itemVat), Source = source, PriceType = type.Name,
                        Conversion = converted.Converted ? converted.Describe() : null, StaleRate = converted.Stale,
                    };
                    break;
                }
            }

            result[item.Id] = quote;
        }

        return new PriceQuoteSetDto(agreement?.Id, agreement is null ? null : $"{agreement.Number} «{agreement.Name}»", discount,
            profileType is { } pt && types.TryGetValue(pt, out var ptype) ? ptype.Name : null, baseType?.Name, result);
    }

    /// <summary>НДС позиции на дату: ставка позиции или основная ставка организации.</summary>
    public static async Task<Func<long?, decimal?>> VatAsync(IKnitErpDbContext db, long org, DateOnly date, CancellationToken ct)
    {
        var rates = await db.VatRates.AsNoTracking().Include(r => r.Periods).Where(r => r.OrganizationId == org && !r.IsArchived).ToListAsync(ct);
        var standard = rates.FirstOrDefault(r => r.Kind == VatRateKind.Standard)?.PercentOn(date);
        return rateId => rateId is { } rid && rates.FirstOrDefault(r => r.Id == rid) is { } rate ? rate.PercentOn(date) : standard;
    }

    /// <summary>Цена из прайса для строки документа: прайс с НДС, а документ без — НДС вычитается, и наоборот; до 4 знаков.</summary>
    public static decimal Reprice(decimal price, bool priceIncludesVat, bool documentIncludesVat, decimal? vatPercent) =>
        TradeRules.InVatBasis(price, priceIncludesVat, documentIncludesVat, vatPercent);

    /// <summary>Ограничения цены пользователя по его ролям: самое мягкое среди ролей, где оно задано (D1103).</summary>
    public sealed record Limits(decimal? MaxDiscountPercent, long? MinPriceTypeId);

    public static async Task<Limits> LimitsForAsync(IKnitErpDbContext db, AccessContext ctx, DateTime nowUtc, CancellationToken ct)
    {
        var roleIds = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.OrganizationId == ctx.OrganizationId && a.UserId == ctx.UserId && a.RoleId != null && a.RevokedAtUtc == null
                        && a.ValidFromUtc <= nowUtc && (a.ValidToUtc == null || a.ValidToUtc > nowUtc))
            .Select(a => a.RoleId!.Value).ToListAsync(ct);
        var rows = await db.SalesRoleSettings.AsNoTracking().Where(s => s.OrganizationId == ctx.OrganizationId && roleIds.Contains(s.RoleId)).ToListAsync(ct);
        // Роль без записи или без значения ограничения не задаёт; среди заданных — самое мягкое.
        var discounts = rows.Where(r => r.MaxDiscountPercent is not null).Select(r => r.MaxDiscountPercent!.Value).ToList();
        var floors = rows.Where(r => r.MinPriceTypeId is not null).Select(r => r.MinPriceTypeId!.Value).ToList();
        return new Limits(discounts.Count == 0 ? null : discounts.Max(), floors.Count switch { 0 => null, 1 => floors[0], _ => await LowestTypeAsync() });

        // Несколько видов-границ: мягче тот, у которого цены ниже; сравнивается по сумме цен — достаточно для выбора вида.
        async Task<long?> LowestTypeAsync() =>
            await db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && floors.Contains(p.PriceTypeId))
                .GroupBy(p => p.PriceTypeId).OrderBy(g => g.Sum(p => p.Price)).Select(g => (long?)g.Key).FirstOrDefaultAsync(ct) ?? floors[0];
    }

    /// <summary>
    /// Нарушения ограничений цены в строках заказа для пользователя ctx (D1104): цена со скидкой ниже минимальной цены позиции
    /// или вида цены — границы роли; ручная скидка от цены по соглашению больше допустимой для роли. Строки без цены в прайсе
    /// по скидке не проверяются (не от чего считать).
    /// </summary>
    public static async Task<IReadOnlyList<PriceIssueDto>> IssuesAsync(IKnitErpDbContext db, AccessContext ctx, SalesOrder order, DateTime nowUtc,
        CancellationToken ct)
    {
        if (order.Lines.Count == 0)
        {
            return [];
        }

        var org = ctx.OrganizationId;
        var itemIds = order.Lines.Select(l => l.ItemId).ToList();
        var limits = await LimitsForAsync(db, ctx, nowUtc, ct);
        var items = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && itemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Code, i.MinPrice }).ToDictionaryAsync(i => i.Id, ct);
        var baseType = await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == org && t.IsDefault).Select(t => (bool?)t.IncludesVat).SingleOrDefaultAsync(ct) ?? true;
        Dictionary<long, decimal> floorPrices = [];
        var floorIncludesVat = true;
        if (limits.MinPriceTypeId is { } floorType)
        {
            floorIncludesVat = await db.PriceTypes.AsNoTracking().Where(t => t.Id == floorType).Select(t => t.IncludesVat).SingleAsync(ct);
            floorPrices = await db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == org && p.PriceTypeId == floorType && itemIds.Contains(p.ItemId))
                .ToDictionaryAsync(p => p.ItemId, p => p.Price, ct);
        }

        var quotes = limits.MaxDiscountPercent is null
            ? null
            : await QuoteAsync(db, org, order.CustomerId, order.OrderDate, order.PricesIncludeVat, itemIds, order.AgreementId, ct);
        var issues = new List<PriceIssueDto>();
        foreach (var line in order.Lines)
        {
            var item = items[line.ItemId];
            // D142: каждая граница — в своей величине (минимальная цена позиции — как базовый вид цены, граница роли — как её вид цены),
            // тем же правилом, что подсветка строки (TradeRules.BelowMinimum). Нарушено обе — показывается большая.
            (decimal Floor, bool IncludesVat)?[] floors =
                [item.MinPrice is { } m ? (m, baseType) : null, floorPrices.TryGetValue(line.ItemId, out var f) ? (f, floorIncludesVat) : null];
            var broken = floors.OfType<(decimal Floor, bool IncludesVat)>()
                .Where(x => TradeRules.BelowMinimum(line.NetPrice, order.PricesIncludeVat, line.VatPercent, x.Floor, x.IncludesVat))
                .OrderByDescending(x => Reprice(x.Floor, x.IncludesVat, true, line.VatPercent)).ToList();
            if (broken.Count > 0)
            {
                var (floor, floorVat) = broken[0];
                issues.Add(new PriceIssueDto(line.ItemId, item.Code, PriceIssueKind.BelowMinimum,
                    decimal.Round(Reprice(line.NetPrice, order.PricesIncludeVat, floorVat, line.VatPercent), 2), decimal.Round(floor, 2)));
            }

            // Ручное снижение: цена строки ниже цены по прайсу и ручная скидка; скидки правил (соглашение, акции — D1106) не в счёт.
            if (limits.MaxDiscountPercent is { } max && quotes!.Items.TryGetValue(line.ItemId, out var q) && q.Price is { } list && list > 0)
            {
                var manual = decimal.Round((1 - line.Price * (1 - line.ManualDiscountPercent / 100m) / list) * 100m, 2);
                if (manual > max)
                {
                    issues.Add(new PriceIssueDto(line.ItemId, item.Code, PriceIssueKind.DiscountTooHigh, manual, max));
                }
            }
        }

        return issues;
    }

    public static string Describe(PriceIssueDto i) => i.Kind == PriceIssueKind.BelowMinimum
        ? $"{i.Code}: цена {i.Actual:0.00} ниже допустимой {i.Limit:0.00}"
        : $"{i.Code}: скидка {i.Actual:0.##}% больше допустимой {i.Limit:0.##}%";
}
