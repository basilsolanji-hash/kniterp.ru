using KnitErp.Domain.Common;

namespace KnitErp.Domain.Finance;

public enum CurrencyRateSource : byte
{
    /// <summary>Официальный курс ЦБ РФ (общий для всех организаций, загружается фоновой службой).</summary>
    Cbr = 1,

    /// <summary>Введён вручную в организации (нет сети, другая валюта) — для этой организации важнее курса ЦБ на ту же дату.</summary>
    Manual = 2,
}

/// <summary>
/// D146: курс валюты к рублю на дату — Value рублей за Nominal единиц (как в XML ЦБ: 10 юаней, 100 тенге). Курс ЦБ — без организации
/// (OrganizationId null), ручной — своей организации. Уникальность: валюта и дата у ЦБ, организация, валюта и дата у ручного.
/// </summary>
public sealed class CurrencyRate
{
    public const decimal MaxValue = 1_000_000m;

    private CurrencyRate()
    {
    }

    public long Id { get; private set; }
    public long? OrganizationId { get; private set; }
    public string CurrencyCode { get; private set; } = string.Empty;
    public DateOnly RateDate { get; private set; }
    public int Nominal { get; private set; } = 1;
    public decimal Value { get; private set; }
    public CurrencyRateSource Source { get; private set; }
    public long? UpdatedByUserId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Рублей за одну единицу валюты.</summary>
    public decimal PerUnit => Value / Nominal;

    public static CurrencyRate Cbr(string code, DateOnly date, int nominal, decimal value, DateTime nowUtc)
    {
        var r = new CurrencyRate { CurrencyCode = NormalizeCode(code), RateDate = date, Source = CurrencyRateSource.Cbr };
        r.Set(nominal, value, null, nowUtc);
        return r;
    }

    public static CurrencyRate Manual(long organizationId, string code, DateOnly date, int nominal, decimal value, long userId, DateTime nowUtc)
    {
        var r = new CurrencyRate { OrganizationId = organizationId, CurrencyCode = NormalizeCode(code), RateDate = date, Source = CurrencyRateSource.Manual };
        r.Set(nominal, value, userId, nowUtc);
        return r;
    }

    public void Set(int nominal, decimal value, long? userId, DateTime nowUtc)
    {
        if (nominal is < 1 or > 1_000_000)
        {
            throw new BusinessRuleException("currency.rate.nominal", "Номинал курса — от 1 до 1 000 000 единиц валюты.");
        }

        if (value <= 0 || value > MaxValue || decimal.Round(value, 4) != value)
        {
            throw new BusinessRuleException("currency.rate.value", "Курс — больше нуля, до четырёх знаков после запятой.");
        }

        Nominal = nominal;
        Value = value;
        UpdatedByUserId = userId;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Буквенный код ISO 4217: три латинские буквы, заглавными.</summary>
    public static string NormalizeCode(string? code)
    {
        var c = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (c.Length != 3 || !c.All(char.IsAsciiLetterUpper))
        {
            throw new BusinessRuleException("currency.code", "Код валюты — три латинские буквы (USD, EUR, CNY).");
        }

        return c;
    }
}
