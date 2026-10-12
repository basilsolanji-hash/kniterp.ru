#!/usr/bin/env bash
# Регрессия: все сценарии scenarios/cat-*.mjs по очереди на уже запущенном приложении (start-app.sh).
# Итог — таблица «сценарий — ок/ошибка — время», подробности — $E2E_DIR/regress/<сценарий>.log. Код выхода 1, если что-то упало.
set -u
: "${KNITERP_SA_PASSWORD:?задайте KNITERP_SA_PASSWORD — часть сценариев готовит данные через sqlcmd}"
dir="$(cd "$(dirname "$0")" && pwd)"
out="${E2E_DIR:?задайте E2E_DIR}/regress"
mkdir -p "$out"
# Тестовые фиды и шаблоны этикеток прошлых прогонов (лимиты на организацию: 10 и 30): только в базе e2e, по названиям из сценариев.
if [ -n "${E2E_DB:-}" ]; then
  docker exec sql /opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -P "$KNITERP_SA_PASSWORD" -d "$E2E_DB" \
    -Q "DELETE FROM kniterp.catalog_feeds WHERE Name LIKE N'Маркет %' OR Name LIKE N'Host %'" >/dev/null || echo "очистка фидов не удалась"
  # Шаблоны этикеток сценария cat-step5 («Ценник XXXXX», лимит — 30 на организацию); основной шаблон не трогаем.
  docker exec sql /opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -P "$KNITERP_SA_PASSWORD" -d "$E2E_DB" \
    -Q "SET QUOTED_IDENTIFIER ON; DELETE FROM kniterp.label_templates WHERE Name LIKE N'Ценник _____' AND Name NOT LIKE N'Ценник %мм%' AND IsDefault = 0" >/dev/null || echo "очистка шаблонов этикеток не удалась"
fi
fail=0
for s in "$dir"/scenarios/cat-*.mjs; do
  name="$(basename "$s" .mjs)"
  start=$(date +%s)
  if timeout 600 node "$s" >"$out/$name.log" 2>&1; then res="ок"; else res="ОШИБКА"; fail=1; fi
  printf '%-34s %-7s %4ss\n' "$name" "$res" "$(( $(date +%s) - start ))"
done
exit $fail
