using KnitErp.Domain.Common;

namespace KnitErp.Domain.Organizations;

public enum LegalEntityKind : byte
{
    /// <summary>Юридическое лицо (ООО, АО).</summary>
    Company = 1,

    /// <summary>Индивидуальный предприниматель.</summary>
    SoleProprietor = 2,
}

/// <summary>Реквизиты юрлица; для ИП Name — «Индивидуальный предприниматель Фамилия Имя Отчество», DirectorName — его ФИО.</summary>
public sealed record LegalEntityData(
    LegalEntityKind Kind, string? Name, string? ShortName, string? Inn, string? Kpp, string? Ogrn, string? LegalAddress,
    string? DirectorPosition, string? DirectorName, string? AccountantName, bool VatExempt);

/// <summary>
/// Своё юрлицо организации — продавец в документах (D78), как «Юр. лица» в МойСклад: у одной фабрики может быть ООО и ИП.
/// В заказе выбирается юрлицо и его расчётный счёт; счёт на оплату, УПД и журнал счетов-фактур — от его имени.
/// Для России проверяются ИНН (10 цифр у юрлица, 12 у ИП), КПП (только у юрлица), ОГРН/ОГРНИП. Одно юрлицо — основное:
/// его подставляет новый заказ; основное нельзя убрать в архив. Не удаляется — архивируется.
/// </summary>
public sealed class LegalEntity
{
    public const int NameMaxLength = 300;
    public const int ShortNameMaxLength = 150;
    public const int AddressMaxLength = 500;
    public const int PersonNameMaxLength = 150;
    public const int PositionMaxLength = 100;
    public const int MaxEntities = 20;

    private LegalEntity()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public LegalEntityKind Kind { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string ShortName { get; private set; } = string.Empty;
    public string Inn { get; private set; } = string.Empty;
    public string? Kpp { get; private set; }

    /// <summary>ОГРН юрлица или ОГРНИП предпринимателя — для ИП печатается в подписи УПД.</summary>
    public string? Ogrn { get; private set; }

    public string? LegalAddress { get; private set; }
    public string? DirectorPosition { get; private set; }
    public string? DirectorName { get; private set; }
    public string? AccountantName { get; private set; }

    /// <summary>
    /// Освобождён от НДС (ст. 145 НК, в т. ч. упрощёнка с освобождением): в документах «Без налога (НДС)», в журнал выданных
    /// счетов-фактур не попадает.
    /// </summary>
    public bool VatExempt { get; private set; }

    public bool IsDefault { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsSoleProprietor => Kind == LegalEntityKind.SoleProprietor;

    public static LegalEntity Create(long organizationId, string countryCode, LegalEntityData data, bool isDefault)
    {
        var entity = new LegalEntity { OrganizationId = organizationId, IsDefault = isDefault };
        entity.Apply(countryCode, data);
        return entity;
    }

    /// <summary>Меняет реквизиты; возвращает изменения для журнала.</summary>
    public IReadOnlyList<FieldChange> Update(string countryCode, LegalEntityData data)
    {
        EnsureActive();
        var before = Snapshot();
        Apply(countryCode, data);
        var after = Snapshot();
        return before.Zip(after).Where(p => p.First.Value != p.Second.Value)
            .Select(p => new FieldChange(p.First.Field, p.First.Value, p.Second.Value)).ToList();
    }

    public void SetDefault(bool isDefault)
    {
        if (isDefault)
        {
            EnsureActive();
        }

        IsDefault = isDefault;
    }

    public void SetArchived(bool archived)
    {
        if (archived && IsDefault)
        {
            throw new BusinessRuleException("legal_entity.default_archive", "Основное юрлицо нельзя убрать в архив. Сначала сделайте основным другое.");
        }

        IsArchived = archived;
    }

    public void EnsureActive()
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("legal_entity.archived", $"Юрлицо «{ShortName}» в архиве.");
        }
    }

    public static string KindName(LegalEntityKind kind) => kind switch
    {
        LegalEntityKind.SoleProprietor => "ИП",
        _ => "Юрлицо",
    };

    private void Apply(string countryCode, LegalEntityData d)
    {
        if (!Enum.IsDefined(d.Kind))
        {
            throw new BusinessRuleException("legal_entity.kind", "Вид — юрлицо или индивидуальный предприниматель.");
        }

        var country = Countries.Get(countryCode);
        var inn = Digits(d.Inn);
        var kpp = string.IsNullOrWhiteSpace(d.Kpp) ? null : d.Kpp.Trim().ToUpperInvariant();
        var ogrn = string.IsNullOrWhiteSpace(d.Ogrn) ? null : Digits(d.Ogrn);
        var sole = d.Kind == LegalEntityKind.SoleProprietor;

        if (country.Code == Countries.Russia)
        {
            if (sole ? !RussianRequisites.IsValidPersonInn(inn) : !RussianRequisites.IsValidLegalEntityInn(inn))
            {
                throw new BusinessRuleException("legal_entity.inn",
                    sole ? "ИНН предпринимателя — 12 цифр с верной контрольной суммой." : "ИНН юрлица — 10 цифр с верной контрольной суммой.");
            }

            if (sole && kpp is not null)
            {
                throw new BusinessRuleException("legal_entity.kpp_sole", "У индивидуального предпринимателя нет КПП.");
            }

            if (kpp is not null && !RussianRequisites.IsValidKpp(kpp))
            {
                throw new BusinessRuleException("legal_entity.kpp", "КПП указан неверно: 9 символов.");
            }

            if (ogrn is not null && (sole ? !RussianRequisites.IsValidOgrnip(ogrn) : !RussianRequisites.IsValidOgrn(ogrn)))
            {
                throw new BusinessRuleException("legal_entity.ogrn",
                    sole ? "ОГРНИП — 15 цифр с верной контрольной цифрой." : "ОГРН — 13 цифр с верной контрольной цифрой.");
            }
        }
        else
        {
            if (!(sole ? Countries.IsValidCounterpartyTaxId(country.Code, inn) : Countries.IsValidOrganizationTaxId(country.Code, inn)))
            {
                throw new BusinessRuleException("legal_entity.inn", $"{Countries.TaxIdRule(country.Code, organization: !sole)} — номер указан неверно.");
            }

            if (kpp is not null)
            {
                throw new BusinessRuleException("legal_entity.kpp_country", "КПП бывает только у российских организаций.");
            }

            if (ogrn is { Length: > 20 })
            {
                throw new BusinessRuleException("legal_entity.ogrn", "Регистрационный номер — не длиннее 20 цифр.");
            }
        }

        Kind = d.Kind;
        Name = DomainText.Require(d.Name, NameMaxLength, "Полное наименование");
        ShortName = DomainText.Require(d.ShortName, ShortNameMaxLength, "Краткое наименование");
        Inn = inn;
        Kpp = kpp;
        Ogrn = ogrn;
        LegalAddress = DomainText.Optional(d.LegalAddress, AddressMaxLength, "Юридический адрес");
        DirectorName = DomainText.Optional(d.DirectorName, PersonNameMaxLength, sole ? "ФИО предпринимателя" : "Руководитель");
        DirectorPosition = sole ? null : DomainText.Optional(d.DirectorPosition, PositionMaxLength, "Должность руководителя");
        AccountantName = DomainText.Optional(d.AccountantName, PersonNameMaxLength, "Главный бухгалтер");
        VatExempt = d.VatExempt;
    }

    private (string Field, string? Value)[] Snapshot() =>
    [
        ("Kind", KindName(Kind)), ("Name", Name), ("ShortName", ShortName), ("Inn", Inn), ("Kpp", Kpp), ("Ogrn", Ogrn),
        ("LegalAddress", LegalAddress), ("DirectorPosition", DirectorPosition), ("DirectorName", DirectorName),
        ("AccountantName", AccountantName), ("VatExempt", VatExempt ? "да" : "нет"),
    ];

    private static string Digits(string? value) => (value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
}

/// <summary>Вид денежного счёта (D80): расчётный счёт в банке или касса (наличные).</summary>
public enum MoneyAccountKind : byte
{
    Bank = 1,
    Cash = 2,
}

/// <summary>
/// Расчётный счёт юрлица (D78). Для России БИК и счета проверяются по контрольному ключу; для других стран — длина и символы.
/// Один счёт юрлица — основной: его подставляет заказ, если счёт не выбран. Не удаляется — архивируется.
/// </summary>
public sealed class LegalEntityAccount
{
    public const int BankNameMaxLength = 300;
    public const int BicMaxLength = 11;
    public const int AccountMaxLength = 34;
    public const int MaxAccounts = 20;

    private LegalEntityAccount()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long LegalEntityId { get; private set; }
    public string BankName { get; private set; } = string.Empty;
    public string Bic { get; private set; } = string.Empty;
    public string Account { get; private set; } = string.Empty;
    public string? CorrAccount { get; private set; }

    /// <summary>Банк или касса (D80). У кассы нет БИК и номера счёта; BankName — её название («Касса цеха»).</summary>
    public MoneyAccountKind Kind { get; private set; } = MoneyAccountKind.Bank;

    /// <summary>Остаток на начало учёта в knitERP (D80); движения считаются с <see cref="OpeningDate"/> включительно.</summary>
    public decimal OpeningBalance { get; private set; }

    /// <summary>
    /// Лимит остатка наличных в кассе (D87, п. 2 Указания Банка России № 3210-У): сверх него наличные сдаются в банк. Малое
    /// предприятие и ИП вправе лимит не устанавливать — тогда пусто. Превышение показывается предупреждением, а не запретом.
    /// </summary>
    public decimal? CashLimit { get; private set; }

    public DateOnly? OpeningDate { get; private set; }

    public bool IsDefault { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsCash => Kind == MoneyAccountKind.Cash;

    /// <summary>Касса юрлица: только название. Основной для счетов на оплату касса не бывает.</summary>
    public static LegalEntityAccount CreateCash(long organizationId, long legalEntityId, string? name) =>
        new()
        {
            OrganizationId = organizationId, LegalEntityId = legalEntityId, Kind = MoneyAccountKind.Cash,
            BankName = DomainText.Require(name, BankNameMaxLength, "Название кассы"),
        };

    public void RenameCash(string? name)
    {
        if (!IsCash)
        {
            throw new BusinessRuleException("money.not_cash", "Это расчётный счёт, не касса.");
        }

        BankName = DomainText.Require(name, BankNameMaxLength, "Название кассы");
    }

    public void SetCashLimit(decimal? limit)
    {
        if (!IsCash)
        {
            throw new BusinessRuleException("money.cash_limit.not_cash", "Лимит остатка устанавливается только для кассы.");
        }

        if (limit is { } l && (l < 0 || Money.Round(l) != l || l > 999_999_999_999m))
        {
            throw new BusinessRuleException("money.cash_limit", "Лимит остатка кассы — не меньше нуля, до копеек.");
        }

        CashLimit = limit;
    }

    /// <summary>Начальный остаток на дату (D80): может быть и отрицательным (овердрафт), до копеек.</summary>
    public void SetOpening(DateOnly? date, decimal amount)
    {
        if (Money.Round(amount) != amount || Math.Abs(amount) > 999_999_999_999m)
        {
            throw new BusinessRuleException("money.opening", "Начальный остаток — до копеек.");
        }

        if (amount != 0 && date is null)
        {
            throw new BusinessRuleException("money.opening_date", "Укажите дату начального остатка.");
        }

        OpeningBalance = amount;
        OpeningDate = date;
    }

    public static LegalEntityAccount Create(long organizationId, long legalEntityId, string countryCode, string? bankName, string? bic, string? account,
        string? corrAccount, bool isDefault)
    {
        var a = new LegalEntityAccount { OrganizationId = organizationId, LegalEntityId = legalEntityId, IsDefault = isDefault };
        a.Set(countryCode, bankName, bic, account, corrAccount);
        return a;
    }

    public void Set(string countryCode, string? bankName, string? bic, string? account, string? corrAccount)
    {
        if (IsArchived)
        {
            throw new BusinessRuleException("legal_entity.account_archived", "Счёт в архиве.");
        }

        if (IsCash)
        {
            throw new BusinessRuleException("money.not_bank", "Это касса — у неё нет банковских реквизитов.");
        }

        var name = DomainText.Require(bankName, BankNameMaxLength, "Банк");
        var b = Code(bic, BicMaxLength, "БИК") ?? throw new BusinessRuleException("org.bank.bik_required", "Укажите БИК банка.");
        var acc = Code(account, AccountMaxLength, "Расчётный счёт") ?? throw new BusinessRuleException("org.bank.account_required", "Укажите номер счёта.");
        var corr = Code(corrAccount, AccountMaxLength, "Корреспондентский счёт");
        if (countryCode == Countries.Russia)
        {
            if (!RussianRequisites.IsValidBik(b))
            {
                throw new BusinessRuleException("org.bank.bik", "БИК — 9 цифр, начинается с 04.");
            }

            if (!RussianRequisites.IsValidSettlementAccount(acc, b))
            {
                throw new BusinessRuleException("org.bank.account", "Расчётный счёт не сходится с БИК: проверьте 20 цифр счёта и БИК.");
            }

            if (corr is not null && !RussianRequisites.IsValidCorrespondentAccount(corr, b))
            {
                throw new BusinessRuleException("org.bank.corr", "Корреспондентский счёт не сходится с БИК: проверьте 20 цифр (начинается с 301).");
            }
        }

        BankName = name;
        Bic = b;
        Account = acc;
        CorrAccount = corr;
    }

    public void SetDefault(bool isDefault)
    {
        if (isDefault)
        {
            EnsureCanBeDefault();
        }

        IsDefault = isDefault;
    }

    /// <summary>Проверка до снятия признака с прежнего основного счёта: иначе неудача оставила бы юрлицо без основного.</summary>
    public void EnsureCanBeDefault()
    {
        if (IsCash)
        {
            throw new BusinessRuleException("money.cash_default", "Касса не может быть основным счётом для оплаты по счетам.");
        }

        if (IsArchived)
        {
            throw new BusinessRuleException("legal_entity.account_archived", "Счёт в архиве.");
        }
    }

    public void SetArchived(bool archived)
    {
        IsArchived = archived;
        if (archived)
        {
            IsDefault = false;
        }
    }

    /// <summary>Код или номер счёта: без пробелов, только латинские буквы и цифры.</summary>
    private static string? Code(string? value, int maxLength, string field)
    {
        var v = value?.Replace(" ", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }

        if (v.Length > maxLength || !v.All(char.IsAsciiLetterOrDigit))
        {
            throw new BusinessRuleException("org.bank.code", $"Поле «{field}»: только цифры и латинские буквы, не длиннее {maxLength}.");
        }

        return v;
    }
}
