# Модуль «Сотрудники» — что важно другим сессиям

Ветка `claude/hr-employees`, решения D200–D213, миграции `HrPersonalData`, `HrOrders`, `HrTimesheets`, `HrPayroll`.
Слияние с веткой производства проверено пробно: сборка, модель, все тесты обеих веток зелёные.

## Всем сессиям
1. **Теги и обсуждения: диапазоны `TaggedEntity`.** 1–10 — общие объекты, 11–19 — производство, 20–29 — «Сотрудники», дальше по договорённости.
   CHECK `ck_tag_links_entity` и `ck_discussion_comments_target` теперь `BETWEEN 1 AND 29`. Значение внутри своего диапазона миграции не требует.
   **Не сужайте ограничение** своей миграцией: миграция, выполненная позже, затрёт чужие значения (так чуть не сломались теги производства).
2. **Слияние.** Скилл `kniterp-tests` и скрипт `.claude/skills/kniterp-tests/resolve-merge.py` разрешают типовые конфликты: значения
   TaggedEntity, ветки ObjectDirectory, строки DbSet, права, метки аудита, меню, снапшот модели. После — `has-pending-model-changes`.
3. **Окружение облачной сессии** готовит `.claude/hooks/session-start.sh` (.NET 10, SQL Server в Docker, `KNITERP_TEST_SQL`, Playwright).
   Без `KNITERP_TEST_SQL` интеграционные тесты молча пропускаются — проверяйте, что Skipped = 0.
4. **Персональные данные.** В тексте, уходящем в журнал аудита или уведомления, используйте выдержку `DiscussionService.Excerpt`
   или `PersonalDataMask.Mask`: длинные номера (СНИЛС, паспорт, телефон) там скрываются. Карточку сотрудника в своих объектах
   показывайте только ФИО и должностью; паспорт, СНИЛС, ИНН, адрес и оклад — через `PersonalDataService` с его проверкой прав.
5. **Роли.** Добавлена системная роль «Кадровик» (`hr_officer`). Её права заданы списком `HrRoleMatrix`, а не столбцом `RoleMatrixP0`.
   Новую строку матрицы добавляйте как обычно (8 ячеек); если нужно право Кадровику — допишите его в `HrRoleMatrix`.
   Тест `Administrator_can_grant_accountant` требует, чтобы Бухгалтер не получал прав выше Администратора — поэтому заработок у него «П».
6. **Две системы e2e.** У производства — `tests/KnitErp.E2ETests` (.NET), у кадров — `tests/e2e` (Playwright на Node, workflow `kniterp-e2e.yml`).
   Обе зелёные; общий подход выбрать при слиянии и перенести сценарии в один набор.

## Сессия № 2 (производство)
- Реализуйте `KnitErp.Application.Hr.IWorkOutputSource` у себя и зарегистрируйте в DI (`services.AddScoped<IWorkOutputSource, …>()`):
  заглушка `EmptyWorkOutputSource` подключена через `TryAddScoped` и уступит вашей регистрации.
  ```csharp
  Task<IReadOnlyList<WorkOutputDto>> OutputAsync(long organizationId, DateOnly from, DateOnly to, CancellationToken ct);
  record WorkOutputDto(long EmployeeId, DateOnly Date, long OperationId, string OperationName, long? ItemId, decimal Quantity, string Unit);
  ```
  Unit — `"шт"` или `"кг"` (как в расценках). OperationId — постоянный идентификатор операции: на него ссылаются сдельные расценки
  (`hr_piece_rates.OperationId`, FK нет). Если операцию переименовать, расценки сохранятся; если удалить, выработка по ней не оплатится.
  Только своя организация, только проведённая выработка, `EmployeeId` — `employees.Id`.
- Для проверки есть интеграционный тест `HrPayrollTests` с `FakeWorkOutputSource` — по нему видно ожидаемый формат.
- Ваш `ck_*_target` «1–11» при слиянии заменяется на «1–29» (скрипт делает это сам).

## Сессия № 1 (склад, деньги, продажи, API, обмен с 1С)
- Выгрузка начислений в 1С: ЗУП — отдельный класс `PayrollEnterpriseDataExport` (EnterpriseData 1.8), `EnterpriseDataExportService` не менялся.
  Имена объектов (`Справочник.Сотрудники`, `Документ.НачисленияЗаработкаKnitERP`, виды начислений) черновые — согласуйте с правилами
  обмена ЗУП, когда будете делать загрузку.
- В API v1 кадровых данных нет намеренно: ПДн и заработок не выдаются через API (ТЗ «Сотрудники» §1). Если понадобится — отдельные
  области токена и запись в журнал на каждое чтение, как в `PersonalDataService`.
- В общие файлы добавлены только строки в конец списков: `Abstractions.cs`, `KnitErpDbContext.cs`, `DependencyInjection.cs`, `Labels.cs`,
  `Permissions.cs`, `RoleMatrixP0.cs`, `AuditEntry.cs`, `MainLayout.razor` (раздел «Кадры»), `Program.cs` (адреса `/hr/*`, фоновая задача
  `HrStatusWorker`), `TranslationCoverageTests.cs` (кадровые справочники), `DiscussionService.Excerpt` (маска ПДн).

## Сессия № 4 (каталог)
- Если товарам или услугам нужны теги и обсуждения, берите значение `TaggedEntity` по договорённости (например, 30–39) и расширьте
  CHECK до нового верхнего предела **одной** миграцией, не сужая нижние диапазоны.
- Сдельные расценки ссылаются на изделие по `ItemId` (`items.Id`, без FK). Архивирование позиции расценку не ломает; при слиянии
  позиций сообщите кадрам — расценки нужно будет перенести на новую позицию.
