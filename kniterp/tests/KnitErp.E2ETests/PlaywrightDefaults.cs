using System.Runtime.CompilerServices;
using Microsoft.Playwright;

namespace KnitErp.E2ETests;

/// <summary>
/// Общие ожидания сквозных тестов. По умолчанию Playwright ждёт проверку (Expect) 5 секунд — на загруженной машине и в CI
/// этого не хватает: при полном прогоне падали разные тесты (помощник, «Сотрудники», производство), а по отдельности проходили.
/// 15 секунд — запас на сервер, который параллельно обслуживает другие классы тестов; успешная проверка не ждёт дольше нужного.
/// </summary>
internal static class PlaywrightDefaults
{
    [ModuleInitializer]
    internal static void Init() => Assertions.SetDefaultExpectTimeout(15_000);
}
