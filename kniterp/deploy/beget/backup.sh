#!/usr/bin/env bash
# Резервная копия knitERP (D54): ./backup.sh full | log
# SQL Server Express не шифрует копии сам, поэтому файл сразу шифруется на сервере (AES-256, ключ /opt/kniterp/backup.key)
# и исходник удаляется. Хранение 35 дней. Если в .env задан RCLONE_REMOTE (например, beget-s3:kniterp-backups),
# зашифрованные копии ещё и выгружаются во внешнее хранилище — тогда выгрузка обязательна.
#
# Готовая копия появляется только после всех этапов (аудит 10.10.2026, п. 3–4): шифрование пишет во временный
# *.part, затем копия расшифровывается и проверяется целостность архива, и только после этого файл переименовывается.
# Итог каждого запуска пишется в $BACKUP_STATUS_DIR: last-ok-<вид>, last-offsite, а при ошибке — last-error с причиной;
# monitor.sh по ним поднимает тревогу.
set -euo pipefail
cd "$(dirname "$0")"
set -a; . ./.env; set +a
umask 077
STATUS_DIR="${BACKUP_STATUS_DIR:-/var/lib/kniterp-backup}"
mkdir -p "$STATUS_DIR"
STEP="подготовка"
PART=""
fail() {
  local code=$?
  [[ -n "$PART" ]] && rm -f "$PART"
  echo "$(date -u +%FT%TZ) ${KIND:-?} ошибка на этапе «$STEP» (код $code)" | tee "$STATUS_DIR/last-error" >&2
  ./monitor.sh notify "Резервная копия не создана: $(cat "$STATUS_DIR/last-error")" >/dev/null 2>&1 || true
  exit "$code"
}
trap fail ERR
KIND="${1:-full}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
KEY="${BACKUP_KEY_FILE:-/opt/kniterp/backup.key}"
sql() { SQLCMDPASSWORD="$SQL_SA_PASSWORD" docker compose exec -T -e SQLCMDPASSWORD sql /opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa "$@"; }

STEP="резервная копия SQL Server"
case "$KIND" in
  full)
    FILE="kniterp-full-$STAMP.bak"
    sql -Q "BACKUP DATABASE [kniterp] TO DISK = N'/var/opt/mssql/backup/$FILE' WITH CHECKSUM, INIT, NAME = N'knitERP full $STAMP';
            RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/backup/$FILE' WITH CHECKSUM;" >/dev/null ;;
  log)
    # Журнал копируется только после первой полной копии.
    ls "$BACKUP_DIR"/enc/kniterp-full-*.enc >/dev/null 2>&1 || exit 0
    FILE="kniterp-log-$STAMP.trn"
    sql -Q "BACKUP LOG [kniterp] TO DISK = N'/var/opt/mssql/backup/$FILE' WITH CHECKSUM, INIT;" >/dev/null ;;
  *) echo "Использование: $0 full|log"; exit 2 ;;
esac

OUT="$BACKUP_DIR/enc/$FILE.gz.enc"
PART="$OUT.part"
STEP="сжатие и шифрование"
gzip -c "$BACKUP_DIR/raw/$FILE" | openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt -pass "file:$KEY" > "$PART"
STEP="проверка зашифрованной копии"
[[ -s "$PART" ]] || { echo "Зашифрованная копия пустая" >&2; false; }
openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -pass "file:$KEY" -in "$PART" | gzip -t
mv -f "$PART" "$OUT"
# Аудит 12.10.2026: отпечаток копии хранится на сервере (во внешнее хранилище не уходит) — restore.sh сверяет его и замечает
# подмену или порчу копии в хранилище до восстановления.
(cd "$BACKUP_DIR/enc" && sha256sum "$(basename "$OUT")" >> SHA256SUMS)
PART=""
rm -f "$BACKUP_DIR/raw/$FILE"
find "$BACKUP_DIR/enc" -name 'kniterp-*' -mtime +35 -delete
find "$BACKUP_DIR/enc" -name '*.part' -mmin +120 -delete

if [[ -n "${RCLONE_REMOTE:-}" ]]; then
  STEP="выгрузка во внешнее хранилище"
  command -v rclone >/dev/null || { echo "RCLONE_REMOTE задан, но rclone не установлен" >&2; false; }
  rclone copy "$OUT" "$RCLONE_REMOTE" --quiet
  STEP="проверка копии во внешнем хранилище"
  rclone check "$BACKUP_DIR/enc" "$RCLONE_REMOTE" --one-way --include "$(basename "$OUT")" --quiet
  date -u +%FT%TZ > "$STATUS_DIR/last-offsite"
fi

date -u +%FT%TZ > "$STATUS_DIR/last-ok-$KIND"
rm -f "$STATUS_DIR/last-error"
echo "$(date -u +%FT%TZ) $KIND ok $(basename "$OUT")"
