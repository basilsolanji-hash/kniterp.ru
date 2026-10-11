using KnitErp.Application.Exchange.MoySklad;

namespace KnitErp.Web.Exchange;

/// <summary>
/// Фоновая загрузка из МойСклад (D310): при старте переводит загрузки, оборванные остановкой процесса, в «прервана»; затем раз в 5 секунд
/// берёт из очереди следующую. Каждая загрузка — в своей области (свой DbContext), ошибки шага пишутся в лог и не останавливают процесс.
/// </summary>
public sealed class MoySkladImportWorker(IServiceScopeFactory scopes, ILogger<MoySkladImportWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var more = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var import = scope.ServiceProvider.GetRequiredService<MoySkladImportService>();
                if (!recovered)
                {
                    var count = await import.RecoverInterruptedAsync(stoppingToken);
                    if (count > 0)
                    {
                        log.LogWarning("Загрузка МойСклад: {Count} оборванных загрузок помечены прерванными.", count);
                    }

                    recovered = true;
                }

                more = await import.ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Загрузка МойСклад: ошибка шага, повтор через 5 секунд.");
            }

            if (more)
            {
                continue;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
