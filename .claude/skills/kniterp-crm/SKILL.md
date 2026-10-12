---
name: kniterp-crm
description: Модуль CRM knitERP — лиды, сделки, контакты, активности и лента, КП → заказ → производство, отчёты и план, рассылки и шаблоны писем, входящая почта IMAP, чаты Telegram/WhatsApp/MAX, импорт и API. Где код, какие правила держать (видимость по ответственному, ПДн и согласия, секреты вебхуков, очередь рассылок, свой контекст базы у панелей), как тестировать и переводить. Использовать перед любой правкой в kniterp/src/**/Crm* и при слиянии веток с CRM.
---

# Модуль CRM knitERP

Общий порядок сборки, тестов, слияния и переводов — скиллы `kniterp-tests`, `kniterp-translations`, `kniterp-e2e`, сдача этапа — `kniterp-stage`.
Этот скилл — только про CRM. Решения — `docs/decisions.md`, D600–D699; заметки и вопросы к базе — `docs/sessions/crm.md`;
автоматизация — `docs/tz/crm-automation.md`.

## Где что лежит
- Домен `src/KnitErp.Domain/Crm/`: `CrmLead`, `CrmDeal` (+ история этапов), `CrmContact` (каналы, согласия, `ErasedName`), `CrmActivity` (+ `CrmOffer`),
  `CrmPipeline`, `CrmAutomation.cs` (настройки, план, рассылка и получатели, внешние ссылки), `CrmMessenger.cs` (каналы мессенджеров),
  `CrmMail.cs` (шаблоны, `CrmEmailHtml`, `CrmMailbox`), `CrmText` (нормализация и маски), `CrmTextParser` (умная вставка).
- Сценарии `src/KnitErp.Application/Crm/`: `CrmAccess` (видимость), `CrmEvents` (автоматизация этапов), сервисы `*Service`,
  `CrmMessengerHooks` (разбор и подпись вебхуков), `CrmMailboxService` (IMAP), `CrmMailingService.ProcessQueueAsync` (очередь рассылок).
- Infrastructure: `Persistence/CrmConfigurations.cs` (все таблицы, CHECK, триггеры только-вставки), `External/MessengerClients.cs`,
  `Email/ImapMailboxClient.cs` (проверка внутренних адресов), `Email/SmtpEmailSender.cs` (HTML, Reply-To, List-Unsubscribe).
- Web: страницы `Components/Pages/Crm*`, `PrintCrmOfferPage`, `Account/CrmUnsubscribe.razor`; панели со своим контекстом базы
  `Components/Shared/Crm*Panel.razor`, `CrmActivities.razor`; `Crm/CrmEndpoints.cs` (Excel, вебхуки `/hooks/crm/{мессенджер}/{секрет}`),
  `Crm/CrmMailWorker.cs` (очередь рассылок и IMAP), `Api/ApiCrm.cs` (API v1), стили — `CrmStyles.razor`.

## Правила, которые нельзя ломать
- Каждый метод сервиса начинает с `guard.DemandAsync(...)`; без `crm.all` — только видимое через `CrmAccess.Leads/Deals/Contacts`; чужое — 404.
- Вход без пользователя (вебхуки, воркер, IMAP) работает с правами подключившего/поставившего в очередь (`IntegrationUser`/`SystemUser` +
  `AccessGuard`): отозвали права — обработка останавливается, а не идёт «от системы».
- Секреты: токены, пароль ящика, App secret — `PersonalDataColumns.Text` (шифрование); секрет вебхука — только SHA-256; сравнение —
  `CryptographicOperations.FixedTimeEquals`; HttpClient мессенджеров — `.RemoveAllLoggers()` (токен Telegram в адресе).
- Сначала проверка реквизитов, потом `db.Add` — иначе неудачная попытка оставит недописанную строку в контексте (D678).
- Собеседник в чатах — «Мессенджер:id»; входящее всегда с `ExternalRef` (по нему отличаются ответы).
- Рассылки: только согласие (38-ФЗ), отписка POST-кнопкой; отправка — только очередью `ProcessQueueAsync`, попытка отмечается до отправки.
- ПДн: в журнал — маски `CrmText.MaskPhone/MaskEmail`; удаление по запросу обезличивает и переписку (D677). Реальных данных в тестах нет:
  телефоны `+7 900 000-00-xx`, почты `@example.org`.
- История этапов, КП и строки КП — только вставка (триггеры в миграциях, `HasTrigger` в модели).
- Панели в карточках и настройках создают свой `IKnitErpDbContext` через `IKnitErpDbContextFactory` на каждое действие; таймеры
  (автообновление чатов) пропускают такт, пока идёт действие.
- Тексты писем пользователя в HTML — только через `CrmEmailHtml.FromText` (экранирование), никаких `MarkupString` из данных.

## Тесты
- Модульные: `CrmRulesTests`, `CrmMessengerHooksTests`, `CrmMailRulesTests`.
- Интеграционные (SQL Server): `CrmTests`, `CrmFullCycleTests` (фейки `CrmTestKit.Max/Telegram/WhatsApp/Imap`, `host.Mail.Messages`),
  `ImapAddressTests`. Фильтр: `--filter "FullyQualifiedName~Crm"`.
- e2e: `CrmE2ETests` (1440 и 390 px, светлая/тёмная, 0 ошибок JS, скриншоты `Shot(...)` в `KNITERP_E2E_ARTIFACTS`).
  `GetByRole(AriaRole.Status)` на страницах с предупреждением о почте — фильтровать по тексту (их два).

## Переводы
Новые строки `L("…")` и сообщения `BusinessRuleException` — be/kk/uz через `kniterp-translations`. Подсказки переменных шаблонов
(`CrmEmailTemplate.Variables`) учтены в `TranslationCoverageTests`. Названия этапов, источников и шаблонов — данные организации, не переводятся.
