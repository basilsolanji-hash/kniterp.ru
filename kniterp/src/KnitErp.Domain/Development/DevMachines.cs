using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>
/// Коды встроенных производителей (D710). С D734 производитель — запись справочника «Производители оборудования» (код —
/// <see cref="DevLookup.Number"/>), полный список — <see cref="DevLookupCatalog"/>.
/// </summary>
public enum DevMachineBrand : byte
{
    Stoll = 1,
    ShimaSeiki = 2,
    Other = 9,
}

/// <summary>Состояние станка: по нему дессинатор и цех видят, можно ли планировать вязку.</summary>
public enum DevMachineStatus : byte
{
    Working = 1,
    Limited = 2,
    Down = 3,
}

/// <summary>
/// Станок (вязальная машина) — справочник разработки (D710): марка, модель, класс, число нитеводителей, рабочая ширина, системы,
/// инвентарный номер, состояние. По нему дессинатор строит заправку (сколько нитеводителей), к нему привязываются программы.
/// Не удаляется — уходит в архив; комментарии и смена состояния — журнал <see cref="DevMachineNote"/> (только вставка).
/// </summary>
public sealed class DevMachine
{
    public const int NameMaxLength = 100;
    public const int ModelMaxLength = 60;
    public const int InventoryMaxLength = 30;
    public const int MaxFeeders = 32;
    public const int MaxGauge = 30;

    private DevMachine()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    /// <summary>Производитель — запись справочника «Производители оборудования» (D734).</summary>
    public long BrandId { get; private set; }

    /// <summary>Тип оборудования — запись справочника «Типы оборудования» (D734).</summary>
    public long? TypeId { get; private set; }

    public string? Model { get; private set; }

    /// <summary>Класс машины (E1,5 … E18) — игл на дюйм, с шагом 0,1.</summary>
    public decimal Gauge { get; private set; }

    public int FeederCount { get; private set; }
    public int? WidthCm { get; private set; }
    public int? Systems { get; private set; }
    public string? InventoryNumber { get; private set; }
    public DevMachineStatus Status { get; private set; }
    public bool IsArchived { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static string BrandName(DevMachineBrand brand) => brand switch
    {
        DevMachineBrand.Stoll => "STOLL",
        DevMachineBrand.ShimaSeiki => "Shima Seiki",
        _ => "Другая",
    };

    public static string StatusName(DevMachineStatus status) => status switch
    {
        DevMachineStatus.Working => "Работает",
        DevMachineStatus.Limited => "Работает с ограничениями",
        _ => "Не работает",
    };

    /// <summary>Класс машины для вывода: «E7», «E1,5».</summary>
    public static string GaugeText(decimal gauge) => "E" + gauge.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>Параметры для программы и техкарты: «Stoll CMS 530 · E7 · 8 нитеводителей»; производитель — название записи справочника.</summary>
    public string Parameters(string? brand) => string.Join(" · ", new[]
    {
        $"{brand}{(Model is null ? "" : " " + Model)}".Trim(), GaugeText(Gauge), $"{FeederCount} нитеводителей",
        WidthCm is { } w ? $"{w} см" : null, Systems is { } s ? $"{s} сист." : null,
    }.Where(p => p is not null));

    public static DevMachine Create(long organizationId, DevMachineValues values)
    {
        var m = new DevMachine { OrganizationId = organizationId, Status = DevMachineStatus.Working };
        m.Set(values);
        return m;
    }

    public void Set(DevMachineValues v)
    {
        if (v.BrandId <= 0)
        {
            throw new BusinessRuleException("dev.machine.brand", "Выберите марку станка.");
        }

        if (v.Gauge is < 1 or > MaxGauge || decimal.Round(v.Gauge, 1) != v.Gauge)
        {
            throw new BusinessRuleException("dev.machine.gauge", $"Класс машины — от 1 до {MaxGauge}, с шагом 0,1.");
        }

        if (v.FeederCount is < 1 or > MaxFeeders)
        {
            throw new BusinessRuleException("dev.machine.feeders", $"Нитеводителей — от 1 до {MaxFeeders}.");
        }

        if (v.WidthCm is < 1 or > 500 || v.Systems is < 1 or > 12)
        {
            throw new BusinessRuleException("dev.machine.size", "Ширина — до 500 см, систем — от 1 до 12.");
        }

        Name = DomainText.Require(v.Name, NameMaxLength, "Название");
        BrandId = v.BrandId;
        TypeId = v.TypeId;
        Model = DomainText.Optional(v.Model, ModelMaxLength, "Модель");
        Gauge = v.Gauge;
        FeederCount = v.FeederCount;
        WidthCm = v.WidthCm;
        Systems = v.Systems;
        InventoryNumber = DomainText.Optional(v.InventoryNumber, InventoryMaxLength, "Инвентарный номер");
    }

    /// <summary>Запись журнала станка; при смене состояния состояние станка меняется вместе с записью.</summary>
    public DevMachineNote AddNote(string? text, DevMachineStatus? status, DateTime nowUtc, long userId)
    {
        if (status is { } s && !Enum.IsDefined(s))
        {
            throw new BusinessRuleException("dev.machine.status", "Выберите состояние станка.");
        }

        var note = DevMachineNote.Create(this, text, status is { } st && st != Status ? st : null, nowUtc, userId);
        if (note.StatusAfter is { } after)
        {
            Status = after;
        }

        return note;
    }

    public void SetArchived(bool archived) => IsArchived = archived;
}

public sealed record DevMachineValues(string? Name, long BrandId, string? Model, decimal Gauge, int FeederCount, int? WidthCm, int? Systems,
    string? InventoryNumber, long? TypeId = null);

/// <summary>Комментарий к станку (D710): «перенёс, плохо работает» — кто, когда, что и как изменилось состояние. Только вставка.</summary>
public sealed class DevMachineNote
{
    public const int TextMaxLength = 500;

    private DevMachineNote()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long MachineId { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public DevMachineStatus? StatusAfter { get; private set; }
    public DateTime AtUtc { get; private set; }
    public long UserId { get; private set; }

    internal static DevMachineNote Create(DevMachine machine, string? text, DevMachineStatus? statusAfter, DateTime nowUtc, long userId) => new()
    {
        OrganizationId = machine.OrganizationId,
        MachineId = machine.Id,
        Text = statusAfter is not null && string.IsNullOrWhiteSpace(text)
            ? $"Состояние: {DevMachine.StatusName(statusAfter.Value)}"
            : DomainText.Require(text, TextMaxLength, "Комментарий"),
        StatusAfter = statusAfter,
        AtUtc = nowUtc,
        UserId = userId,
    };
}
