using KnitErp.Application.Access;
using KnitErp.Application.Automation;
using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Automation;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Напоминание ответственному в колокольчик: срок оплаты счёта скоро.</summary>
public sealed record PaymentDueAlert(long InvoiceId, string InvoiceNumber, string Customer, decimal Outstanding, DateOnly DueDate, long OrderId)
{
    public string Key => $"paydue:{InvoiceId}:{DueDate:yyyyMMdd}";
}

/// <summary>
/// D148 (А10): напоминания об оплате. Включаются в «Настройках автоматизации» (по умолчанию выключены). Фоновая служба ставит в очередь
/// писем продаж (D1123) письмо покупателю со счётом: за N дней до срока (одно на счёт) и при просрочке (на следующий день после срока,
/// затем через заданное число дней, всего не больше трёх). Условия: счёт выставлен, у него есть срок, по заказу оплачено меньше суммы
/// счёта, у покупателя указана почта, нет «не отправлять автоматически» и он не отписался от напоминаний (ссылка в письме).
/// Повтор прохода не дублирует письма (DedupKey). Ответственному по заказу — уведомление в колокольчик за те же N дней; о просрочке
/// ему уже напоминает D1113. В журнал приложения — только числа, без почты и названий (ПДн).
/// </summary>
public static class PaymentReminders
{
    public const int Batch = 500;
    public const string UnsubscribePath = "account/payment-reminders-off";

    /// <summary>Поставить письма во всех организациях, где напоминания включены. Возвращает число поставленных писем.</summary>
    public static async Task<int> QueueAsync(IKnitErpDbContext db, IClock clock, string? publicUrl, CancellationToken ct = default)
    {
        var queued = 0;
        var orgs = await db.AutomationSettings.AsNoTracking().Where(s => s.PaymentRemindersEnabled).Select(s => s.OrganizationId).ToListAsync(ct);
        foreach (var org in orgs)
        {
            queued += await QueueForAsync(db, clock, org, publicUrl, ct);
        }

        return queued;
    }

    public static async Task<int> QueueForAsync(IKnitErpDbContext db, IClock clock, long org, string? publicUrl, CancellationToken ct)
    {
        var settings = await AutomationSettingsService.ForAsync(db, org, ct);
        if (!settings.PaymentRemindersEnabled)
        {
            return 0;
        }

        var today = await SalesCredit.TodayAsync(db, org, clock.UtcNow, ct);
        var invoices = await Unpaid(db, org).Where(i => i.DueDate <= today.AddDays(settings.RemindDaysBefore))
            .OrderBy(i => i.DueDate).Take(Batch).ToListAsync(ct);
        var queued = 0;
        foreach (var i in invoices)
        {
            var (kind, key) = i.DueDate >= today
                ? (SalesMailKind.PaymentReminder, $"remind:{i.Id}:before")
                : AutomationSettings.OverdueReminderNumber(i.DueDate, today, settings.OverdueRepeatDays) is var n and > 0
                    ? (SalesMailKind.PaymentOverdue, $"remind:{i.Id}:overdue:{n}")
                    : (SalesMailKind.PaymentOverdue, string.Empty);
            if (key.Length == 0 || await db.SalesMailMessages.AsNoTracking().AnyAsync(m => m.OrganizationId == org && m.DedupKey == key, ct))
            {
                continue;
            }

            var mail = await db.CustomerMailSettings.AsNoTracking().SingleOrDefaultAsync(s => s.OrganizationId == org && s.CounterpartyId == i.CustomerId, ct);
            if (mail is not { Email: not null, NoAutoSend: false, NoPaymentReminders: false })
            {
                continue;
            }

            var values = await SalesMailOutbox.ValuesAsync(db, org, SalesMailKind.Invoice, i.SalesOrderId, i.Id, null, ct) with { Amount = i.Total - i.Paid };
            var (subject, body, _) = await SalesMailOutbox.TemplateAsync(db, org, kind, ct);
            var text = SalesMailText.RenderDue(SalesMailOutbox.Render(body, values), i.DueDate);
            if (publicUrl is { Length: > 0 } url)
            {
                text += $"\n\nНе хотите получать напоминания об оплате — {url.TrimEnd('/')}/{UnsubscribePath}/{mail.ReminderToken:N}";
            }

            db.SalesMailMessages.Add(SalesMailMessage.Create(org, kind, i.SalesOrderId, i.CustomerId, mail.Email,
                SalesMailText.RenderDue(SalesMailOutbox.Render(subject, values), i.DueDate), text, true, key, i.ResponsibleUserId ?? i.CreatedByUserId,
                clock.UtcNow, [(SalesMailDocument.Invoice, i.Id)]));
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, null, AuditActions.SalesMailQueued, nameof(SalesOrder), i.SalesOrderId.ToString(), null,
                SalesMailMessage.KindName(kind), $"{i.Number}: автоматически, срок {i.DueDate:dd.MM.yyyy}", null));
            await db.SaveChangesAsync(ct);
            queued++;
        }

        return queued;
    }

    /// <summary>Колокольчик ответственного: счета его заказов со сроком в ближайшие N дней и неоплаченным остатком (только если напоминания включены).</summary>
    public static async Task<IReadOnlyList<PaymentDueAlert>> AlertsAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, CancellationToken ct)
    {
        if (!ctx.Permissions.Has(Permissions.SalesView) || !ctx.Permissions.Has(Permissions.PriceView))
        {
            return [];
        }

        var settings = await AutomationSettingsService.ForAsync(db, ctx.OrganizationId, ct);
        if (!settings.PaymentRemindersEnabled)
        {
            return [];
        }

        var rows = await Unpaid(db, ctx.OrganizationId)
            .Where(i => i.ResponsibleUserId == ctx.UserId && i.DueDate >= today && i.DueDate <= today.AddDays(settings.RemindDaysBefore))
            .OrderBy(i => i.DueDate).Take(20).ToListAsync(ct);
        var ids = rows.Select(r => r.CustomerId).Distinct().ToList();
        var names = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == ctx.OrganizationId && ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return rows.Select(r => new PaymentDueAlert(r.Id, r.Number, names[r.CustomerId], r.Total - r.Paid, r.DueDate, r.SalesOrderId)).ToList();
    }

    /// <summary>Отписка по ссылке из письма (без входа). Ответ одинаковый для любого ключа. Возвращает true, если что-то изменилось.</summary>
    public static async Task<bool> UnsubscribeAsync(IKnitErpDbContext db, Guid token, DateTime nowUtc, CancellationToken ct = default)
    {
        var s = await db.CustomerMailSettings.SingleOrDefaultAsync(x => x.ReminderToken == token, ct);
        if (s is null || s.NoPaymentReminders)
        {
            return false;
        }

        s.SetPaymentReminders(false);
        db.AuditEntries.Add(AuditEntry.Create(nowUtc, s.OrganizationId, null, AuditActions.PaymentReminderUnsubscribed, nameof(CustomerMailSettings),
            s.CounterpartyId.ToString(), "напоминания об оплате", "отписан по ссылке из письма", null, null));
        await db.SaveChangesAsync(ct);
        return true;
    }

    private sealed record UnpaidInvoice
    {
        public long Id { get; init; }
        public string Number { get; init; } = string.Empty;
        public DateOnly DueDate { get; init; }
        public long SalesOrderId { get; init; }
        public long CustomerId { get; init; }
        public long? ResponsibleUserId { get; init; }
        public long CreatedByUserId { get; init; }
        public decimal Total { get; init; }
        public decimal Paid { get; init; }
    }

    /// <summary>Выставленные счета со сроком, по заказу которых оплачено меньше суммы счёта (как «просроченные счета» главного экрана).</summary>
    private static IQueryable<UnpaidInvoice> Unpaid(IKnitErpDbContext db, long org) =>
        db.CustomerInvoices.AsNoTracking()
            .Where(i => i.OrganizationId == org && i.Status == CustomerInvoiceStatus.Issued && i.DueDate != null)
            .Join(db.SalesOrders.AsNoTracking(), i => i.SalesOrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status == SalesOrderStatus.Confirmed || x.o.Status == SalesOrderStatus.Closed)
            .Select(x => new UnpaidInvoice
            {
                Id = x.i.Id, Number = x.i.Number, DueDate = x.i.DueDate!.Value, SalesOrderId = x.i.SalesOrderId, CustomerId = x.i.CustomerId,
                ResponsibleUserId = x.o.ResponsibleUserId, CreatedByUserId = x.o.CreatedByUserId,
                Total = x.i.Lines.Sum(l => l.Amount),
                Paid = PaidByOrder.Sales(db, org).Where(a => a.OrderId == x.i.SalesOrderId).Sum(a => (decimal?)a.Amount) ?? 0m,
            })
            .Where(x => x.Paid < x.Total);
}
