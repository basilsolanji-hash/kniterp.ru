using KnitErp.Application.Hr;

namespace KnitErp.Web.Api;

/// <summary>Раз в час сверяет статус «В отпуске» с приказами Т-6 (D210).</summary>
public sealed class HrStatusWorker(IServiceScopeFactory scopes, ILogger<HrStatusWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var changed = await scope.ServiceProvider.GetRequiredService<LeaveStatusSync>().RunAsync(stoppingToken);
                if (changed > 0)
                {
                    log.LogInformation("Статусы отпусков по приказам обновлены: {Count}.", changed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Сверка статусов отпусков: ошибка, повтор через час.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
