# Сессия «Карточка контрагента и интерфейс» — `claude/fervent-gates-248buc`

Решения — D500–D599 (D500–D503 на 11.10.2026). Ветка основана на `claude/hr-employees` (производство + «Сотрудники») и слита с базой `claude/fervent-volta-3pn88r` (D109–D114).

## Что сделано
- D500: карточка контрагента `/counterparties/{id}` — реквизиты, долги и акт сверки, договоры со сканами, история заказов и документов, обсуждение.
  Новые таблицы `counterparty_contracts`, `counterparty_contract_files` (только вставка), миграция `CounterpartyContracts`.
  Новые необязательные отборы в чужих фильтрах (в конце записи): `SalesOrderFilter.CustomerId`, `PurchaseOrderFilter.SupplierId`,
  `CustomerInvoiceFilter.CustomerId`, `StockDocumentListFilter.CounterpartyId`.
- D501: меню на телефоне не прокручивает страницу (CSS в `App.razor`, блок `max-width: 899px`; проверено вместе с D114).
- D502: кнопки языков по алфавиту (`UiLanguages.Menu`).
- D503: поддомены — отложены допущением.

## Проверки перед передачей (11.10.2026, после слияния базы)
Модульные 416/416, интеграционные 203/203 (Skipped 0), e2e Node 18 + 1 намеренно пропущен, .NET e2e 11/11, `has-pending-model-changes` — чисто.

## Для слияния
- Порядок миграций по времени: `CounterpartyContracts` (15:46) стоит между `ProductionOperationCorrects` и базовыми `AddSalesStageRules`/`AddCustomRoles`; ограничений не сужает.
- Конфликты при слиянии базы: словари переводов (скрипт `resolve-merge.py`), `SqlTestHost.cs` (обе стороны), `App.razor` (D501 + D114, обе стороны).
- Сценарии `tests/e2e/tests/counterparty-card.spec.ts` по D403 перенести в `tests/KnitErp.E2ETests` после общего слияния.

## Следующее в этой сессии
Договор в заказе покупателя и поставщику (необязательное поле, по умолчанию основной действующий договор контрагента и юрлица заказа), реестр «Договоры» в меню CRM.
