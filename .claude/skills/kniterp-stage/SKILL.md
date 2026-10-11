---
name: kniterp-stage
description: Порядок сдачи одного этапа knitERP (ветка сессии): код → миграция → модульные, интеграционные и e2e тесты → переводы → журнал решений → коммит → пуш → отчёт по-русски. Использовать, когда этап ТЗ готов к сдаче или нужно проверить, что ничего не забыто.
---

# Сдача этапа knitERP

1. **Миграция** (если менялась модель): `cd kniterp && dotnet tool restore && dotnet ef migrations add <Префикс><Имя> -p src/KnitErp.Infrastructure -o Persistence/Migrations`.
   - Префикс — свой у сессии (каталог — `Cat`). SQL вне модели (триггеры) — `migrationBuilder.Sql("EXEC(N'…')")`, в Down — `DROP … IF EXISTS`.
   - Триггер на таблице → `t.HasTrigger("имя")` в конфигурации, иначе EF падает на OUTPUT («target table has database triggers»).
   - Пересоздать свою неприменённую миграцию: удалить её файлы, `git checkout` снапшота, снова `migrations add` (`migrations remove` хочет базу).
   - **Идемпотентный скрипт** (так обновляется рабочий сервер): `dotnet ef migrations script --idempotent -p src/KnitErp.Infrastructure -o /tmp/m.sql`,
     затем выполнить его дважды на пустой базе: `sqlcmd -C -I -b -d <база> -i /tmp/m.sql` (`-I` — QUOTED_IDENTIFIER для фильтрованных индексов).
     Любой SQL в `migrationBuilder.Sql`, который ссылается на колонки этой же миграции, — только через `EXEC(N'…')`, иначе пакет не компилируется.
2. **Сборка**: `dotnet build kniterp/KnitErp.slnx` — предупреждения = ошибки.
3. **Тесты**: `export KNITERP_TEST_SQL="Server=localhost,1433;User Id=sa;Password=<пароль>;TrustServerCertificate=true;Encrypt=false"`;
   `dotnet test kniterp/tests/KnitErp.UnitTests` и `dotnet test kniterp/tests/KnitErp.IntegrationTests` (полный прогон ~3–5 мин, запускать в фоне).
   Интеграционные классы — `IClassFixture<SqlTestHost>`, свой диапазон `_innSeed`. Права: начальные остатки создаёт Старший кладовщик, не Владелец.
   Сквозные тесты — единый .NET-набор `dotnet test kniterp/tests/KnitErp.E2ETests` (D218; Владелец с 2FA — фикстура `HrAppFixture`).
   Тесты падают с «A network-related … error» (среда простаивала) — `bash .claude/skills/kniterp-tests/ensure-sql.sh` (печатает строку для `KNITERP_TEST_SQL`).
4. **Переводы** — skill `kniterp-translations` (MISSING 0, STALE 0).
5. **E2E** — набор `tests/KnitErp.E2ETests` (шаг 3) и ручной осмотр экранов skill `kniterp-e2e`: 1440 и 390 px, 0 ошибок JS.
6. **Журнал решений** `kniterp/docs/decisions.md`: только свой диапазон номеров; правки чужих сервисов — пометка `// Dxxx (сессия N)` в коде и в коммите;
   то, что больше точечной правки, — запрос владельцу в `kniterp/docs/tz/*-requests.md`.
7. **Коммит и пуш** в ветку сессии: `git push -u origin <ветка>` (повтор при сетевой ошибке 2/4/8/16 с). Чужие ветки не сливать, PR не создавать.
8. **Отчёт** по-русски: что сделано, тесты (числа), решения, запросы; разделы «Готовность проекта» и «Следующие 3 шага».
