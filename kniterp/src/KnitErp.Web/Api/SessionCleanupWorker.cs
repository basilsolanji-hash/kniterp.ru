using KnitErp.Application.Authentication;

namespace KnitErp.Web.Api;

/// <summary>D126: раз в сутки удаляет старые записи сессий (<see cref="SessionCleanup"/>).</summary>
public sealed class SessionCleanupWorker(IServiceScopeFactory scopes, ILogger<SessionCleanupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var removed = await scope.ServiceProvider.GetRequiredService<SessionCleanup>().RunAsync(stoppingToken);
                if (removed > 0)
                {
                    log.LogInformation("Удалены старые записи сессий: {Count}.", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Очистка старых сессий: ошибка, повтор через сутки.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
