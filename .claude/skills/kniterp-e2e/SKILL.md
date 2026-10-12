---
name: kniterp-e2e
description: E2E-проверка knitERP в браузере (Playwright, 1440 и 390 px, 0 ошибок JS). Использовать для проверки экранов после изменения Blazor-страниц и перед сдачей этапа — поднимает приложение на отдельной базе SQL Server, входит Владельцем с 2FA и прогоняет сценарий.
---

# E2E knitERP

## Подготовка (один раз за сессию)
- SQL Server: `docker run -d --name sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='<одноразовый пароль>' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`
  (если `docker ps` не отвечает — сначала `dockerd >/tmp/dockerd.log 2>&1 &`; после простоя среды контейнер останавливается —
  `docker start sql` и ждать `sqlcmd … -Q 'SELECT 1'`; признак — «A network-related … error» в тестах).
- .NET 10 SDK: если `dotnet` нет, а dot.net закрыт прокси — `apt-get install -y dotnet-sdk-10.0 aspnetcore-runtime-10.0`.
- Сертификат разработки: `dotnet dev-certs https` (cookie входа только по HTTPS).
- Playwright уже установлен глобально (`/opt/node22/lib/node_modules/playwright`, Chromium в `/opt/pw-browsers`); `playwright install` не запускать.

## Запуск
```bash
export KNITERP_SA_PASSWORD='<пароль sa>' E2E_DIR=<scratchpad>/e2e
bash .claude/skills/kniterp-e2e/start-app.sh kniterp_e2e1     # новая база → миграции → ссылка приглашения в $E2E_DIR/invite.txt
node .claude/skills/kniterp-e2e/scenarios/<сценарий>.mjs       # с тем же E2E_DIR
```
Первый сценарий на новой базе задаёт пароль Владельца (`owner@kniterp.local`) и подключает 2FA — ключ и пароль сохраняются в
`$E2E_DIR/owner.json`, следующие сценарии входят по ним (TOTP считает `lib.mjs`). Новая база — удалить `owner.json`.

## Сценарий
`lib.mjs`: `ownerSession(width)`, `collectErrors(page)`, `assertNoHorizontalScroll(page, label)`, `ready(page)`, `totp(key)`.
Каждый сценарий: шаги через видимые подписи (как пользователь), проверка на 1440, затем `setViewportSize(390)` и
`assertNoHorizontalScroll`, в конце — выход с кодом 1, если были ошибки JS. Примеры — `scenarios/cat-stage*.mjs`.

## Советы
- Код 2FA одноразовый: `lib.mjs` сам ждёт новое 30-секундное окно между входами (повтор кода приложение отклоняет).
- Сценарий повторяемый: коды позиций — с суффиксом `sfx` (время), отбор списка — поиском по нему.
- Суммы в ru-RU разделены неразрывным пробелом — сравнивайте через `plainText(locator)`.
- Blazor: после `goto` вызывайте `ready(page)`, иначе клик может уйти до подключения схемы.
- `InputSelect` для enum — значение опции = имя члена (`selectOption('Service')`).
- Лог приложения — `$E2E_DIR/app.log` (ошибки сервера смотреть там).

## Проверка штрихкодов декодером (этикетки, D316)
Штрихкоды этикеток проверяются настоящим декодером, а не на глаз:
1. Позиции со штрихкодами: строки `ItemId|Code` в `$E2E_DIR/bc-items.txt` (например, `SELECT ItemId, Code FROM kniterp.item_barcodes …`
   через `sqlcmd -h -1 -W -s "|"`).
2. Шаблоны с названиями `BC 40x30`, `BC 43x25`, `BC 30x20`, `BC A4 105x74`, `BC 58x40`, `BC A4 70x37` (создать из образцов в конструкторе).
3. `node .claude/skills/kniterp-e2e/barcodes/render.mjs` — каждая этикетка картинкой при 203 и 300 dpi (`deviceScaleFactor = dpi / 96`).
4. Декодер — в отдельном venv: `python3 -m venv <scratch>/bcvenv && <scratch>/bcvenv/bin/pip install zxing-cpp pillow numpy`,
   затем `<scratch>/bcvenv/bin/python -I .claude/skills/kniterp-e2e/barcodes/decode.py` — итог по шаблону, dpi и типу кода (серый и 1 бит).
Результат 12.10.2026: EAN-13 — 100 %; длинный Code 128 на 30×20 и 43×25 при 203 dpi не читается — экран предупреждает (`BarcodeFit`).
