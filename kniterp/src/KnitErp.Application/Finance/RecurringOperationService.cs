using KnitErp.Application.Access;
using KnitErp.Application.Automation;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Finance;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Finance;

public sealed record RecurringOperationDto(
    long Id, string Name, MoneyOperationKind Kind, long AccountId, string Account, long? CashFlowItemId, string? CashFlowItem, decimal Amount, string? Party,
    string Basis, RecurrencePeriod Period, int Day, DateOnly StartDate, DateOnly? EndDate, DateOnly NextDate, bool IsArchived, byte[] RowVersion);

public sealed record RecurringRunDto(
    long Id, long TemplateId, string Name, MoneyOperationKind Kind, string Account, decimal Amount, DateOnly DueDate, RecurringRunStatus Status,
    string? MoneyOperationNumber, string? SkipReason, byte[] RowVersion);

public sealed record RecurringOverviewDto(
    IReadOnlyList<RecurringOperationDto> Templates, IReadOnlyList<RecurringRunDto> Runs, IReadOnlyList<MoneyAccountOptionDto> Accounts,
    IReadOnlyList<(long Id, string Name, CashFlowDirection Direction)> Items, bool Enabled, bool CanEdit);

/// <summary>
/// D147: регулярные денежные операции. Шаблон с расписанием → по расписанию черновик (фоновая служба, <see cref="GenerateAsync"/>) →
/// человек проводит (создаётся денежная операция обычным путём: права, закрытый период, остаток кассы) или пропускает с причиной.
/// Права — как у денежных операций: «Цены и суммы» и продажи или закупки (изменение). Всё — в журнал аудита; черновик — один на дату.
/// </summary>
public sealed class RecurringOperationService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public const int RunsShown = 100;

    public async Task<RecurringOverviewDto> GetAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        var org = ctx.OrganizationId;
        var accounts = await MoneyService.OptionsAsync(db, org, ct);
        var labels = accounts.ToDictionary(a => a.Id, a => a.Label);
        var items = await db.CashFlowItems.AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsArchived && i.SystemCode != CashFlowItem.CustomerPayments && i.SystemCode != CashFlowItem.SupplierPayments
                        && i.SystemCode != CashFlowItem.AccountableIssue && i.SystemCode != CashFlowItem.AccountableReturn)
            .OrderBy(i => i.Name).Select(i => new { i.Id, i.Name, i.Direction }).ToListAsync(ct);
        var allItems = await db.CashFlowItems.AsNoTracking().Where(i => i.OrganizationId == org).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        var templates = await db.RecurringOperations.AsNoTracking().Where(r => r.OrganizationId == org)
            .OrderBy(r => r.IsArchived).ThenBy(r => r.NextDate).ThenBy(r => r.Name).ToListAsync(ct);
        var runs = await (from r in db.RecurringOperationRuns.AsNoTracking()
                          where r.OrganizationId == org
                          join t in db.RecurringOperations.AsNoTracking() on r.RecurringOperationId equals t.Id
                          join m in db.MoneyOperations.AsNoTracking() on r.MoneyOperationId equals m.Id into mm
                          from m in mm.DefaultIfEmpty()
                          orderby r.Status == RecurringRunStatus.Pending descending, r.DueDate descending, r.Id descending
                          select new { r, t.Name, t.Kind, t.AccountId, Number = m == null ? null : m.Number })
            .Take(RunsShown).ToListAsync(ct);
        var settings = await AutomationSettingsService.ForAsync(db, org, ct);
        string AccountLabel(long id) => labels.GetValueOrDefault(id) ?? "—";
        return new RecurringOverviewDto(
            templates.Select(t => new RecurringOperationDto(t.Id, t.Name, t.Kind, t.AccountId, AccountLabel(t.AccountId), t.CashFlowItemId,
                t.CashFlowItemId is { } i ? allItems.GetValueOrDefault(i) : null, t.Amount, t.Party, t.Basis, t.Period, t.Day, t.StartDate, t.EndDate, t.NextDate,
                t.IsArchived, t.RowVersion)).ToList(),
            runs.Select(x => new RecurringRunDto(x.r.Id, x.r.RecurringOperationId, x.Name, x.Kind, AccountLabel(x.AccountId), x.r.Amount, x.r.DueDate, x.r.Status,
                x.Number, x.r.SkipReason, x.r.RowVersion)).ToList(),
            accounts, items.Select(i => (i.Id, i.Name, i.Direction)).ToList(), settings.RecurringOperationsEnabled, CanEdit(ctx));
    }

    public async Task<long> CreateAsync(RecurringOperationData data, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        await ValidateAsync(ctx, data, ct);
        var t = RecurringOperation.Create(ctx.OrganizationId, data, ctx.UserId, clock.UtcNow);
        db.RecurringOperations.Add(t);
        try
        {
            await db.SaveChangesAsync(ct);
            Audit(ctx, AuditActions.RecurringOperationChanged, nameof(RecurringOperation), t.Id, null, t.Describe(), "создан шаблон");
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        return t.Id;
    }

    public async Task UpdateAsync(long id, RecurringOperationData data, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var t = await db.RecurringOperations.SingleOrDefaultAsync(r => r.Id == id && r.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Регулярная операция");
        try
        {
            t.EnsureVersion(t.RowVersion, rowVersion);
            await ValidateAsync(ctx, data, ct);
            var before = t.Describe();
            t.Update(data);
            Audit(ctx, AuditActions.RecurringOperationChanged, nameof(RecurringOperation), t.Id, before, t.Describe(), null);
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Отключить (в архив) или включить снова — пропущенное в архиве не догоняется.</summary>
    public async Task SetArchivedAsync(long id, bool archived, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var t = await db.RecurringOperations.SingleOrDefaultAsync(r => r.Id == id && r.OrganizationId == ctx.OrganizationId, ct)
                ?? throw new NotFoundException("Регулярная операция");
        t.EnsureVersion(t.RowVersion, rowVersion);
        if (archived)
        {
            t.Archive();
        }
        else
        {
            t.Restore(await TodayAsync(db, ctx.OrganizationId, clock.UtcNow, ct));
        }

        Audit(ctx, AuditActions.RecurringOperationChanged, nameof(RecurringOperation), t.Id, archived ? "действует" : "отключён",
            archived ? "отключён" : "действует", t.Name);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>
    /// Провести черновик: денежная операция на дату (по умолчанию — дата черновика) и сумму (по умолчанию — из шаблона). Повторное
    /// нажатие и одновременное проведение не создают второй операции: черновик блокируется и проверяется под транзакцией.
    /// </summary>
    public async Task<long> PostRunAsync(long runId, DateOnly? date, decimal? amount, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        try
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.LockAsync($"kniterp.recurring.run.{ctx.OrganizationId}.{runId}", ct);
            var run = await db.RecurringOperationRuns.SingleOrDefaultAsync(r => r.Id == runId && r.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Черновик регулярной операции");
            await db.ReloadAsync(run, ct);
            run.EnsureVersion(run.RowVersion, rowVersion);
            if (run.Status != RecurringRunStatus.Pending)
            {
                throw new BusinessRuleException("recurring.run.decided", "Черновик уже проведён или пропущен.");
            }

            var t = await db.RecurringOperations.AsNoTracking().SingleAsync(r => r.Id == run.RecurringOperationId && r.OrganizationId == ctx.OrganizationId, ct);
            var opDate = date ?? run.DueDate;
            var opAmount = amount ?? run.Amount;
            var operationId = await new MoneyOperationService(db, guard, currentUser, clock).CreateAsync(new MoneyOperationCommand(
                opDate, t.Kind, t.AccountId, null, opAmount, t.Party, t.Basis, $"Регулярная операция «{t.Name}» за {run.DueDate:dd.MM.yyyy}", t.CashFlowItemId), ct);
            run.MarkPosted(operationId, ctx.UserId, clock.UtcNow);
            Audit(ctx, AuditActions.RecurringRunPosted, nameof(RecurringOperationRun), run.Id, RecurringOperationRun.StatusName(RecurringRunStatus.Pending),
                $"{opAmount:0.00}", $"{t.Name} за {run.DueDate:dd.MM.yyyy}");
            await db.SaveOrConflictAsync(ct);
            await tx.CommitAsync(ct);
            return operationId;
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    public async Task SkipRunAsync(long runId, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        try
        {
            var run = await db.RecurringOperationRuns.SingleOrDefaultAsync(r => r.Id == runId && r.OrganizationId == ctx.OrganizationId, ct)
                      ?? throw new NotFoundException("Черновик регулярной операции");
            run.EnsureVersion(run.RowVersion, rowVersion);
            run.Skip(reason, ctx.UserId, clock.UtcNow);
            Audit(ctx, AuditActions.RecurringRunSkipped, nameof(RecurringOperationRun), run.Id, RecurringOperationRun.StatusName(RecurringRunStatus.Pending),
                RecurringOperationRun.StatusName(RecurringRunStatus.Skipped), run.SkipReason);
            await db.SaveOrConflictAsync(ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>
    /// Фоновая служба: черновики по расписанию во всех организациях, где регулярные операции включены. Ничего не проводит. Повторный
    /// запуск безопасен: черновик на дату — один (проверка и UNIQUE). Возвращает число созданных черновиков.
    /// </summary>
    public static async Task<int> GenerateAsync(IKnitErpDbContext db, IClock clock, CancellationToken ct = default)
    {
        var created = 0;
        var orgs = await db.RecurringOperations.AsNoTracking().Where(r => !r.IsArchived).Select(r => r.OrganizationId).Distinct().ToListAsync(ct);
        foreach (var org in orgs)
        {
            if (!(await AutomationSettingsService.ForAsync(db, org, ct)).RecurringOperationsEnabled)
            {
                continue;
            }

            var today = await TodayAsync(db, org, clock.UtcNow, ct);
            var due = await db.RecurringOperations.Where(r => r.OrganizationId == org && !r.IsArchived && r.NextDate <= today).ToListAsync(ct);
            foreach (var t in due)
            {
                var dates = t.TakeDue(today).ToList();
                var existing = await db.RecurringOperationRuns.AsNoTracking().Where(r => r.OrganizationId == org && r.RecurringOperationId == t.Id && dates.Contains(r.DueDate))
                    .Select(r => r.DueDate).ToListAsync(ct);
                foreach (var d in dates.Except(existing))
                {
                    var run = RecurringOperationRun.Create(t, d, clock.UtcNow);
                    db.RecurringOperationRuns.Add(run);
                    db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, null, AuditActions.RecurringRunCreated, nameof(RecurringOperation), t.Id.ToString(),
                        null, $"{t.Amount:0.00}", $"Автоматически по расписанию: {t.Name} за {d:dd.MM.yyyy}", null));
                    created++;
                }
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.DiscardChanges(); // другой экземпляр приложения создал те же черновики — следующий проход продолжит
            }
        }

        return created;
    }

    /// <summary>Сколько черновиков ждут проведения — для колокольчика (права проверяет вызывающий).</summary>
    internal static Task<int> PendingCountAsync(IKnitErpDbContext db, long org, CancellationToken ct) =>
        db.RecurringOperationRuns.AsNoTracking().CountAsync(r => r.OrganizationId == org && r.Status == RecurringRunStatus.Pending, ct);

    internal static bool CanEdit(AccessContext ctx) =>
        ctx.Permissions.Has(Permissions.PriceView) && (ctx.Permissions.Has(Permissions.SalesEdit) || ctx.Permissions.Has(Permissions.PurchaseEdit));

    private async Task ValidateAsync(AccessContext ctx, RecurringOperationData d, CancellationToken ct)
    {
        _ = await db.LegalEntityAccounts.AsNoTracking().Where(a => a.Id == d.AccountId && a.OrganizationId == ctx.OrganizationId && !a.IsArchived)
                .Select(a => (long?)a.Id).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("Счёт или касса");
        if (d.CashFlowItemId is { } itemId)
        {
            var item = await db.CashFlowItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Статья движения денег");
            if (item.IsArchived || item.Direction != (d.Kind == MoneyOperationKind.Income ? CashFlowDirection.In : CashFlowDirection.Out)
                || item.SystemCode is CashFlowItem.CustomerPayments or CashFlowItem.SupplierPayments or CashFlowItem.AccountableIssue or CashFlowItem.AccountableReturn)
            {
                throw new BusinessRuleException("recurring.item",
                    $"Статья «{item.Name}» не подходит: нужна действующая статья {(d.Kind == MoneyOperationKind.Income ? "поступлений" : "выплат")}, не оплаты и не подотчёт.");
            }
        }
    }

    private async Task<AccessContext> DemandEditAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.PriceView, ct);
        if (!CanEdit(ctx))
        {
            await guard.DenyAsync(ctx, Permissions.SalesEdit, ct);
        }

        return ctx;
    }

    internal static async Task<DateOnly> TodayAsync(IKnitErpDbContext db, long org, DateTime nowUtc, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.TimeZoneId).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(nowUtc, tz));
    }

    private void Audit(AccessContext ctx, string action, string entity, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entity, id.ToString(), before, after, reason,
            currentUser.CorrelationId));
}
