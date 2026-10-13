namespace KnitErp.Web.Components.Shared;

/// <summary>
/// D820: загрузка тяжёлой страницы во вкладке (главный экран) и переход на другую страницу не идут одновременно. У вкладки Blazor один
/// DbContext, а загрузка прежней страницы при переходе не прерывается — новая страница начинала свой запрос параллельно, и вкладка
/// падала («страница перестала отвечать»). Страница загружает данные через <see cref="Run{T}"/>, а переход внутри программы
/// (MainLayout, NavigationLock) ждёт <see cref="WaitIdleAsync"/> — не дольше 10 секунд. Как <c>DevGate</c> (D718), но для всей вкладки.
/// </summary>
public sealed class TabLoadGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> Run<T>(Func<Task<T>> action)
    {
        await _gate.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Дождаться конца идущей загрузки (если есть). Не дождались за отведённое время — переход идёт как раньше.</summary>
    public async Task WaitIdleAsync(TimeSpan timeout)
    {
        if (await _gate.WaitAsync(timeout))
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
