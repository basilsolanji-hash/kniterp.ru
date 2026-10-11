#!/usr/bin/env bash
# Выпуск новой версии knitERP на сервере (docs/operations.md): копия → сборка → миграции → запуск → проверка.
#   ./update.sh            — последняя версия ветки
#
# Безопасный выпуск (аудит 10.10.2026, п. 5 и 9):
# - прежний образ сохраняется как kniterp-web:previous, новый получает метку коммита и неизменяемый тег kniterp-web:<коммит>;
# - каждый выпуск пишется в $RELEASE_LOG: время, коммит, образы и digest базовых образов SQL Server и .NET;
# - если миграции или запуск не удались, а схема базы не менялась (ни одна миграция не применилась), автоматически
#   запускается прежняя версия; если схема уже изменилась — приложение остаётся остановленным (старая версия поверх новой
#   схемы опасна), приходит оповещение и печатается команда восстановления из копии, снятой перед выпуском.
set -euo pipefail
cd "$(dirname "$0")"
set -a; . ./.env; set +a
RELEASE_LOG="${RELEASE_LOG:-/var/lib/kniterp-releases/releases.log}"
mkdir -p "$(dirname "$RELEASE_LOG")"

sql() { docker compose exec -T sql /opt/mssql-tools18/bin/sqlcmd -C -b -h -1 -W -S localhost -U sa -P "$SQL_SA_PASSWORD" -d kniterp "$@"; }
applied() { sql -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM [kniterp].[__ef_migrations_history];" 2>/dev/null | tr -d '[:space:]' || echo "?"; }
image_id() { docker image inspect -f '{{.Id}}' "$1" 2>/dev/null || true; }
digest() { docker image inspect -f '{{join .RepoDigests ","}}' "$1" 2>/dev/null || echo "-"; }
alert() { ./monitor.sh notify "$1" || true; echo "$1" >&2; }
healthy() {
  for _ in $(seq 1 30); do
    curl -fsS http://127.0.0.1:8080/health 2>/dev/null | grep -q Healthy && return 0
    sleep 2
  done
  return 1
}

# Откат: схема не менялась — запускаем прежний образ; менялась — оставляем остановленным и объясняем, что делать.
rollback() {
  local reason="$1" now
  now="$(applied)"
  if [[ -n "$PREVIOUS" && "$now" == "$BEFORE" ]]; then
    docker tag "$PREVIOUS" kniterp-web:latest
    docker compose up -d --no-build app
    if healthy; then
      alert "Выпуск $COMMIT отменён ($reason). Схема базы не менялась — работает прежняя версия."
    else
      alert "Выпуск $COMMIT отменён ($reason), но прежняя версия тоже не ответила Healthy: docker compose logs app --tail 100"
    fi
  else
    docker compose stop app || true
    alert "Выпуск $COMMIT не удался ($reason), схема базы уже изменена (миграций было $BEFORE, стало $now). Приложение остановлено.
Восстановление: TARGET=kniterp ./restore.sh $BACKUP_FILE ; затем docker tag ${PREVIOUS:-<прежний образ>} kniterp-web:latest && docker compose up -d --no-build app"
  fi
  echo "$(date -u +%FT%TZ) $COMMIT FAILED: $reason (миграции $BEFORE → $now)" >> "$RELEASE_LOG"
  exit 1
}

# D109: код на сервере никто не правит руками — изменённые или лишние файлы в рабочей копии останавливают выпуск
# (признак вмешательства); новые коммиты и их авторы пишутся в журнал выпусков. VERIFY_SIGNATURES=1 в .env — выпускать
# только коммиты с проверенной подписью (git verify-commit; ключи подписи — в доверенных ключах сервера).
echo "== Проверка рабочей копии"
DIRTY="$(git -C ../.. status --porcelain --untracked-files=normal)"
if [[ -n "$DIRTY" ]]; then
  alert "Выпуск остановлен: файлы кода на сервере изменены вручную (возможное вмешательство):
$DIRTY
Посмотрите: git -C $(cd ../.. && pwd) diff ; вернуть как в репозитории: git -C $(cd ../.. && pwd) checkout -- . && git -C $(cd ../.. && pwd) clean -nd"
  exit 1
fi
OLD_HEAD="$(git -C ../.. rev-parse HEAD)"
echo "== Код"; git -C ../.. pull --ff-only
if [[ "${VERIFY_SIGNATURES:-0}" == "1" ]] && ! git -C ../.. verify-commit HEAD 2>/dev/null; then
  UNSIGNED="$(git -C ../.. rev-parse --short=12 HEAD)"
  git -C ../.. reset --hard -q "$OLD_HEAD"
  alert "Выпуск остановлен: коммит $UNSIGNED без проверенной подписи. Код возвращён на ${OLD_HEAD:0:12}."
  exit 1
fi
echo "Новые коммиты:"; git -C ../.. log --format='  %h %an %ad %s' --date=short "$OLD_HEAD..HEAD" | tee -a "$RELEASE_LOG" | head -40
COMMIT="$(git -C ../.. rev-parse --short=12 HEAD)"
echo "== Полная копия перед выпуском (точка отката)"; ./backup.sh full
BACKUP_FILE="$(ls -t "$BACKUP_DIR"/enc/kniterp-full-*.enc | head -1)"

PREVIOUS="$(image_id kniterp-web:latest)"
[[ -n "$PREVIOUS" ]] && docker tag "$PREVIOUS" kniterp-web:previous
echo "== Сборка $COMMIT"
docker compose build --build-arg "SOURCE_COMMIT=$(git -C ../.. rev-parse HEAD)" app
docker tag kniterp-web:latest "kniterp-web:$COMMIT"

BEFORE="$(applied)"
echo "== Миграции (применено до выпуска: $BEFORE)"
docker compose stop app
docker compose run --rm -e ConnectionStrings__KnitErp="Server=sql;Database=kniterp;User Id=sa;Password=$SQL_SA_PASSWORD;Encrypt=true;TrustServerCertificate=true" app migrate \
  || rollback "ошибка миграции"

echo "== Запуск"; docker compose up -d --no-build app
healthy || rollback "приложение не ответило Healthy за минуту"

echo "$(date -u +%FT%TZ) $COMMIT OK image=$(image_id kniterp-web:latest) previous=${PREVIOUS:--} migrations=$BEFORE→$(applied) sql=$(digest "$(docker compose images -q sql 2>/dev/null | head -1)") aspnet=$(digest mcr.microsoft.com/dotnet/aspnet:10.0) sdk=$(digest mcr.microsoft.com/dotnet/sdk:10.0)" >> "$RELEASE_LOG"
echo "Готово: $COMMIT, Healthy. Журнал выпусков: $RELEASE_LOG"
