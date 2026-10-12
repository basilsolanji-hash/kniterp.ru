---
name: kniterp-tests
description: Как собрать knitERP и запустить его тесты — модульные, интеграционные (SQL Server) и сквозные .NET Playwright, — как поднять SQL Server после простоя, разрешать конфликты слияния веток (снапшот, словари, меню) и проверять безопасность. Использовать перед коммитом изменений в kniterp/ и при падении тестов. Переводы — skill kniterp-translations, порядок сдачи этапа — kniterp-stage.
---

# Тесты knitERP

Рабочая папка — `kniterp/`. Окружение готовит `.claude/hooks/session-start.sh`: .NET 10, dev-сертификат HTTPS, SQL Server в Docker
(`KNITERP_TEST_SQL`), пакеты e2e. Если переменной `KNITERP_TEST_SQL` нет, интеграционные тесты молча пропускаются (Skipped) — это не «зелёный» результат.
Тесты падают с «A network-related or instance-specific error» (среда простаивала, docker остановился) — `bash .claude/skills/kniterp-tests/ensure-sql.sh`.

## Порядок проверки перед коммитом
1. `dotnet build KnitErp.slnx` — предупреждения считаются ошибками (TreatWarningsAsErrors).
2. `dotnet test tests/KnitErp.UnitTests` — сюда входит `TranslationCoverageTests`.
3. `dotnet test tests/KnitErp.IntegrationTests` — около 2 минут, каждый тест-класс получает свою базу. Убедиться, что Skipped = 0.
4. Если менялась модель: `dotnet ef migrations has-pending-model-changes -p src/KnitErp.Infrastructure` → «No changes».
5. Если менялся интерфейс: `dotnet test tests/KnitErp.E2ETests` (единый .NET-набор кадров и производства, около 2 минут; каждая
   фикстура поднимает приложение на своей тестовой базе, Владелец кадров входит с 2FA сам). Только кадры: `--filter "FullyQualifiedName~HrE2ETests"`.
   Снимки упавших сценариев — в `KNITERP_E2E_ARTIFACTS`. Новый сценарий кадров — метод в `HrE2ETests` (помощники `StructureAsync`,
   `StaffAsync`, `HireOrderAsync`, `OpenAsync`), запуск приложения — общий `WebAppProcess`.

Узкий прогон: `dotnet test tests/KnitErp.IntegrationTests --filter "FullyQualifiedName~Hr"`.

## Миграции
- Создание: `dotnet ef migrations add <Имя> -p src/KnitErp.Infrastructure -o Persistence/Migrations`. Модуль «Сотрудники» — префикс `Hr…`.
- Применённые миграции не редактируются. Если миграция ещё не запушена: `dotnet ef migrations remove -p src/KnitErp.Infrastructure --force`.
- Таблица с триггером объявляется в модели через `t.HasTrigger("...")`, иначе EF использует OUTPUT и вставка падает.
- Конфликт в `KnitErpDbContextModelSnapshot.cs` при слиянии: принять обе стороны и проверить шаг 4.

## Проверка слияния веток (до настоящего слияния)
1. `git worktree add --detach <scratch>/trial <своя-ветка>`, затем в ней `git merge --no-commit --no-ff origin/<другая-ветка>`.
2. `python3 .claude/skills/kniterp-tests/resolve-merge.py` — типовые конфликты: значения TaggedEntity, ветки ObjectDirectory, CHECK «1–29», снапшот.
3. Сборка, `has-pending-model-changes`, все тесты, включая e2e (`tests/KnitErp.E2ETests`; набор на Node `tests/e2e` удалён 11.10.2026 — если слияние его вернуло, удалите).
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
  Как добавлять и удалять ключи — skill `kniterp-translations` (`missing.sh` → JSON → `merge.py`); свой скрипт здесь больше не ведётся.

## Частые ошибки
- Пакетное действие или автоматика в долгоживущем контексте вкладки Blazor (D225: «все табели», обновление табелей после приказа) читает
  статус документа **без отслеживания** (`AsNoTracking`): отслеживаемый запрос вернёт старый экземпляр из контекста, и документ, закрытый
  другим пользователем, будет считаться открытым. Тест: закрыть документ через второй `host.Hr(...)`, затем действие первым.
- `InputNumber` не поддерживает `byte`: рендер падает, а кнопки страницы перестают работать.
- `step="0.25"` при `min="0.01"` делает значение «1» недопустимым, и браузер не отправляет форму.
- После отказа сценария у вкладки Blazor остаётся изменённая сущность — вызывать `db.DiscardChanges()`.
  Для «создать при первом сохранении» (настройки, ПДн): создать объект, применить проверки, и **только после успеха** `db.X.Add(obj)`;
  при исключении — `DiscardChanges()`. Иначе следующее сохранение той же вкладки вставит вторую запись (нарушение UNIQUE).
  Тест-регрессия: сначала неверное значение (ожидаем BusinessRuleException), затем верное тем же сервисом — должно сохраниться.

## Ссылки на файлы в Blazor
Ссылка на выгрузку (xlsx, xml, pdf) — с `download data-enhance-nav="false"`, иначе улучшенная навигация Blazor перехватывает её и файл
не скачивается. Не давайте ссылку на файл, которого может не быть (404 скачивается как пустой «.txt»): показывайте только существующие
варианты. В e2e проверяйте скачивание через `page.RunAndWaitForDownloadAsync` и имя файла.

## Деньги, зависящие от других документов
Если расчёт читает другие утверждённые документы (перерасчёт заработка за прошлые месяцы, D219), блокировка `db.LockAsync` должна быть
общей на организацию и браться в транзакции и при расчёте, и при утверждении — иначе два утверждения подряд или одновременно оплатят одно
и то же дважды. Тест: два черновика с одной доплатой, утвердить первый — второй отказывает «пересчитайте». Часы теста (`host.Clock`)
стоят на месте, поэтому сравнения времени «после расчёта» пишите как `>=`.

## Подсказки от сессии «Карточка контрагента» (D500–D502)
- Номера решений: D500–D599 — эта сессия (карточка контрагента, интерфейс). Не занимать.
- Раздел «история» в чужой карточке строить через list-метод модуля с новым необязательным отбором в конце записи-фильтра
  (`SalesOrderFilter.CustomerId`, `PurchaseOrderFilter.SupplierId`, `CustomerInvoiceFilter.CustomerId`, `StockDocumentListFilter.CounterpartyId`),
  а не своим запросом: так сохраняются права, скрытие сумм (D91) и область складов. Вызывать только при наличии права —
  `guard.DenyAsync` пишет «отказ» в журнал аудита, лишний вызов засоряет журнал.
- Загрузка файлов: `FileSignatures.Matches` (Domain/Catalog/CounterpartyContract.cs) — тип по сигнатуре, хранение через `PersonalDataColumns.Bytes`
  (шифрование мастер-ключом), таблица только на вставку (триггер + `HasTrigger`), отдача `Results.File(..., fileName)` вложением.
- Телефонный e2e, которому нужна кнопка «Меню»: `test.skip((page.viewportSize()?.width ?? 0) > 899, ...)` — проект desktop запускает и тесты с `@phone`.
- Вложенные Playwright-селекторы: `getByRole('button', { name: 'Да', exact: true })` — иначе совпадёт с «Продажи», «Задачи».
