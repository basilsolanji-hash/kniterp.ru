# API knitERP v1 (D88)

Собственный HTTP API для интернет-магазина, 1С, маркетплейсов и своих программ. Машинное описание — OpenAPI 3:
`https://<ваш-адрес>/api/v1/openapi.json` (открыто без токена, данных не содержит).

Все примеры ниже — с вымышленным токеном `knt_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX` и адресом `https://erp.example.ru`.

## Токен
- Выпускает Владелец или Администратор: «Настройки → Интеграции (API)». Секрет показывается **один раз**; в базе хранится только SHA-256.
- Срок — 30, 90, 180 дней или год (не больше 366 дней). Отзыв действует сразу.
- Права токена выбираются из прав выпускающего. Права запроса = права токена ∩ **текущие** права создателя: понизили роль создателю — токен потерял лишнее. Управление пользователями, выдача прав, реквизиты организации, открытие закрытого периода и персональные данные через API не выдаются.
- Организация берётся из токена; параметра «организация» в API нет. Чужие записи → 404.
- Cookie сессии браузера в API не действуют.

```bash
curl -H "Authorization: Bearer knt_XXXX…" https://erp.example.ru/api/v1/me
```

## Ответы и ошибки
JSON, имена полей — camelCase, перечисления — строками, даты — `YYYY-MM-DD`, время — UTC ISO 8601. Ошибки — `application/problem+json`:

| Код | `code` | Когда |
|---|---|---|
| 400 | `format`, `idempotency.key`, `idempotency.key_required` | Неразборчивый JSON или параметр; слишком длинный Idempotency-Key; нет Idempotency-Key у запроса, создающего документ или оплату |
| 401 | `auth.required` | Нет токена, отозван, истёк |
| 403 | `auth.forbidden` | Нет права у токена или создателя |
| 404 | `not_found` | Нет записи (в т. ч. чужой организации) |
| 409 | `concurrency` | Запись изменили — перечитайте и повторите с новым `rowVersion` |
| 422 | код правила, например `field.required`; `idempotency.mismatch` | Нарушено бизнес-правило |
| 429 | `rate_limit` | Больше 100 запросов за 5 секунд на токен; ждите `Retry-After` секунд |

Списки номенклатуры, контрагентов, заказов и складских документов принимают `tagId` — отбор по тегу (D104). Списки: `?offset=0&limit=100` (по умолчанию 100, не больше 500), ответ `{ "items": [...], "offset": 0, "limit": 100, "nextOffset": 100 }`; `nextOffset` = null — страниц больше нет. Номенклатура (`/api/v1/items`, `/api/v1/catalog/items`) листается в базе: доступны все позиции, а не только первые 2000 (исправлено сессией № 4, D313).

## Повтор без дублей — Idempotency-Key
Каждый изменяющий запрос (POST) может нести заголовок `Idempotency-Key` (до 100 символов, например UUID). Запросы, создающие документы и деньги, — `POST /sales-orders`, `/purchase-orders`, `/stock-documents`, `/customer-payments` — без ключа отклоняются: 400 `idempotency.key_required` (D129). Повтор с тем же ключом и тем же телом в течение суток не выполнит действие второй раз — вернётся сохранённый ответ с заголовком `Idempotent-Replayed: true`. Тот же ключ с другим телом → 422 `idempotency.mismatch`. Ключ и результат записываются в одной транзакции с действием.

```bash
curl -X POST https://erp.example.ru/api/v1/customer-payments \
  -H "Authorization: Bearer knt_XXXX…" -H "Content-Type: application/json" \
  -H "Idempotency-Key: 7f1c2a9e-0d3b-4c55-9a51-2b7e8c1d0f42" \
  -d '{"date":"2026-10-11","customerId":12,"orderId":345,"amount":15000.00,"documentNumber":"п/п 118"}'
```

## Методы

| Метод | Путь | Право |
|---|---|---|
| GET | `/api/v1/me` | — |
| GET | `/api/v1/items?search=&type=` | просмотр справочников |
| GET, POST | `/api/v1/counterparties` | просмотр / изменение контрагентов |
| GET | `/api/v1/warehouses`, `/api/v1/stock?warehouseId=&type=&search=` | просмотр склада |
| GET | `/api/v1/sales-orders?status=&from=&to=&search=`, `/api/v1/sales-orders/{id}` | просмотр продаж |
| POST | `/api/v1/sales-orders` | создание заказов покупателя |
| POST | `/api/v1/sales-orders/{id}/confirm` | подтверждение |
| GET, POST | `/api/v1/customer-payments` | просмотр / ввод оплат |
| GET | `/api/v1/purchase-orders`, `/api/v1/purchase-orders/{id}` | просмотр закупок |
| GET | `/api/v1/money/balances?asOf=` | просмотр денег |
| GET | `/api/v1/reports/cash-flow?from=&to=&legalEntityId=` | просмотр денег |
| GET | `/api/v1/webhook-events` | — |
| POST | `/api/v1/purchase-orders`, `/api/v1/purchase-orders/{id}/confirm` | создание / подтверждение заказов поставщику |
| GET | `/api/v1/stock-documents?kind=&status=&from=&to=&warehouseId=&search=`, `/api/v1/stock-documents/{id}` | просмотр складских документов (в области складов) |
| GET | `/api/v1/stock-documents/options?kind=Receipt` | склады, контрагенты и причины для нового документа |
| POST | `/api/v1/stock-documents` | создание складских документов; `post: true` — ещё и проведение |
| POST | `/api/v1/stock-documents/{id}/post`, `/api/v1/stock-documents/{id}/reverse` | проведение и сторно |
| GET, POST | `/api/v1/batches?itemId=&search=&withBalance=true` | партии (цвет, крашение), новая партия |
| GET | `/api/v1/batches/balances?warehouseId=&search=` | остатки с разбивкой по партиям |
| GET | `/api/v1/tags`, `/api/v1/tags/{id}/objects` | теги и отмеченные объекты (в пределах прав) |
| GET | `/api/v1/exchange/enterprise-data?from=&to=&legalEntityId=` | выгрузка для 1С (XML EnterpriseData 1.8): номенклатура, отчёты склада, цены |
| POST | `/api/v1/tags/{id}/objects` | поставить тег: `{"entity":"StockDocument","entityId":15}` |

Новый заказ со строками и подтверждением — одной транзакцией (ошибка в строке — заказа нет):

```bash
curl -X POST https://erp.example.ru/api/v1/sales-orders \
  -H "Authorization: Bearer knt_XXXX…" -H "Content-Type: application/json" \
  -H "Idempotency-Key: shop-order-100231" \
  -d '{"orderDate":"2026-10-11","customerId":12,"warehouseId":1,"pricesIncludeVat":true,
       "customerReference":"Заказ сайта 100231",
       "lines":[{"itemId":40,"quantity":10,"price":1250.00,"vatPercent":20}],
       "confirm":true}'
```

Поступление по заказу поставщику с разбивкой по партиям и проведением — одной транзакцией (виды: `Receipt`, `WriteOff`, `Transfer`, `ReturnToSupplier`, `Shipment`, `CustomerReturn`):

```bash
curl -X POST https://erp.example.ru/api/v1/stock-documents \
  -H "Authorization: Bearer knt_XXXX…" -H "Content-Type: application/json" \
  -H "Idempotency-Key: 1c-receipt-000457" \
  -d '{"kind":"Receipt","warehouseId":1,"counterpartyId":12,"reasonId":3,"documentDate":"2026-10-11","purchaseOrderId":7,
       "lines":[{"itemId":40,"quantity":20,"batches":[{"batchId":5,"quantity":20,"packages":8,"grossWeightKg":20.8}]}],
       "post":true}'
```

Подтвердить ранее созданный заказ: `POST /api/v1/sales-orders/{id}/confirm` с телом `{"rowVersion":"<из последнего ответа>"}`.

## Вебхуки
Подписка — «Настройки → Интеграции (API) → Вебхуки»: адрес (только HTTPS, не внутренняя сеть и не localhost — проверяется и после разрешения DNS), список событий. Секрет подписи `whsec_…` показывается один раз.

События: `sales_order.created`, `sales_order.confirmed`, `sales_order.cancelled`, `purchase_order.created`, `purchase_order.confirmed`, `stock_document.posted` (в том числе инвентаризация), `stock_document.reversed`, `stock_batch.created`, `customer_payment.created`, `supplier_payment.created`, `money_operation.created`, `expense_report.approved`, тестовое `ping`.

Запрос knitERP — `POST`, тело:

```json
{ "id": "…", "type": "customer_payment.created", "sequence": 9123, "occurredAt": "2026-10-11T08:15:02.120Z",
  "data": { "entity": "CustomerPayment", "id": "88", "summary": "…" } }
```

Заголовки: `X-KnitErp-Event`, `X-KnitErp-Delivery` (один и тот же при повторах — по нему отбрасывайте дубли), `X-KnitErp-Timestamp` (Unix-секунды), `X-KnitErp-Signature: v1=<hex HMAC-SHA256(секрет, "<timestamp>.<тело>")>`. Порядок восстанавливайте по `sequence`. Подробности события запрашивайте через API по `data.id`.

D128: `data.summary` — краткая подпись без сумм: у денежных событий (`sales_order.confirmed`, `purchase_order.confirmed`, `customer_payment.created`, `supplier_payment.created`, `money_operation.created`, `expense_report.approved`) — только номер документа. Суммы берите через API токеном с правом «Цены и суммы». Создать, включить или выключить подписку можно только после повторного подтверждения паролем и только на события модулей, которые видит сам пользователь (продажи, закупки, склад — отчёты склада, CRM; оплаты и денежные операции — ещё «Цены и суммы»). Если создатель подписки заблокирован или лишился этих прав, события не отправляются: доставка получает статус «не доставлено» с причиной.

Проверка подписи (Python):

```python
import hmac, hashlib, time
def valid(secret: str, ts: str, body: bytes, header: str) -> bool:
    if abs(time.time() - int(ts)) > 300:
        return False
    mac = hmac.new(secret.encode(), ts.encode() + b"." + body, hashlib.sha256).hexdigest()
    return hmac.compare_digest("v1=" + mac, header)
```

Ответ 2xx за 10 секунд — доставлено. Иначе повтор через 1 мин, 5 мин, 15 мин, 1 ч, 3 ч, 6 ч, 12 ч; после 8 попыток — «не доставлено» (видно в журнале доставок). Перенаправления не выполняются. Выключенная и снова включённая подписка начинает с текущего момента — пропущенное не досылается.

## Настройки сервера
- `Webhooks__Disabled=true` — не запускать отправку вебхуков на этом экземпляре.
- `Webhooks__AllowPrivateNetworks=true` — только для тестового стенда; на рабочем сервере не включать.

## Каталог для обмена (D307, сессия № 4)

| Метод | Путь | Право |
|---|---|---|
| GET | `/api/v1/catalog/items?search=&type=&kind=&groupId=&tagId=&includeArchived=` | просмотр справочников |
| GET | `/api/v1/items/{id}/availability` | просмотр справочников |
| GET | `/api/v1/items/{id}/packs` | просмотр справочников |
| GET | `/api/v1/barcodes/{code}` | просмотр справочников |

`catalog/items` — те же поля, что `items`, плюс:
- `kind` — вид позиции: `Goods`, `Service`, `Bundle`;
- `paymentSubject` — признак предмета расчёта: `GOOD`, `EXCISABLE_GOOD`, `SERVICE`;
- `isMarked`, `trackingType` — маркировка, коды как в МойСклад: `NOT_TRACKED`, `LP_CLOTHES`, `LP_LINENS`, `SHOES`, `OTP`;
- `gtin` — GTIN-14;
- `minPrice` — минимальная цена, только с правом «Цены и суммы»;
- `packs` — упаковки `[{ unitId, unit, factor, barcode }]`;
- `components` — состав комплекта `[{ itemId, code, quantity }]`, у других видов — null.

`availability`:
- у товара `stocked = true`, `total` = остаток − резерв подтверждённых заказов по складам, которые видит токен;
- у комплекта `stocked = false`, `total` — сколько наборов можно собрать из компонентов;
- у услуги `stocked = false`, `total = 0`.

`barcodes/{code}` — позиция по штрихкоду позиции или упаковки и `quantity` в основных единицах (у упаковки — её коэффициент).
Если штрихкод не найден — 404.

Право «Цены: переоценка» (`catalog.price.edit`) через API не выдаётся.

## Фид каталога YML (D314, сессия № 4)
Не API по токену, а файл для площадок: `GET /feeds/kf_<ключ>.yml` без заголовка Authorization. Ссылку выдаёт «Настройки → Фиды для площадок»
(один раз). Ответ — `application/xml` в формате YML Яндекса; кэш 5 минут; не больше 30 запросов в минуту с адреса; неизвестный или выключенный
ключ — 404.
