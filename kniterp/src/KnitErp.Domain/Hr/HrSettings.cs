using KnitErp.Domain.Common;

namespace KnitErp.Domain.Hr;

/// <summary>База оплаты по окладу: часы табеля к норме часов или дни к норме дней.</summary>
public enum SalaryBase : byte
{
    Hours = 1,
    Days = 2,
}

/// <summary>Параметры заработка (D216). Значения не ниже минимума ТК РФ — их можно только увеличить.</summary>
public sealed record PayrollSettingsValues(
    decimal NightPercent = HrSettings.MinNightPercent,
    decimal OvertimeFirst = HrSettings.MinOvertimeFirst,
    decimal OvertimeNext = HrSettings.MinOvertimeNext,
    decimal HolidayMultiplier = HrSettings.MinHolidayMultiplier,
    SalaryBase SalaryBase = SalaryBase.Hours,
    decimal? PieceHourlyRate = null,
    string? ZupVersion = null,
    string? ZupPieceCode = null,
    string? ZupSalaryCode = null,
    string? ZupNightCode = null,
    string? ZupOvertimeCode = null,
    string? ZupHolidayCode = null);

/// <summary>Кадровые правила организации (D216).</summary>
/// <param name="RequireAgreements">D227: сотрудник со своей учётной записью сначала подписывает согласие на обработку ПДн, правила работы в системе и обязательство о конфиденциальности.</param>
public sealed record HrRulesValues(bool HrOfficerClosesTimesheet = false, bool LeaveOnlyByOrder = false, bool BlockPersonalDataWithoutConsent = false,
    bool RequireAgreements = true);

/// <summary>
/// Настройки модуля «Сотрудники» для организации (D216): доплаты и база оклада для расчёта, коды видов начислений для 1С: ЗУП,
/// кадровые правила. Одна запись на организацию; нет записи — значения по умолчанию (минимум ТК РФ, правила выключены).
/// </summary>
public sealed class HrSettings
{
    public const decimal MinNightPercent = 20m;
    public const decimal MinOvertimeFirst = 1.5m;
    public const decimal MinOvertimeNext = 2m;
    public const decimal MinHolidayMultiplier = 2m;
    public const int CodeMaxLength = 100;

    private HrSettings()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public decimal NightPercent { get; private set; } = MinNightPercent;
    public decimal OvertimeFirst { get; private set; } = MinOvertimeFirst;
    public decimal OvertimeNext { get; private set; } = MinOvertimeNext;
    public decimal HolidayMultiplier { get; private set; } = MinHolidayMultiplier;
    public SalaryBase SalaryBase { get; private set; } = SalaryBase.Hours;
    public decimal? PieceHourlyRate { get; private set; }
    public string? ZupVersion { get; private set; }
    public string? ZupPieceCode { get; private set; }
    public string? ZupSalaryCode { get; private set; }
    public string? ZupNightCode { get; private set; }
    public string? ZupOvertimeCode { get; private set; }
    public string? ZupHolidayCode { get; private set; }
    public bool HrOfficerClosesTimesheet { get; private set; }
    public bool LeaveOnlyByOrder { get; private set; }
    public bool BlockPersonalDataWithoutConsent { get; private set; }
    public bool RequireAgreements { get; private set; } = true;
    public DateTime UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static HrSettings Default(long organizationId, DateTime nowUtc) => new() { OrganizationId = organizationId, UpdatedAtUtc = nowUtc };

    public PayrollSettingsValues Payroll => new(NightPercent, OvertimeFirst, OvertimeNext, HolidayMultiplier, SalaryBase, PieceHourlyRate, ZupVersion,
        ZupPieceCode, ZupSalaryCode, ZupNightCode, ZupOvertimeCode, ZupHolidayCode);

    public HrRulesValues Rules => new(HrOfficerClosesTimesheet, LeaveOnlyByOrder, BlockPersonalDataWithoutConsent, RequireAgreements);

    /// <summary>Параметры заработка. Возвращает описание изменений для журнала (без сумм ставок сотрудников).</summary>
    public string? SetPayroll(PayrollSettingsValues v, DateTime nowUtc)
    {
        Min(v.NightPercent, MinNightPercent, 200m, "Доплата за ночные часы, %", "ст. 154 ТК РФ, не меньше 20 %");
        Min(v.OvertimeFirst, MinOvertimeFirst, 5m, "Сверхурочные: первые 2 часа", "ст. 152 ТК РФ, не меньше 1,5");
        Min(v.OvertimeNext, MinOvertimeNext, 5m, "Сверхурочные: следующие часы", "ст. 152 ТК РФ, не меньше 2");
        Min(v.HolidayMultiplier, MinHolidayMultiplier, 5m, "Работа в выходные и праздники", "ст. 153 ТК РФ, не меньше 2");
        if (!Enum.IsDefined(v.SalaryBase))
        {
            throw new BusinessRuleException("hr.settings.salary_base", "Неизвестная база оплаты по окладу.");
        }

        if (v.PieceHourlyRate is <= 0)
        {
            throw new BusinessRuleException("hr.settings.piece_rate", "Часовая тарифная ставка сдельщиков — больше нуля или пусто.");
        }

        var before = Payroll;
        NightPercent = v.NightPercent;
        OvertimeFirst = v.OvertimeFirst;
        OvertimeNext = v.OvertimeNext;
        HolidayMultiplier = v.HolidayMultiplier;
        SalaryBase = v.SalaryBase;
        PieceHourlyRate = v.PieceHourlyRate is { } rate ? Money.Round(rate) : null;
        ZupVersion = Code(v.ZupVersion, "Версия 1С: ЗУП");
        ZupPieceCode = Code(v.ZupPieceCode, "Код вида начисления");
        ZupSalaryCode = Code(v.ZupSalaryCode, "Код вида начисления");
        ZupNightCode = Code(v.ZupNightCode, "Код вида начисления");
        ZupOvertimeCode = Code(v.ZupOvertimeCode, "Код вида начисления");
        ZupHolidayCode = Code(v.ZupHolidayCode, "Код вида начисления");
        UpdatedAtUtc = nowUtc;
        var after = Payroll;
        return before == after ? null
            : $"ночные {after.NightPercent:0.##} %, сверхурочные ×{after.OvertimeFirst:0.##}/×{after.OvertimeNext:0.##}, праздничные ×{after.HolidayMultiplier:0.##}, "
              + $"оклад {(after.SalaryBase == SalaryBase.Hours ? "по часам" : "по дням")}, тариф сдельщиков {(after.PieceHourlyRate is null ? "нет" : "задан")}";
    }

    /// <summary>Кадровые правила. Возвращает описание изменений для журнала.</summary>
    public string? SetRules(HrRulesValues v, DateTime nowUtc)
    {
        var before = Rules;
        HrOfficerClosesTimesheet = v.HrOfficerClosesTimesheet;
        LeaveOnlyByOrder = v.LeaveOnlyByOrder;
        BlockPersonalDataWithoutConsent = v.BlockPersonalDataWithoutConsent;
        RequireAgreements = v.RequireAgreements;
        UpdatedAtUtc = nowUtc;
        return before == Rules ? null
            : $"кадровик закрывает табель: {(v.HrOfficerClosesTimesheet ? "да" : "нет")}, отпуск только по приказу: {(v.LeaveOnlyByOrder ? "да" : "нет")}, "
              + $"ПДн без согласия: {(v.BlockPersonalDataWithoutConsent ? "запрещены" : "разрешены")}, "
              + $"подписание документов перед доступом: {(v.RequireAgreements ? "да" : "нет")}";
    }

    /// <summary>Код вида начисления для ЗУП: из настроек или стандартный.</summary>
    public static string ZupCode(PayrollSettingsValues s, PayrollLineKind kind) => kind switch
    {
        PayrollLineKind.Piece or PayrollLineKind.PieceCorrection => s.ZupPieceCode ?? "СдельнаяОплата",
        PayrollLineKind.Salary => s.ZupSalaryCode ?? "ОплатаПоОкладу",
        PayrollLineKind.Night => s.ZupNightCode ?? "ДоплатаЗаРаботуВНочноеВремя",
        PayrollLineKind.Overtime => s.ZupOvertimeCode ?? "ОплатаСверхурочныхЧасов",
        PayrollLineKind.Holiday => s.ZupHolidayCode ?? "ОплатаПраздничныхИВыходныхДней",
        _ => kind.ToString(),
    };

    private static void Min(decimal value, decimal min, decimal max, string field, string rule)
    {
        if (value < min || value > max || decimal.Round(value, 2) != value)
        {
            throw new BusinessRuleException("hr.settings.minimum", $"«{field}»: {rule}, до двух знаков после запятой.");
        }
    }

    private static string? Code(string? value, string field)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }

        return v.Length <= CodeMaxLength ? v : throw new BusinessRuleException("field.too_long", $"Поле «{field}» длиннее {CodeMaxLength} символов.");
    }
}
