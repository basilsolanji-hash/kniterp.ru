---
name: kniterp-development
description: Модуль «Разработка» knitERP (D700–D799) — модели, стадии, задание, файлы, станки и программы, техзадание дессинатора (заправка нитеводителей), детали, размеры, образцы цеха, технологические операции, автоматизация. Где код, какие правила держать, как тестировать и снимать скриншоты экранов. Использовать перед любой правкой в kniterp/src/**/Development*, страниц Dev*.razor и при слиянии ветки разработки с базой.
---

# Модуль «Разработка» knitERP

Общая сборка и тесты — skill `kniterp-tests`, порядок сдачи — `kniterp-stage`, переводы — `kniterp-translations`. Здесь — только разработка.
ТЗ — `kniterp/docs/tz/development.md`, автоматизация — `docs/tz/development-automation.md`, сообщения сессиям — `docs/sessions/development.md`.

## Где что лежит
- Домен: `src/KnitErp.Domain/Development/` — `DevModel` (карточка, задание, стадии), `DevStages` (переходы, D703), `DevFile` (файлы, сигнатуры),
  `DevLookup` (справочники), `DevAutomation` (настройки, шаблоны, правила D790–D798), `DevRoleMatrix` (роли отдела), `DevTech*` (станки,
  детали, заправка, размеры, образцы, операции).
- Сценарии: `src/KnitErp.Application/Development/` — `DevModelService`, `DevSettingsService`, `DevExchangeService` (Excel),
  `DevNotifications` (колокольчик), `DevTechService` (техзадание, станки, программы), `DevShopService` (образцы цеха).
- EF: только `src/KnitErp.Infrastructure/Persistence/DevConfigurations.cs`. Миграции — префикс `Dev…`.
- Страницы: `src/KnitErp.Web/Components/Pages/Dev*.razor`, общие стили — `DevStyles.razor`, техкарта вязания — `DevTechCardPage.razor` (`/dev/tech/{detailId}`).
- Файлы и Excel наружу — `Program.cs` (`/dev/files/{id}`, `/dev/models.xlsx`, `/dev/models-template.xlsx`).

## Правила, которые нельзя ломать
- Права: `dev.view` смотреть; `dev.edit` модель, задание, детали, размеры, операции; `dev.pattern` рисунок, заправка, программы;
  `dev.sample` факт образца в цеху; `dev.approve` «Утверждено» и уход с него; `dev.cost.view` суммы (цены операций, целевая себестоимость);
  `dev.settings` справочники, станки, стадии, автоматизация. Каждая проверка — в сервисе (`guard.DemandAsync`), кнопка — не защита.
- Чужая организация → `NotFoundException`. Внешние ключи на объекты организации — составные `(OrganizationId, Id)`.
- Только вставка (триггер + `HasTrigger`): история стадий, файлы, журнал станка. Справочники и станки — архив, не удаление.
- Сценарий упал после изменения отслеживаемой сущности — `db.DiscardChanges()` (у вкладки Blazor один DbContext).
- Суммы в журнал аудита не пишем — только «задана/изменена».
- CHECK тегов и обсуждений не трогать: база держит 1…99 (D118). `TaggedEntity` модуля — 40–49.
- Миграцию создавать только после `git merge origin/claude/fervent-volta-3pn88r` (иначе на сервере она применится позже чужой).
  Слияние прошло перемоткой (база уже влила ветку) — свои миграции НЕ пересоздавать: они уже в базе.
  ГЛАВНОЕ: миграция, отправленная (push) в ветку, считается применённой — база вливает ветку в любой момент. Дальше — только новая
  корректирующая миграция; пересоздавать и удалять отправленную нельзя (12.10 так чуть не задвоилась `DevCosting`). Проверка перед
  правкой миграции: `git ls-tree origin/claude/fervent-volta-3pn88r <путь к Migrations> | grep <имя>`.
- Превью картинок (`/dev/files/{id}?inline=1`) журнал не пишет; скачивание — пишет (D799).

- Несколько компонентов страницы ходят в базу только через `DevGate.Run(...)` (одна очередь на вкладку, D718) — иначе
  «A second operation was started» и обрыв сеанса при быстрых кликах. Новый блок/вкладка — `@inject DevGate Gate`.
- Экран без пустых полей (D719): факты — `dl.dev-facts` с `div` на пару, панели — `.dev-panels` (широкая — `.panel.wide`),
  списки — `ul.dev-tiles`, матрицы размеров — `table.dev-matrix`. Проверять скриншотом 1440 px.
- Этикетка — `<DevLabelLink Kind="model|detail|sample" Id="…" />`; печать — `PrintDevLabelPage`, данные — `DevTechService.LabelAsync`.
- Настраиваемое — в `DevSettings` (допуск факта, разделы показателя, этикетки) и справочниках; новые «магические числа» не зашивать.

- Таблицы, сохраняемые целиком (размеры, табель мер, упаковка, замеры), — только изменённые ячейки `DevCellEdit(Row, Column, Field, Was,
  Value)`, ответ `DevSaveResult` с конфликтами (D720); не вызывать `Gate.Run` внутри другого `Gate.Run` — семафор не реентерабельный.
- Передача в производство — `DevHandoffService` (сервисы каталога и техкарт внутри транзакции), списание пряжи образца — `DevYarnService`
  (черновик склада); нормы — чистая функция `DevNorms` (юнит-тесты в `DevTechRulesTests`).

- Себестоимость — `DevCostService` (итоги считает сервер, экран шлёт только `DevCostScenario`), формула — `DevCosting` (юнит-тесты),
  нормы пряжи — общий `DevYarnNorms` (и для передачи). Суммы в журнал аудита не писать.

- Утверждение — `DevApprovalRules.Ensure` (расчёт обязателен, порог маржи → комментарий к переходу); аналитика — `DevAnalyticsService`
  (он же `IDevelopmentDashboardSource`); замечания с подстановками — `DevNote` + шаблон в `DevNotes.All`, на экране `DevFormat.Note(w)`.

- Новая версия модели — `DevTechService.NewVersionAsync` (только от «Передано в производство», одна следующая); новые сущности с
  техданными добавлять и в копирование версии. Причина доработки — `ReasonId` образца (справочник «Причины доработки»).

- Ссылки модели на чужие модули (заказчик, коллекция, сделка CRM) — проверка организации в `ValidateAsync` и составной FK
  `(OrganizationId, X)`; новое поле-ссылку без того и другого не добавлять (аудит 12.10, D771). Файлы: не в «Передано» и не в
  «Отменено», предел — `DevFile.EnsureQuota` под блокировкой загрузки. Журнал аудита — `kniterp/docs/audit/development-2026-10-12.md`.

- Образец у заказчика (D723) — `DevSample.HandToCustomer` / `CloseCustody`, сервис `DevShopService.HandSampleAsync`,
  `CloseSampleCustodyAsync`, список `SamplesOut` (аналитика, колокольчик, главный экран). Закрыть передачу можно и у переданной/отменённой модели.

- Рисунки (D726–D729) — `DevPatternService` (библиотека, карточка, цвета, колориты, файлы версий, статусы, копия, привязка к модели,
  очередь программиста `ProgrammerQueue`), страницы `/dev/patterns`, `/dev/patterns/{id}`, блок `DevModelPatternBlock`; версии и
  файлы рисунка — только вставка; утверждённый рисунок — только новой версией. Сетка — `DevPatternGrid` (PNG/BMP/CSV). Фото —
  `IImageShrinker` (SkiaSharp) до 2000 px.

## Частые ошибки
- Карточка, открытая тем же компонентом с другим `Id` («Создать похожую»), сохраняет поля прошлой модели — сбрасывать состояние
  в `OnParametersSetAsync` при смене `Id`.
- `L(переменная)` тест переводов не видит — названия домена регистрировать в `TranslationCoverageTests` (блок «Разработка»).
- `Assert.Equal([..], ...)` с выражением коллекции не компилируется — `new[] { ... }`.
- Телефон 390 px: таблицы — внутри `.scroll`, вкладки — `.dev-tabs` (перенос), колонки доски — одна под другой.

## Тесты
- Приёмка ТЗ §14 — `DevE2ETests.Acceptance_model_from_idea_to_production`: вся цепочка через интерфейс до позиции каталога и
  черновика техкарты. Пряжу заводит `HrAppFixture.CreateYarnAsync`; в списках пряжи и станков подпись длиннее названия
  («код — название») — выбирать `SelectContainingAsync`.
- Модульные: `tests/KnitErp.UnitTests/DevModelRulesTests.cs`, `DevTechRulesTests.cs`.
- Интеграционные: `DevModelTests`, `DevAutomationTests`, `DevTechTests` (свои `_innSeed` 970–973 млн).
- Сквозные: `tests/KnitErp.E2ETests/DevE2ETests.cs` (фикстура `HrAppFixture`, ошибки JS собираются в `RunAsync`).
- Запуск только модуля: `--filter "FullyQualifiedName~Dev"`.

## Скриншоты экранов для владельца
```bash
cd kniterp && source <scratchpad>/sqlenv   # KNITERP_TEST_SQL
export PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers KNITERP_SCREENSHOTS=<scratchpad>/shots
dotnet build KnitErp.slnx && dotnet test tests/KnitErp.E2ETests --no-build --filter "FullyQualifiedName~DevScreenshotTests"
```
PNG появятся в `$KNITERP_SCREENSHOTS` (1440 px и 390 px); без переменной тест ничего не делает. Отправить — `SendUserFile`.
