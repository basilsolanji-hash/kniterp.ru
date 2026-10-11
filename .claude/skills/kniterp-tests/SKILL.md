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

## Проверка слияния веток (до настоящего слияния)
1. `git worktree add --detach <scratch>/trial <своя-ветка>`, затем в ней `git merge --no-commit --no-ff origin/<другая-ветка>`.
2. `python3 .claude/skills/kniterp-tests/resolve-merge.py` — типовые конфликты: значения TaggedEntity, ветки ObjectDirectory, CHECK «1–29», снапшот.
3. Сборка, `has-pending-model-changes`, все тесты, включая e2e обеих веток (`tests/e2e` на Node и `tests/KnitErp.E2ETests` на .NET, если она есть).
4. Проверить порядок миграций по времени: миграция, которая выполняется позже, не должна сужать ограничение, расширенное другой.
5. `git worktree remove --force <scratch>/trial`; в чужие ветки не пушить.

## Ревью безопасности модуля
Встроенный `/security-review` сравнивает с `origin/HEAD`, которого в облачном клоне нет. Поэтому ревью делает агент по диффу ветки:
`git diff origin/<базовая-ветка>...HEAD -- kniterp/src ':!**/Migrations/*.Designer.cs' ':!**/KnitErpDbContextModelSnapshot.cs'`.
Что проверять: фильтр по `OrganizationId` в каждом запросе по id (чужое → NotFound); область подразделений (`StructureScope`) и уровень
«С» (только своё); ПДн и суммы заработка в DTO, печати, Excel, XML и **журнале аудита**; блокировка и RowVersion при проведении
и проверке штата; неизменяемость (триггеры и `HasTrigger`); `MarkupString` и формулы в Excel. Каждая находка — тест и запись в D2xx.

## Переводы
- Каждая строка интерфейса пишется как `L("…")`. Сообщения `BusinessRuleException` и `NotFoundException` из Domain и Application тоже попадают в словари.
- Тест падает как на отсутствующих, так и на лишних ключах в `src/KnitErp.Web/Localization/{be,kk,uz}.json`.
- Названия, которые выводятся как `L(переменная)` (справочники в домене), регистрируются в `TranslationCoverageTests.UsedKeys`.
- Новые ключи добавляются в конец файла, существующий порядок не меняется: так меньше конфликтов с другими сессиями.
  Скрипт: `python3 .claude/skills/kniterp-tests/translations.py add new.json` (формат `{"ключ": ["be", "kk", "uz"]}`)
  и `... remove "ключ"` — для ключей, которые тест назвал лишними.

## Частые ошибки
- `InputNumber` не поддерживает `byte`: рендер падает, а кнопки страницы перестают работать.
- `step="0.25"` при `min="0.01"` делает значение «1» недопустимым, и браузер не отправляет форму.
- После отказа сценария у вкладки Blazor остаётся изменённая сущность — вызывать `db.DiscardChanges()`.
  Для «создать при первом сохранении» (настройки, ПДн): создать объект, применить проверки, и **только после успеха** `db.X.Add(obj)`;
  при исключении — `DiscardChanges()`. Иначе следующее сохранение той же вкладки вставит вторую запись (нарушение UNIQUE).
  Тест-регрессия: сначала неверное значение (ожидаем BusinessRuleException), затем верное тем же сервисом — должно сохраниться.

## Ссылки на файлы в Blazor
Ссылка на выгрузку (xlsx, xml, pdf) — с `download data-enhance-nav="false"`, иначе улучшенная навигация Blazor перехватывает её и файл
не скачивается. Не давайте ссылку на файл, которого может не быть (404 скачивается как пустой «.txt»): показывайте только существующие
варианты. В e2e проверяйте скачивание через `page.waitForEvent('download')` и имя файла.
