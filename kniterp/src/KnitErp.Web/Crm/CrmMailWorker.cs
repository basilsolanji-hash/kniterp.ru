using KnitErp.Application.Common;
using KnitErp.Application.Crm;

namespace KnitErp.Web.Crm;

/// <summary>
/// Фоновая почта CRM (D675, D676): раз в 30 секунд — запланированные рассылки и очередь писем (до 100 за проход), входящие ящики — когда
/// подошло время их проверки (раз в 3 минуты). Свой контекст базы на каждый проход; ошибка одного прохода не останавливает следующий.
/// </summary>
public sealed class CrmMailWorker(IServiceScopeFactory scopes, ILogger<CrmMailWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sp = scope.ServiceProvider;
                await using (var db = sp.GetRequiredService<IKnitErpDbContextFactory>().Create())
                {
                    var sent = await CrmMailingService.ProcessQueueAsync(db, sp.GetRequiredService<IClock>(), sp.GetRequiredService<IEmailSender>(), 100, stoppingToken);
                    if (sent > 0)
                    {
                        log.LogInformation("Рассылки CRM: отправлено писем {Count}.", sent);
                    }
                }

                await using (var db = sp.GetRequiredService<IKnitErpDbContextFactory>().Create())
                {
                    var received = await CrmMailboxService.PollDueAsync(db, sp.GetRequiredService<IClock>(), sp.GetRequiredService<ICrmMailboxClient>(), stoppingToken);
                    if (received > 0)
                    {
                        log.LogInformation("Входящая почта CRM: новых писем {Count}.", received);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Почта CRM: ошибка прохода, повтор через 30 секунд.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
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
