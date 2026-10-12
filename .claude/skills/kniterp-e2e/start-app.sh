#!/usr/bin/env bash
# Поднимает knitERP (Development, https://localhost:5443) на отдельной базе и печатает ссылку приглашения Владельца.
# Нужны: SQL Server на localhost:1433 и пароль sa в KNITERP_SA_PASSWORD (одноразовый контейнер, не секрет рабочей среды).
#   bash .claude/skills/kniterp-e2e/start-app.sh [имя_базы]
# Лог — $E2E_DIR/app.log, ссылка приглашения — $E2E_DIR/invite.txt.
set -euo pipefail
root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/kniterp"
db="${1:-kniterp_e2e_$(date +%s)}"
E2E_DIR="${E2E_DIR:-/tmp/kniterp-e2e}"
mkdir -p "$E2E_DIR"
# [K] — чтобы шаблон не совпал с командной строкой вызывающей оболочки (иначе pkill убивает и её).
pkill -f "[K]nitErp\.Web" 2>/dev/null || true
# Ждём выхода прежнего экземпляра, иначе новый не займёт порт 5443.
for i in $(seq 1 30); do pgrep -f "[K]nitErp\.Web" >/dev/null || break; sleep 0.5; done
# …и освобождения порта (экземпляр, запущенный параллельно, мог занять его позже).
for i in $(seq 1 30); do (exec 3<>/dev/tcp/127.0.0.1/5443) 2>/dev/null || break; pkill -f "[K]nitErp\.Web" 2>/dev/null || true; sleep 0.5; done
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__KnitErp="Server=localhost,1433;Database=$db;User Id=sa;Password=${KNITERP_SA_PASSWORD:?задайте KNITERP_SA_PASSWORD};TrustServerCertificate=true;Encrypt=false"
dotnet build "$root/src/KnitErp.Web" -v q -nologo >/dev/null
nohup dotnet run --no-build --project "$root/src/KnitErp.Web" --urls "https://localhost:5443" >"$E2E_DIR/app.log" 2>&1 &
# Ссылка приглашения пишется при старте раньше, чем Kestrel слушает порт: ждём «Now listening» (и ссылку, если база новая).
for i in $(seq 1 180); do
  if grep -q "Now listening" "$E2E_DIR/app.log" 2>/dev/null; then break; fi
  if grep -qE "Unhandled exception|fail: " "$E2E_DIR/app.log" 2>/dev/null; then echo "сбой запуска — см. $E2E_DIR/app.log" >&2; exit 1; fi
  sleep 1
done
token="$(grep -o 'account/invite?token=[^ ]*' "$E2E_DIR/app.log" | head -1 | sed 's/.*token=//')"
echo "https://localhost:5443/account/invite?token=$token" > "$E2E_DIR/invite.txt"
echo "база: $db"; echo "приглашение: $(cat "$E2E_DIR/invite.txt")"
