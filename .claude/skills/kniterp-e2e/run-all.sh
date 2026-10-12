#!/usr/bin/env bash
# Регрессия: все сценарии scenarios/cat-*.mjs по очереди на уже запущенном приложении (start-app.sh).
# Итог — таблица «сценарий — ок/ошибка — время», подробности — $E2E_DIR/regress/<сценарий>.log. Код выхода 1, если что-то упало.
set -u
: "${KNITERP_SA_PASSWORD:?задайте KNITERP_SA_PASSWORD — часть сценариев готовит данные через sqlcmd}"
dir="$(cd "$(dirname "$0")" && pwd)"
out="${E2E_DIR:?задайте E2E_DIR}/regress"
mkdir -p "$out"
fail=0
for s in "$dir"/scenarios/cat-*.mjs; do
  name="$(basename "$s" .mjs)"
  start=$(date +%s)
  if timeout 600 node "$s" >"$out/$name.log" 2>&1; then res="ок"; else res="ОШИБКА"; fail=1; fi
  printf '%-34s %-7s %4ss\n' "$name" "$res" "$(( $(date +%s) - start ))"
done
exit $fail
