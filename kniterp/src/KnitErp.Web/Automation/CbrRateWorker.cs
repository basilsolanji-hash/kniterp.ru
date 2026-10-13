using KnitErp.Application.Common;
using KnitErp.Application.Finance;

namespace KnitErp.Web.Automation;

/// <summary>
/// D146: ежедневная загрузка официальных курсов ЦБ РФ (XML_daily.asp). Раз в 3 часа: курс на завтра (ЦБ публикует его днём) и на сегодня;
/// загруженная дата не скачивается повторно. Нет сети — предупреждение в журнал, в организации курс вводится вручную. Отключается
/// ключом Automation:CbrRates:Enabled = false; адрес — Automation:CbrRates:Url.
/// </summary>
public sealed class CbrRateWorker(IServiceScopeFactory scopes, IHttpClientFactory http, IConfiguration config, ILogger<CbrRateWorker> log) : BackgroundService
{
    public const string DefaultUrl = "https://www.cbr.ru/scripts/XML_daily.asp";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(3));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sp = scope.ServiceProvider;
                var clock = sp.GetRequiredService<IClock>();
                var moscow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, "Europe/Moscow"));
                foreach (var date in new[] { moscow, moscow.AddDays(1) })
                {
                    await using var db = sp.GetRequiredService<IKnitErpDbContextFactory>().Create();
                    var client = http.CreateClient(nameof(CbrRateWorker));
                    var url = $"{config["Automation:CbrRates:Url"] ?? DefaultUrl}?date_req={date:dd'/'MM'/'yyyy}";
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > CbrRateImporter.MaxBytes)
                    {
                        throw new InvalidOperationException("Ответ ЦБ больше 1 МБ.");
                    }

                    var content = await response.Content.ReadAsByteArrayAsync(stoppingToken);
                    var (rateDate, changed) = await new CbrRateImporter(db, clock).ImportAsync(content, stoppingToken);
                    if (changed > 0)
                    {
                        log.LogInformation("Курсы ЦБ на {Date:dd.MM.yyyy}: загружено {Count}.", rateDate, changed);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning("Курсы ЦБ не загружены ({Error}) — повтор через 3 часа; при необходимости курс вводится вручную.", ex.Message);
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
