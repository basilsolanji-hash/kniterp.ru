using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>Производитель вязальной машины (D710). Числа хранятся в базе.</summary>
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
    public DevMachineBrand Brand { get; private set; }
    public string? Model { get; private set; }

    /// <summary>Класс машины (E7, E12…) — игл на дюйм.</summary>
    public int Gauge { get; private set; }

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

    /// <summary>Параметры для программы и техкарты: «STOLL CMS 530 · E7 · 8 нитеводителей».</summary>
    public string Parameters => string.Join(" · ", new[]
    {
        $"{BrandName(Brand)}{(Model is null ? "" : " " + Model)}", $"E{Gauge}", $"{FeederCount} нитеводителей",
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
        if (!Enum.IsDefined(v.Brand))
        {
            throw new BusinessRuleException("dev.machine.brand", "Выберите марку станка.");
        }

        if (v.Gauge is < 1 or > MaxGauge)
        {
            throw new BusinessRuleException("dev.machine.gauge", $"Класс машины — от 1 до {MaxGauge}.");
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
        Brand = v.Brand;
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

public sealed record DevMachineValues(string? Name, DevMachineBrand Brand, string? Model, int Gauge, int FeederCount, int? WidthCm, int? Systems,
    string? InventoryNumber);

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
