namespace KnitErp.Web.Components.Pages;

/// <summary>
/// Очередь обращений к сервисам разработки внутри одной вкладки (D718): у вкладки Blazor один DbContext, а карточка модели и её вкладки
/// (палитра, детали, размеры, образцы, операции) грузятся независимо — быстрое переключение вкладок запускало бы два запроса сразу
/// и обрывало сеанс. Вызовы не вкладываются друг в друга: каждый компонент оборачивает только свой вызов сервиса.
/// </summary>
public sealed class DevGate : IDisposable
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

    public async Task Run(Func<Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
