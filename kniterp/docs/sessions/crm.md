# CRM (пятая сессия) — сообщения базе и другим сессиям

Ветка `claude/crm`, ТЗ `docs/tz/crm.md`, решения D600–D699 (`docs/decisions.md`, раздел «CRM»), `TaggedEntity` 30–39.
Свой код: `Domain/Crm`, `Application/Crm`, `Infrastructure/Persistence/CrmConfigurations.cs`, страницы `Components/Pages/Crm*`,
компоненты `Components/Shared/CrmDuplicates.razor`, `CrmPager.razor`, миграции `Crm…`, тесты `CrmTests`, `CrmRulesTests`, `CrmE2ETests`.

## Готово (этап 1, 11.10.2026)
- Права `crm.*`, роли «Менеджер продаж» и «Руководитель продаж» (D600).
- Воронки, этапы, источники, причины отказа; готовая воронка при первом обращении (D602, D603, D609).
- Лиды: быстрый ввод, карточка, «в работу», отказ, дубликаты, объединение, квалификация в одной транзакции (D604, D605).
- Сделки: канбан и таблица, карточка, лента этапов, история (только вставка), позиции, наблюдатели, переназначение (D606, D608).
- «Создать заказ» → заказ покупателя базы; подтверждённый заказ → сделка «Выиграна» (D607).
- Контакты (этап 1): список, карточка со сделками, архив.
- Теги и обсуждения на лидах, сделках и контактах (D601).
- Тесты: модульные `CrmRulesTests` (14), интеграционные `CrmTests` (6, SQL Server), e2e `CrmE2ETests` (3: 1440 px и 390 px, светлая и тёмная тема, 0 ошибок JS).

## Вопросы к базе (сессия № 1 читает перед слиянием)

Каждая правка чужого кода — точечная, помечена `D6xx (сессия CRM)` и покрыта тестом.

1. **ТЗ §9 п.1 — заказ из сделки.** Вместо поля `SalesOrder.CrmDealId` связь хранится в CRM (`crm_deal_orders`, D607): модуль продаж
   не меняется. Единственная правка `SalesService` — одна строка в `ConfirmOrderAsync` после `StageAutomation.ApplyAsync`:
   `await Crm.CrmDealService.OnOrderConfirmedAsync(db, ctx, id, clock.UtcNow, currentUser.CorrelationId, ct);` (тест
   `CrmTests.Lead_qualifies_into_deal_order_and_won_deal`). Если база предпочтёт событие/интерфейс (как `IProductionDashboardSource`) —
   переделаю: сообщите имя интерфейса.
2. **ТЗ §9 п.2 — теги и обсуждения.** `TaggedEntity.CrmLead = 30`, `CrmDeal = 31`, `CrmContact = 32` (`Tags.cs`), ветка в `ObjectDirectory`
   (вызывает `CrmVisibility.DescribeAsync`). CHECK `ck_tag_links_entity` и `ck_discussion_comments_target` расширены до
   `BETWEEN 1 AND 39` миграцией `CrmLeadsDeals` и в `Configurations.cs`. **Просьба:** в `.claude/skills/kniterp-tests/resolve-merge.py`
   заменить «BETWEEN 1 AND 29» на «BETWEEN 1 AND 39» — иначе скрипт при слиянии вернёт старое ограничение и снапшот разойдётся с миграцией
   (тест `CrmTests.Visibility_follows_responsible_participants_and_crm_all` упадёт на теге сделки).
   Тест «Сотрудников» `HrPayrollTests.Tag_and_discussion_constraints_reserve_ranges_for_all_modules` проверял, что 30 отклоняется;
   граница в нём сдвинута: 30 и 39 принимаются, 40 — нет (пометка D601).
3. **ТЗ §9 п.5 — меню и права.** В `MainLayout.razor` в группу «CRM» добавлены пункты «Сделки», «Лиды», «Контакты», «Настройки CRM».
   В `Permissions.cs` — 5 прав и группа «CRM» (`GroupOf("crm")`, в `Groups` после «Закупки»). В `RoleMatrixP0.cs` — 5 строк матрицы,
   роли `sales_manager`, `sales_head` в `SystemRoles.Ordered` и список `CrmRoleMatrix` (как `HrRoleMatrix`). Коды аудита `crm.*`
   в `AuditEntry.cs`. `TranslationCoverageTests` — статусы лидов, итоги сделок, виды справочников CRM.
4. **ТЗ §9 п.3 — вкладка «Контакты и сделки» в карточке контрагента** (этап 2). Предлагаю: CRM даёт компонент
   `Components/Shared/CrmCounterpartyPanel.razor` (параметр `CounterpartyId`, сам проверяет `crm.view`), карточка контрагента показывает
   вкладку, если у пользователя есть `crm.view`. Сервисы готовы: `CrmContactService.ListAsync(counterpartyId: …)`,
   `CrmDealService.ListAsync(new CrmDealFilter(CounterpartyId: …))`. Нужна точка встраивания — вкладка в `CounterpartyCardPage.razor`
   (сессия контрагентов D500).
5. **ТЗ §9 п.4 — блок «CRM» главного экрана** (этап 4): сделаю `ICrmDashboardSource` по образцу `IProductionDashboardSource`; база
   добавит `DashboardBlocks.Crm`.
6. Отображение названий этапов воронки, источников и причин — как данные организации, без перевода (как этапы заказов D75).

## Следующие этапы
- Этап 2 (D610–D619): несколько телефонов и почт контакта, основания обработки ПДн и согласие на рассылки, удаление ПДн по запросу,
  выгрузка в Excel с `crm.export`, вкладка в карточке контрагента.
- Этап 3 (D620–D629): активности, лента, напоминания, КП.
- Этап 4 (D630–D639): отчёты, план продаж, блок главного экрана.
- Этап 5 (D640–D659): заявки через API v1, импорт, вебхуки.
