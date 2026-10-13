using KnitErp.Domain.Common;

namespace KnitErp.Domain.Automation;

/// <summary>
/// D145: настройки автоматизации организации (волна 2, docs/automation.md А6, А9, А10). Одна строка на организацию; пока её нет —
/// значения по умолчанию: автозакрытие заказов и регулярные операции включены (они ничего не проводят молча), напоминания покупателям
/// письмом выключены (письма уходят клиентам — включает организация).
/// </summary>
public sealed class AutomationSettings
{
    public const int MaxRemindDaysBefore = 30;
    public const int MaxOverdueRepeatDays = 60;
    public const int MaxOverdueReminders = 3;

    private AutomationSettings()
    {
    }

    public long OrganizationId { get; private set; }

    /// <summary>Напоминания покупателю об оплате счёта письмом (за N дней до срока и при просрочке).</summary>
    public bool PaymentRemindersEnabled { get; private set; }

    /// <summary>За сколько дней до срока оплаты напомнить (0 — в день срока).</summary>
    public int RemindDaysBefore { get; private set; } = 3;

    /// <summary>Повтор напоминания о просрочке через столько дней (0 — одно напоминание); всего не больше трёх.</summary>
    public int OverdueRepeatDays { get; private set; } = 7;

    /// <summary>Заказ покупателя, полностью отгруженный и оплаченный, закрывается сам.</summary>
    public bool AutoCloseSalesOrders { get; private set; } = true;

    /// <summary>Заказ поставщику, полностью поступивший и оплаченный, закрывается сам.</summary>
    public bool AutoClosePurchaseOrders { get; private set; } = true;

    /// <summary>Регулярные операции создают черновики по расписанию.</summary>
    public bool RecurringOperationsEnabled { get; private set; } = true;

    public long? UpdatedByUserId { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static AutomationSettings Default(long organizationId) => new() { OrganizationId = organizationId };

    public void Update(bool reminders, int daysBefore, int repeatDays, bool closeSales, bool closePurchases, bool recurring, long userId, DateTime nowUtc)
    {
        if (daysBefore is < 0 or > MaxRemindDaysBefore)
        {
            throw new BusinessRuleException("automation.remind_days", $"Напоминание до срока — за 0–{MaxRemindDaysBefore} дней.");
        }

        if (repeatDays is < 0 or > MaxOverdueRepeatDays)
        {
            throw new BusinessRuleException("automation.repeat_days", $"Повтор напоминания о просрочке — через 0–{MaxOverdueRepeatDays} дней.");
        }

        PaymentRemindersEnabled = reminders;
        RemindDaysBefore = daysBefore;
        OverdueRepeatDays = repeatDays;
        AutoCloseSalesOrders = closeSales;
        AutoClosePurchaseOrders = closePurchases;
        RecurringOperationsEnabled = recurring;
        UpdatedByUserId = userId;
        UpdatedAtUtc = nowUtc;
    }

    public string Describe() =>
        $"напоминания об оплате: {(PaymentRemindersEnabled ? $"да, за {RemindDaysBefore} дн., повтор через {OverdueRepeatDays} дн." : "нет")}; " +
        $"автозакрытие заказов покупателей: {(AutoCloseSalesOrders ? "да" : "нет")}; поставщикам: {(AutoClosePurchaseOrders ? "да" : "нет")}; " +
        $"регулярные операции: {(RecurringOperationsEnabled ? "да" : "нет")}";

    /// <summary>
    /// Номер напоминания о просрочке на дату: первое — на следующий день после срока, следующие — через repeatDays (0 — только одно),
    /// всего не больше трёх. 0 — напоминать не нужно.
    /// </summary>
    public static int OverdueReminderNumber(DateOnly dueDate, DateOnly today, int repeatDays)
    {
        var late = today.DayNumber - dueDate.DayNumber;
        if (late < 1)
        {
            return 0;
        }

        var n = repeatDays == 0 ? 1 : (late - 1) / repeatDays + 1;
        return Math.Min(n, MaxOverdueReminders);
    }
}
