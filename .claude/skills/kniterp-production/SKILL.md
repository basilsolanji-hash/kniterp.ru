---
name: kniterp-production
description: Модуль «Производство» knitERP — производственные заказы, этапы, материалы, выпуск, техкарты, планирование, терминал цеха. Где код, какие правила держать (блокировка заказа, проведение через складские документы, только вставка), как тестировать (.NET e2e Playwright), как добавлять переводы и с какими модулями есть общие контракты. Использовать перед любой правкой в kniterp/src/**/Production* и при слиянии веток с производством.
---

# Модуль «Производство» knitERP

Общий порядок сборки и тестов — скилл `kniterp-tests` (ветка «Сотрудники»; после слияния он будет и здесь). Этот скилл — только про производство.

## Где что лежит
- Домен: `src/KnitErp.Domain/Production/` — `ProductionOrder` (статусы, снимок норм), `ProductionStages.cs` (этапы, `ProductionFlow` — сколько можно отметить),
  `ProductionMaterials.cs` (баланс материалов), `TechCard.cs` (`BillOfMaterials` — многоуровневая спецификация), `ShopFloorCode.cs` (код операции для терминала).
- Сценарии: `src/KnitErp.Application/Production/` — `ProductionOrderService`, `ProductionFloorService` (этапы; `ProductionLocks`), `ProductionMaterialService`
  (выдача, возврат, отходы, расход, выпуск, сторно; `ProductionPosting`), `ShopFloorService` (терминал цеха), `ProductionWorkOutputSource` (выработка для «Сотрудников»),
  `MaterialPlanningService`, `ProductionPrintService`, `ProductionReportService`, `TechCardService`.
- Конфигурации EF — только `src/KnitErp.Infrastructure/Persistence/ProductionConfigurations.cs`.
- Страницы: `src/KnitErp.Web/Components/Pages/Production*`, `Shop*` (терминал), `PrintProductionDocumentPage`, `PrintRouteSheetPage`, `MaterialPlanningPage`.
- Решения: `docs/decisions.md`, D90–D99 (новые — дополнениями к ним, пока заказчик не выдаст новый диапазон).

## Правила, которые нельзя ломать
- Всё, что меняет поток заказа (отметки, выдача, выпуск, закрытие), идёт в транзакции под `ProductionLocks.LockOrderAsync` — одновременные действия не превысят поток.
- Материалы двигаются только складскими документами (`ProductionPosting.PostAsync` → `StockDocumentService`), связь — `production_order_documents`.
  Документ, связанный с производством, сторнируется только из карточки заказа (`ReverseDocumentAsync`), склад его не сторнирует.
- `production_operations`, `production_order_materials`, `production_order_documents` — только вставка (триггеры в миграциях; в модели — `HasTrigger`).
  Исправление — новая запись «Исправление» с отрицательным количеством.
- Запущенный заказ: изделие, количество, техкарта и снимок норм защищены триггером `tr_production_orders_fixed_after_release`.
- Цены и себестоимость показывать только с `Permissions.PriceView`; на терминале цеха цен нет.
- Новый сервис, который используют виджеты на чужих страницах, — через `IKnitErpDbContextFactory` (как `SalesProductionService`), иначе два компонента делят один DbContext.

## Тесты
- Интеграционные: `tests/KnitErp.IntegrationTests/ProductionOrderTests.cs` (общий `SetUpAsync`, `AddWorkerAsync` — рабочий с карточкой и выдачей права).
  У `TestClock` время 09.10.2026 05:00 UTC — выдачи прав в тестах датировать `host.Clock.UtcNow`, а не `DateTime.UtcNow`, иначе право «ещё не действует».
  Тест-класс делит одну базу между своими тестами: фильтровать данные по своей организации.
- Сквозные (.NET Playwright): `tests/KnitErp.E2ETests` — `AppFixture` поднимает приложение по HTTPS с временным сертификатом, сидит организацию,
  начальника цеха, кладовщика и вязальщицу (`WorkerEmail`). Телефон — `ViewportSize 390×844, IsMobile, HasTouch` и проверка `scrollWidth <= clientWidth`.
  Кнопки до готовности Blazor — `ClickUntilAsync`. Падение — снимок в `KNITERP_E2E_ARTIFACTS`.
  Запуск: `dotnet build` всего решения, затем `dotnet test tests/KnitErp.E2ETests --no-build` (нужен `KNITERP_TEST_SQL`).

## Переводы
Ключи интерфейса — в `src/KnitErp.Web/Localization/{be,kk,uz}.json`, новые — в конец файла: `python3 -I .claude/skills/kniterp-production/append_tr.py <файл с T = {ключ: (be, kk, uz)}> kniterp/src/KnitErp.Web/Localization`.
Списки из домена, которые выводятся как `L(переменная)` (этапы, причины невыполнения), регистрируются в `TranslationCoverageTests.UsedKeys`.

## Общие контракты с другими модулями (сверять при слиянии)
- `TaggedEntity`: 1–10 общие, 11–19 производство (`ProductionOrder = 11`), 20–29 кадры; CHECK тегов и обсуждений — `BETWEEN 1 AND 29` (D202).
- `IWorkOutputSource` (`Application/Hr/WorkOutput.cs`) — файл побайтно одинаковый в обеих ветках; производство регистрирует `ProductionWorkOutputSource`
  через `AddScoped`, кадры — заглушку через `TryAddScoped`: при любом порядке строк побеждает производство. Идентификатор операции — номер этапа 1–5.
- `ItemType` 10 и 11 заняты каталогом (сессия 4) — не использовать.
- В ветку `claude/fervent-volta-3pn88r` не пушить; перед новой миграцией — `git merge origin/claude/fervent-volta-3pn88r`.
