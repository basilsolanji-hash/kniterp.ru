using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Finance;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Finance;

public sealed record CurrencyRateDto(long Id, string CurrencyCode, DateOnly Date, int Nominal, decimal Value, CurrencyRateSource Source, string? UpdatedBy,
    byte[] RowVersion)
{
    public decimal PerUnit => Value / Nominal;
}

public sealed record CurrencyRatesDto(DateOnly Date, IReadOnlyList<CurrencyRateDto> Rates, DateOnly? LastCbrDate, bool CanEdit);

/// <summary>Курс на дату: рублей за единицу, дата курса (последняя не позже запрошенной) и источник.</summary>
public sealed record EffectiveRate(string CurrencyCode, decimal PerUnit, DateOnly Date, CurrencyRateSource Source);

/// <summary>
/// D146: справочник курсов валют к рублю. Курс ЦБ загружает фоновая служба (общий для всех), ручной вводит организация — он важнее
/// курса ЦБ на ту же дату. Курс на дату — последний известный не позже неё. Смотреть — любой участник организации; вводить вручную —
/// «Цены и суммы» и право продаж или закупок (как денежные операции). Ввод и изменение — в журнал аудита.
/// </summary>
public sealed class CurrencyRateService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, ICbrRateSource? cbr = null)
{
    public const string Rub = "RUB";

    /// <summary>Кнопка «Обновить курсы ЦБ сейчас» видна, если загрузка курсов включена на сервере.</summary>
    public bool CanRefresh => cbr is { Enabled: true };

    /// <summary>
    /// D152: загрузить курсы ЦБ сейчас (на сегодня и завтра по Москве) — если служба ещё не успела. Права — как ручной ввод курса.
    /// Не чаще раза в минуту на сервер (ЦБ не любит частых запросов); нет сети — понятный отказ «введите курс вручную».
    /// </summary>
    public async Task<(DateOnly Date, int Changed)> RefreshAsync(CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        if (cbr is not { Enabled: true })
        {
            throw new BusinessRuleException("currency.cbr.disabled", "Загрузка курсов ЦБ на сервере выключена — введите курс вручную.");
        }

        lock (RefreshGate)
        {
            if (clock.UtcNow - _lastRefreshUtc < TimeSpan.FromMinutes(1))
            {
                throw new BusinessRuleException("currency.cbr.too_often", "Курсы ЦБ только что загружались — повторите через минуту.");
            }

            _lastRefreshUtc = clock.UtcNow;
        }

        var moscow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, "Europe/Moscow"));
        (DateOnly Date, int Changed) last = (moscow, 0);
        var total = 0;
        foreach (var date in new[] { moscow, moscow.AddDays(1) })
        {
            byte[] content;
            try
            {
                content = await cbr.FetchAsync(date, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                throw new BusinessRuleException("currency.cbr.unavailable", "Сайт ЦБ сейчас недоступен — повторите позже или введите курс вручную.");
            }

            last = await new CbrRateImporter(db, clock).ImportAsync(content, ct);
            total += last.Changed;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CurrencyRateChanged, nameof(CurrencyRate), "ЦБ",
            null, $"загружено курсов: {total}", $"Курсы ЦБ на {last.Date:dd.MM.yyyy} — загрузка по кнопке", currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return (last.Date, total);
    }

    private static readonly object RefreshGate = new();
    private static DateTime _lastRefreshUtc = DateTime.MinValue;

    public async Task<CurrencyRatesDto> ListAsync(DateOnly date, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var org = ctx.OrganizationId;
        // Одним запросом — курсы за 40 дней до даты (ЦБ публикует каждый рабочий день); на валюту — последний, ручной важнее ЦБ.
        var recent = await db.CurrencyRates.AsNoTracking()
            .Where(r => (r.OrganizationId == null || r.OrganizationId == org) && r.RateDate <= date && r.RateDate > date.AddDays(-40))
            .ToListAsync(ct);
        var rows = recent.GroupBy(r => r.CurrencyCode).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.RateDate).ThenByDescending(r => r.OrganizationId != null).First())
            .Select(r => new CurrencyRateDto(r.Id, r.CurrencyCode, r.RateDate, r.Nominal, r.Value, r.Source, null, r.RowVersion)).ToList();

        var userIds = rows.Select(r => r.Id).ToList();
        var by = await db.CurrencyRates.AsNoTracking().Where(r => userIds.Contains(r.Id) && r.UpdatedByUserId != null)
            .Join(db.Users.AsNoTracking(), r => r.UpdatedByUserId, u => u.Id, (r, u) => new { r.Id, u.DisplayName }).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var last = await db.CurrencyRates.AsNoTracking().Where(r => r.OrganizationId == null).MaxAsync(r => (DateOnly?)r.RateDate, ct);
        return new CurrencyRatesDto(date, rows.Select(r => r with { UpdatedBy = by.GetValueOrDefault(r.Id) }).ToList(), last, CanEdit(ctx));
    }

    /// <summary>Курс на дату для пересчёта документа в валюте; рубль — 1. Курса нет — понятный отказ «введите курс вручную».</summary>
    public async Task<EffectiveRate> RateAsync(string currency, DateOnly date, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        return await RateAsync(db, ctx.OrganizationId, currency, date, ct);
    }

    /// <summary>Пересчёт суммы из одной валюты в другую по курсам ЦБ (или ручным) на дату, до копеек.</summary>
    public async Task<decimal> ConvertAsync(decimal amount, string from, string to, DateOnly date, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        return await ConvertAsync(db, ctx.OrganizationId, amount, from, to, date, ct);
    }

    internal static async Task<decimal> ConvertAsync(IKnitErpDbContext db, long org, decimal amount, string from, string to, DateOnly date, CancellationToken ct)
    {
        var f = CurrencyRate.NormalizeCode(from);
        var t = CurrencyRate.NormalizeCode(to);
        if (f == t)
        {
            return amount;
        }

        var rub = amount * (await RateAsync(db, org, f, date, ct)).PerUnit;
        return Money.Round(t == Rub ? rub : rub / (await RateAsync(db, org, t, date, ct)).PerUnit);
    }

    internal static async Task<EffectiveRate> RateAsync(IKnitErpDbContext db, long org, string currency, DateOnly date, CancellationToken ct)
    {
        var code = CurrencyRate.NormalizeCode(currency);
        if (code == Rub)
        {
            return new EffectiveRate(Rub, 1m, date, CurrencyRateSource.Cbr);
        }

        var r = await Query(db, org, code, date).FirstOrDefaultAsync(ct)
                ?? throw new BusinessRuleException("currency.rate.missing",
                    $"Нет курса {code} на {date:dd.MM.yyyy}: загрузка курсов ЦБ ещё не прошла — введите курс вручную в «Курсах валют».");
        return new EffectiveRate(code, r.Value / r.Nominal, r.RateDate, r.Source);
    }

    /// <summary>Ввести или изменить ручной курс на дату (rowVersion — null для нового).</summary>
    public async Task SetManualAsync(string currency, DateOnly date, int nominal, decimal value, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var code = CurrencyRate.NormalizeCode(currency);
        if (code == Rub)
        {
            throw new BusinessRuleException("currency.rate.rub", "Курс рубля к рублю не вводится.");
        }

        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.LockAsync($"kniterp.currency.{ctx.OrganizationId}.{code}", ct);
            var existing = await db.CurrencyRates.SingleOrDefaultAsync(r => r.OrganizationId == ctx.OrganizationId && r.CurrencyCode == code && r.RateDate == date, ct);
            string? before = null;
            if (existing is null)
            {
                if (rowVersion is not null)
                {
                    throw new ConcurrencyConflictException();
                }

                db.CurrencyRates.Add(CurrencyRate.Manual(ctx.OrganizationId, code, date, nominal, value, ctx.UserId, clock.UtcNow));
            }
            else
            {
                if (rowVersion is not null)
                {
                    existing.EnsureVersion(existing.RowVersion, rowVersion);
                }

                before = Describe(existing.Nominal, existing.Value);
                existing.Set(nominal, value, ctx.UserId, clock.UtcNow);
            }

            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CurrencyRateChanged, nameof(CurrencyRate),
                code, before, Describe(nominal, value), $"{code} на {date:dd.MM.yyyy}, вручную", currentUser.CorrelationId));
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private static string Describe(int nominal, decimal value) => $"{nominal} = {value.ToString("0.0000", CultureInfo.InvariantCulture)} руб.";

    private static bool CanEdit(AccessContext ctx) =>
        ctx.Permissions.Has(Permissions.PriceView) && (ctx.Permissions.Has(Permissions.SalesEdit) || ctx.Permissions.Has(Permissions.PurchaseEdit));

    private async Task<AccessContext> DemandEditAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        if (!CanEdit(ctx))
        {
            await guard.DenyAsync(ctx, Permissions.SalesEdit, ct);
        }

        return ctx;
    }

    private IQueryable<CurrencyRate> Query(long org, string code, DateOnly date) => Query(db, org, code, date);

    /// <summary>Последний курс не позже даты: более поздняя дата важнее, на одну дату ручной курс организации важнее курса ЦБ.</summary>
    private static IQueryable<CurrencyRate> Query(IKnitErpDbContext db, long org, string code, DateOnly date) =>
        db.CurrencyRates.AsNoTracking()
            .Where(r => r.CurrencyCode == code && r.RateDate <= date && (r.OrganizationId == null || r.OrganizationId == org))
            .OrderByDescending(r => r.RateDate).ThenByDescending(r => r.OrganizationId != null);
}

/// <summary>D146, D152: источник XML курсов ЦБ (сайт cbr.ru в приложении; в тестах — без сети).</summary>
public interface ICbrRateSource
{
    bool Enabled { get; }

    Task<byte[]> FetchAsync(DateOnly date, CancellationToken ct);
}

/// <summary>Строка ежедневного XML ЦБ РФ (XML_daily.asp): буквенный код, номинал, курс.</summary>
public sealed record CbrRateRow(string Code, int Nominal, decimal Value);

/// <summary>
/// D146: загрузка официальных курсов ЦБ РФ. Разбор XML — без внешних сущностей и с ограничением размера (<see cref="SafeXml"/>).
/// Повторная загрузка той же даты обновляет курс, не создаёт дублей. Дата курса — из атрибута Date файла (ЦБ отдаёт курс последнего
/// рабочего дня, если на запрошенную дату курса нет). Без пользователя: общий справочник, журнал приложения — без персональных данных.
/// </summary>
public sealed class CbrRateImporter(IKnitErpDbContext db, IClock clock)
{
    public const int MaxBytes = 1024 * 1024;

    public static (DateOnly Date, IReadOnlyList<CbrRateRow> Rows) Parse(byte[] content)
    {
        var doc = SafeXml.Load(content, MaxBytes);
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "ValCurs"
                         || !DateOnly.TryParseExact((string?)root.Attribute("Date"), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new BusinessRuleException("currency.cbr.format", "Файл курсов ЦБ не распознан: нет ValCurs с датой.");
        }

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var rows = new List<CbrRateRow>();
        foreach (var v in root.Elements("Valute"))
        {
            var code = ((string?)v.Element("CharCode"))?.Trim();
            if (code is not { Length: 3 } || !int.TryParse((string?)v.Element("Nominal"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nominal)
                || !decimal.TryParse(((string?)v.Element("Value"))?.Trim(), NumberStyles.Number, ru, out var value) || value <= 0 || nominal < 1)
            {
                continue; // нераспознанная строка — пропуск, остальные курсы загружаются
            }

            rows.Add(new CbrRateRow(code.ToUpperInvariant(), nominal, decimal.Round(value, 4)));
        }

        return (date, rows);
    }

    /// <summary>Записать курсы файла. Возвращает дату курса и число новых или изменённых курсов.</summary>
    public async Task<(DateOnly Date, int Changed)> ImportAsync(byte[] content, CancellationToken ct = default)
    {
        var (date, rows) = Parse(content);
        var codes = rows.Select(r => r.Code).ToList();
        var existing = await db.CurrencyRates.Where(r => r.OrganizationId == null && r.RateDate == date && codes.Contains(r.CurrencyCode))
            .ToDictionaryAsync(r => r.CurrencyCode, ct);
        var changed = 0;
        foreach (var row in rows.DistinctBy(r => r.Code))
        {
            if (existing.TryGetValue(row.Code, out var rate))
            {
                if (rate.Nominal != row.Nominal || rate.Value != row.Value)
                {
                    rate.Set(row.Nominal, row.Value, null, clock.UtcNow);
                    changed++;
                }
            }
            else
            {
                db.CurrencyRates.Add(CurrencyRate.Cbr(row.Code, date, row.Nominal, row.Value, clock.UtcNow));
                changed++;
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.DiscardChanges(); // одновременная загрузка той же даты на другом экземпляре — она уже записала курсы
            return (date, 0);
        }

        return (date, changed);
    }
}
