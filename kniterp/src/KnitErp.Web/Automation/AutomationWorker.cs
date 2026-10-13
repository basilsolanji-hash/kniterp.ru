using KnitErp.Application.Automation;
using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Application.Sales;

namespace KnitErp.Web.Automation;

/// <summary>
/// D145: автоматизация волны 2 — раз в 15 минут: черновики регулярных операций (D147), автозакрытие заказов (D150), напоминания об оплате
/// (D148, письма уходят обычной очередью продаж). Каждый шаг — своим контекстом; ошибка одного шага не мешает другим. В журнал —
/// только числа. Отключается ключом Automation:WorkerDisabled (тестовые стенды); в организации — «Настройками автоматизации».
/// </summary>
public sealed class AutomationWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<AutomationWorker> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await Step("регулярные операции", (db, clock, _) => RecurringOperationService.GenerateAsync(db, clock, stoppingToken), "Регулярные операции: черновиков {Count}.");
            await Step("автозакрытие заказов", (db, clock, _) => OrderAutoClose.RunAsync(db, clock, stoppingToken), "Автозакрытие: закрыто заказов {Count}.");
            await Step("напоминания об оплате", (db, clock, mail) => PaymentReminders.QueueAsync(db, clock, mail.PublicUrl ?? config["App:PublicUrl"], stoppingToken),
                "Напоминания об оплате: в очереди писем {Count}.");
        }
        while (await WaitAsync(timer, stoppingToken));

        async Task Step(string name, Func<IKnitErpDbContext, IClock, IEmailSender, Task<int>> run, string message)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sp = scope.ServiceProvider;
                await using var db = sp.GetRequiredService<IKnitErpDbContextFactory>().Create();
                var count = await run(db, sp.GetRequiredService<IClock>(), sp.GetRequiredService<IEmailSender>());
                if (count > 0)
                {
                    log.LogInformation(message, count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Автоматизация ({Step}): ошибка прохода, повтор через 15 минут.", name);
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
