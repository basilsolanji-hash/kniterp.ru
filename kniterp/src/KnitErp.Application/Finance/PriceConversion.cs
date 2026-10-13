using KnitErp.Application.Common;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Finance;

/// <summary>Цена в валюте организации: сумма, исходная цена и валюта, курс (рублей за единицу) и его дата; Stale — курс не на дату документа.</summary>
public sealed record ConvertedPrice(decimal Price, decimal SourcePrice, string SourceCurrency, decimal Rate, DateOnly? RateDate, bool Stale)
{
    public bool Converted => SourceCurrency != PriceCurrencies.Default || Rate != 1m;

    /// <summary>«12 $ по 92,15 ₽» — для строки документа.</summary>
    public string Describe() => Converted
        ? $"{SourcePrice.ToString("#,0.####", Ru)} {PriceCurrencies.Symbol(SourceCurrency)} по {Rate.ToString("#,0.00##", Ru)}{(RateDate is { } d && Stale ? $" (курс на {d:dd.MM.yyyy})" : "")}"
        : string.Empty;

    private static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
}

/// <summary>
/// D151: пересчёт цен номенклатуры в валюту организации по курсу ЦБ (D146) на дату документа или выгрузки. Курс — последний известный
/// не позже даты (не на саму дату — Stale, экран предупреждает); курса нет вовсе — null (подстановка запрещена: вызывающий сообщает
/// «нет курса»). Курсы читаются одним запросом на валюту за проход. Цена — до 4 знаков, как в прайсе.
/// </summary>
public sealed class PriceConversion
{
    private readonly IKnitErpDbContext _db;
    private readonly long _org;
    private readonly DateOnly _date;
    private readonly Dictionary<string, (decimal PerUnit, DateOnly Date)?> _rates = [];
    private string? _orgCurrency;

    public PriceConversion(IKnitErpDbContext db, long organizationId, DateOnly date)
    {
        _db = db;
        _org = organizationId;
        _date = date;
    }

    public DateOnly Date => _date;

    /// <summary>Цена в валюте организации; null — нет курса нужной валюты.</summary>
    public async Task<ConvertedPrice?> ToOrganizationAsync(decimal price, string? currency, CancellationToken ct)
    {
        var from = PriceCurrencies.Normalize(currency);
        var to = _orgCurrency ??= await _db.Organizations.AsNoTracking().Where(o => o.Id == _org).Select(o => o.CurrencyCode).SingleAsync(ct);
        if (from == to)
        {
            return new ConvertedPrice(price, price, from, 1m, null, false);
        }

        if (await RubAsync(from, ct) is not { } f || await RubAsync(to, ct) is not { } t)
        {
            return null;
        }

        var rate = f.PerUnit / t.PerUnit;
        var stale = from != CurrencyRateService.Rub && f.Date != _date || to != CurrencyRateService.Rub && t.Date != _date;
        var rateDate = from != CurrencyRateService.Rub ? f.Date : t.Date;
        return new ConvertedPrice(decimal.Round(price * rate, 4), price, from, decimal.Round(rate, 4), rateDate, stale);
    }

    /// <summary>Набор цен по ключу: в валюте организации; без курса — ключ пропускается (позиция «без цены»).</summary>
    public async Task<Dictionary<TKey, decimal>> ToOrganizationAsync<TKey>(IEnumerable<(TKey Key, decimal Price, string Currency)> rows, CancellationToken ct)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, decimal>();
        foreach (var (key, price, currency) in rows)
        {
            if (await ToOrganizationAsync(price, currency, ct) is { } c)
            {
                result[key] = c.Price;
            }
        }

        return result;
    }

    /// <summary>Сегодня в часовом поясе организации — дата пересчёта для выгрузок и подсказок.</summary>
    public static async Task<PriceConversion> TodayAsync(IKnitErpDbContext db, long organizationId, DateTime nowUtc, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        return new PriceConversion(db, organizationId, DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(nowUtc, tz)));
    }

    /// <summary>То же, но без курса — понятный отказ (для ручных действий: «подставить цену», выгрузка по запросу).</summary>
    public async Task<ConvertedPrice> RequireAsync(decimal price, string? currency, CancellationToken ct) =>
        await ToOrganizationAsync(price, currency, ct)
        ?? throw new BusinessRuleException("currency.rate.missing",
            $"Нет курса {PriceCurrencies.Normalize(currency)} на {_date:dd.MM.yyyy}: цену в валюте нельзя перевести — введите курс в «Курсах валют».");

    private async Task<(decimal PerUnit, DateOnly Date)?> RubAsync(string code, CancellationToken ct)
    {
        if (code == CurrencyRateService.Rub)
        {
            return (1m, _date);
        }

        if (_rates.TryGetValue(code, out var cached))
        {
            return cached;
        }

        var r = await _db.CurrencyRates.AsNoTracking()
            .Where(x => x.CurrencyCode == code && x.RateDate <= _date && (x.OrganizationId == null || x.OrganizationId == _org))
            .OrderByDescending(x => x.RateDate).ThenByDescending(x => x.OrganizationId != null)
            .Select(x => new { x.Value, x.Nominal, x.RateDate }).FirstOrDefaultAsync(ct);
        var result = r is null ? ((decimal, DateOnly)?)null : (r.Value / r.Nominal, r.RateDate);
        _rates[code] = result;
        return result;
    }
}
