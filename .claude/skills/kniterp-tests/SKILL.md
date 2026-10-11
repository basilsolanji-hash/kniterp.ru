---
name: kniterp-tests
description: Как собрать knitERP и запустить его тесты — модульные, интеграционные (SQL Server) и сквозные Playwright, — как разрешать конфликт снапшота миграций и как работать со словарями переводов be/kk/uz. Использовать перед коммитом изменений в kniterp/ и при падении тестов.
---

# Тесты knitERP

Рабочая папка — `kniterp/`. Окружение готовит `.claude/hooks/session-start.sh`: .NET 10, dev-сертификат HTTPS, SQL Server в Docker
(`KNITERP_TEST_SQL`), пакеты e2e. Если переменной `KNITERP_TEST_SQL` нет, интеграционные тесты молча пропускаются (Skipped) — это не «зелёный» результат.

## Порядок проверки перед коммитом
1. `dotnet build KnitErp.slnx` — предупреждения считаются ошибками (TreatWarningsAsErrors).
2. `dotnet test tests/KnitErp.UnitTests` — сюда входит `TranslationCoverageTests`.
3. `dotnet test tests/KnitErp.IntegrationTests` — около 2 минут, каждый тест-класс получает свою базу. Убедиться, что Skipped = 0.
4. Если менялась модель: `dotnet ef migrations has-pending-model-changes -p src/KnitErp.Infrastructure` → «No changes».
5. Если менялся интерфейс: `dotnet build src/KnitErp.Web`, затем `cd tests/e2e && npx playwright test` (около минуты; приложение
   поднимается на новой базе `kniterp_e2e_*`, вход Владельца с 2FA выполняется автоматически).

Узкий прогон: `dotnet test tests/KnitErp.IntegrationTests --filter "FullyQualifiedName~Hr"`.

## Миграции
- Создание: `dotnet ef migrations add <Имя> -p src/KnitErp.Infrastructure -o Persistence/Migrations`. Модуль «Сотрудники» — префикс `Hr…`.
- Применённые миграции не редактируются. Если миграция ещё не запушена: `dotnet ef migrations remove -p src/KnitErp.Infrastructure --force`.
- Таблица с триггером объявляется в модели через `t.HasTrigger("...")`, иначе EF использует OUTPUT и вставка падает.
- Конфликт в `KnitErpDbContextModelSnapshot.cs` при слиянии: принять обе стороны и проверить шаг 4.

## Переводы
- Каждая строка интерфейса пишется как `L("…")`. Сообщения `BusinessRuleException` и `NotFoundException` из Domain и Application тоже попадают в словари.
- Тест падает как на отсутствующих, так и на лишних ключах в `src/KnitErp.Web/Localization/{be,kk,uz}.json`.
- Названия, которые выводятся как `L(переменная)` (справочники в домене), регистрируются в `TranslationCoverageTests.UsedKeys`.
- Новые ключи добавляются в конец файла, существующий порядок не меняется: так меньше конфликтов с другими сессиями.

## Частые ошибки
- `InputNumber` не поддерживает `byte`: рендер падает, а кнопки страницы перестают работать.
- `step="0.25"` при `min="0.01"` делает значение «1» недопустимым, и браузер не отправляет форму.
- После отказа сценария у вкладки Blazor остаётся изменённая сущность — вызывать `db.DiscardChanges()`.
