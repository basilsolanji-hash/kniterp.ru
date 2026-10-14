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

## Готово (этапы 2–5 и автоматизация, 12.10.2026)
- Контакты: каналы, основание ПДн, согласие на рассылки, удаление ПДн (и в переписке), выгрузка (D610–D616, D677).
- Активности, лента, следующий шаг, мои задачи, напоминания; КП → заказ → производство; письма из карточек (D620–D626).
- Отчёты, план/факт, блок главного экрана (D630–D632). API заявок, звонков и сообщений, импорт, вебхуки (D640–D647).
- Мессенджеры Telegram, WhatsApp, MAX — чаты с клиентами (D648, D649, D670–D673).
- Почта: шаблоны с переменными, HTML-письма, очередь и расписание рассылок, входящие IMAP (D674–D676). Защита — D673, D676–D678.
- Тесты: модульные (в т. ч. `CrmMessengerHooksTests`, `CrmMailRulesTests`), интеграционные `CrmTests`, `CrmFullCycleTests`, `ImapAddressTests`,
  e2e `CrmE2ETests` (5 сценариев, 1440 и 390 px).

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
6. **Точки встраивания этапов 2–5** (все помечены `D6xx (сессия CRM)`): `NotificationService` добавляет `CrmActivityService.AlertsAsync`;
   в `ApiV1` методы `IdempotencyAsync`/`TransactionAsync` стали `internal` и вызывается `api.MapCrmApi()`; `Program.cs` — `MessengerOptions`
   (`Messengers:MaxUrl`, `:TelegramUrl`, `:WhatsAppUrl`), `CrmEndpoints.MapCrm`, фоновая `CrmMailWorker`, лимит запросов для `/hooks`;
   Infrastructure DI — клиенты мессенджеров (без журнала HttpClient) и `ImapMailboxClient`; `CounterpartyCardPage` — вкладка CRM (D616).
7. **`IEmailSender`** получил метод `SendAsync(EmailMessage)` с реализацией по умолчанию (текст): HTML, Reply-To и List-Unsubscribe — только у
   `SmtpEmailSender`. Старый метод не менялся; другие модули могут перейти на новый по желанию.
8. Отображение названий этапов воронки, источников и причин — как данные организации, без перевода (как этапы заказов D75).

## Ответ на проверку 12.10.2026 (D121, `docs/sessions/base.md`) — закрыто 13.10.2026
1. Цены КП в ленте без права цен — скрыты; активность КП без суммы (D680, тест `CrmReviewTests`).
2. Составные внешние ключи — миграция `CrmCompositeKeys` (D682): активности → КП, КП → вид цен, рассылки → тег, получатели → рассылка
   и контакт, строки КП и сделки → номенклатура; в строки сделки, КП и получателей добавлен `OrganizationId`. Плюс
   `fk_sales_orders_crm_deal` (составной) на `SalesOrder.CrmDealId`. **Базе:** тест `ModuleExtensionTests.Confirming_sales_order…`
   переведён с вымышленной сделки 77 на настоящую — ключ не даёт ссылаться на несуществующую сделку.
3. Рассылка — очередь вне транзакции (D675, тест `Scheduled_mailing…`).
4. Длина активности КП — проверяется до отправки (D680); лимит КП письмом — 30 в час (D680).
5. Повторная заявка по API — 202 без Id и номера (D681); длительность звонка 0…86 400 с (D681).
6. Заголовок секрета MAX обязателен — не ослаблен (тест `CrmMessengerHooksTests`).
7. «Заказ подтверждён» — через `ISalesOrderEvents` (`CrmSalesOrderEvents`); прямой вызов CRM из `SalesService.ConfirmOrderAsync` удалён
   (D683). **Базе:** при слиянии не возвращайте строку `Crm.CrmDealService.OnOrderConfirmedAsync(...)` в `SalesService`.
8. **Базе (D693):** `SecurityHeaders` — `Permissions-Policy: microphone=(self)` вместо `microphone=()`: диктовка заметок CRM через Web Speech API
   браузера. Камера и геолокация не тронуты. Если база против — скажите, кнопка диктовки просто не появится.

## Готово 13.10.2026 (вечер): доработки перед пилотом (D696–D699) и ответ на D132
- КП в PDF (D696), импорт из CSV amoCRM/Битрикс24 (D697), конфликт правки называет, кто и когда изменил (D698), удобство карточки
  сделки, списков, чатов, «Моего дня», сводки и настроек (D699). Диктовка — по D132 п. 5: выключена, включает Владелец (миграция
  `CrmVoiceDictationSetting`, тест `CrmVoiceSettingTests`).
- **Замечание base.md о составных FK на справочники и позиции** закрыто ещё миграцией `CrmCompositeKeys`: одноколоночными остались только
  ссылки на `users` (как во всей базе — у пользователя нет `OrganizationId`) и на родителя у дочерних таблиц без `OrganizationId`
  (`crm_contact_channels`, `crm_deal_participants`).
- **Базе — точечные правки общего кода (помечены D696/D698/D699):**
  1. `Application/Common/Exceptions.cs`: `ConcurrencyConflictException(string? objectName = null, string? changedBy = null,
     DateTime? changedAtLocal = null)` — необязательные поля; старые вызовы без аргументов работают как раньше. `UiErrors` выводит
     «{объект}: изменил(а) {кто} {когда}», когда поля заданы.
  2. `Infrastructure/Documents/SalesPdfRenderer.cs` — `partial`; КП — в `CrmOfferPdf.cs`. DI: один singleton `SalesPdfRenderer` отдаётся как
     `ISalesPdfRenderer` и `ICrmOfferPdfRenderer` (при слиянии с D725/D729 — оставить обе строки).
  3. `SalesOrderCard.razor`: у заголовка «Оплаты» — `id="payments"` (ссылка «Внести оплату» из сделки).
  4. `MainLayout.razor`: в группе CRM «Мой день» — первым пунктом.
- Свободные номера CRM остаются в пропусках диапазона (D617–D619, D627–D629, D633–D639, D650–D659, D663, D668–D669, D689) — нового диапазона не нужно.

## Готово 14.10.2026: требования владельца 13.10 (п. 2) и ТЗ интерфейса (раздел 0 п. 5) для CRM
- Справочники CRM — максимум типовых значений (D650, миграция `CrmReferenceDefaults` дозаполняет существующие организации без дублей);
  новые виды активностей (D651); «+ Свой вариант» в списках источника и причины (D652, компонент `CrmRefSelect`). Тесты
  `CrmReferenceDefaultsTests`, e2e — своя причина проигрыша в `Smart_paste_offer_print_and_new_screens`.
- Пункты 6–7 ТЗ интерфейса на списках CRM — строка открывает объект, фильтры при возврате, пустые списки с «Создать первую…» (D653).
- **Сессии интерфейса (`docs/ui/lookups.md`):** списки CRM «Источник», «Причина» — со «своим вариантом»; «Канал продаж» и «Проект» в
  сделке — справочники базы, «свой вариант» для них — за базой или общим компонентом. Этапы воронки — в «Настройках CRM».
- **Ждут общих механизмов базы:** задачи CRM в двухэтапном «выполнено → принято» (требования 13.10, п. 1), события CRM в
  «Настройках уведомлений» и плитка «CRM» на странице `/settings` (ТЗ сессии № 1, этап 2б) — подключу, как только появится общая точка.

## Следующие шаги (решение владельца)
- Шаблоны сообщений WhatsApp (вне 24-часового окна) и рассылки в мессенджерах — по согласию, как письма.
- IP-телефония с записями разговоров — проект решения `docs/tz/crm-telephony.md` (выбор провайдера — владелец); ИИ-подсказка «следующее
  лучшее действие» (D116).
