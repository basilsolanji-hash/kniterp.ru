#!/usr/bin/env bash
# Первая установка knitERP на VPS Beget (Ubuntu 24.04). Запуск от root из папки kniterp/deploy/beget:
#   ./install.sh erp.вашдомен.ru
# Повторный запуск безопасен: существующие .env, ключи и база не перезаписываются.
set -euo pipefail
cd "$(dirname "$0")"

DOMAIN="${1:-}"
[[ $EUID -eq 0 ]] || { echo "Запустите от root (sudo ./install.sh домен)."; exit 1; }
[[ -n "$DOMAIN" ]] || { echo "Укажите домен: ./install.sh erp.example.ru (DNS-запись A домена — на IP этого сервера)."; exit 1; }

echo "== 1/8 Пакеты: Docker, Caddy, openssl"
export DEBIAN_FRONTEND=noninteractive
# На новом сервере в первые минуты работает автоматическое обновление Ubuntu и держит блокировку пакетов —
# ждём её до 15 минут, а не падаем.
APT=(apt-get -o DPkg::Lock::Timeout=900)
"${APT[@]}" update -q
"${APT[@]}" install -y -q docker.io docker-compose-v2 caddy openssl cron ufw >/dev/null
systemctl enable --now docker cron >/dev/null

echo "== 2/8 Секреты (.env, ключ резервных копий)"
rand() { openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c "$1"; }
if [[ ! -f .env ]]; then
  umask 077
  cat > .env <<ENV
KNITERP_DOMAIN=$DOMAIN
SQL_SA_PASSWORD=Kn1-$(rand 28)
SQL_APP_PASSWORD=Kn1-$(rand 28)
MASTER_KEY=$(openssl rand -base64 32)
PREVIOUS_MASTER_KEYS=
SQL_MEMORY_MB=2048
BACKUP_DIR=/opt/kniterp/backups
ASSISTANT_API_KEY=
REQUISITES_API_KEY=
RCLONE_REMOTE=
ENV
  echo "   Создан .env. СРАЗУ сохраните MASTER_KEY в сейф Владельца (cat .env) — без него не расшифровать 2FA и не проверить журналы."
else
  echo "   .env уже есть — не меняю."
fi
set -a; . ./.env; set +a

mkdir -p /opt/kniterp "$BACKUP_DIR/raw" "$BACKUP_DIR/enc"
chown 10001:0 "$BACKUP_DIR/raw"   # пользователь mssql в контейнере
chmod 700 "$BACKUP_DIR/enc"
if [[ ! -f /opt/kniterp/backup.key ]]; then
  (umask 077; openssl rand -base64 48 > /opt/kniterp/backup.key)
  echo "   Создан ключ шифрования копий /opt/kniterp/backup.key — его копию тоже в сейф."
fi

echo "== 3/8 Сборка образа приложения (несколько минут)"
docker compose build app

echo "== 4/8 Запуск SQL Server Express"
docker compose up -d sql
for i in $(seq 1 60); do
  [[ "$(docker inspect -f '{{.State.Health.Status}}' "$(docker compose ps -q sql)")" == healthy ]] && break
  sleep 5
done

sql() { SQLCMDPASSWORD="$SQL_SA_PASSWORD" docker compose exec -T -e SQLCMDPASSWORD sql /opt/mssql-tools18/bin/sqlcmd -C -b -I -S localhost -U sa "$@"; }

echo "== 5/8 Схема базы (миграции)"
docker compose run --rm -e ConnectionStrings__KnitErp="Server=sql;Database=kniterp;User Id=sa;Password=$SQL_SA_PASSWORD;Encrypt=true;TrustServerCertificate=true" app migrate

echo "== 6/8 Учётная запись приложения в базе (только данные, без права менять схему)"
sql -v AppPassword="$SQL_APP_PASSWORD" -i /dev/stdin <<'SQL'
IF SUSER_ID('kniterp_app') IS NULL CREATE LOGIN [kniterp_app] WITH PASSWORD = '$(AppPassword)', CHECK_POLICY = ON;
ELSE ALTER LOGIN [kniterp_app] WITH PASSWORD = '$(AppPassword)';
GO
USE [kniterp];
IF USER_ID('kniterp_app') IS NULL CREATE USER [kniterp_app] FOR LOGIN [kniterp_app];
ALTER ROLE [db_datareader] ADD MEMBER [kniterp_app];
ALTER ROLE [db_datawriter] ADD MEMBER [kniterp_app];
GRANT EXECUTE TO [kniterp_app];
ALTER DATABASE [kniterp] SET RECOVERY FULL;
GO
SQL

echo "== 7/8 Запуск приложения и HTTPS"
docker compose up -d app
cat > /etc/caddy/Caddyfile <<CADDY
# knitERP: HTTPS-сертификат Let's Encrypt выпускается и продлевается сам.
$DOMAIN {
    encode gzip
    reverse_proxy 127.0.0.1:8080
}
CADDY
systemctl reload caddy || systemctl restart caddy
ufw allow 22/tcp >/dev/null; ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null
ufw --force enable >/dev/null

echo "== 8/8 Резервные копии по расписанию"
cat > /etc/cron.d/kniterp-backup <<CRON
# knitERP: полная копия ночью, журнал транзакций каждые 15 минут (D54).
30 2 * * * root $(pwd)/backup.sh full >> /var/log/kniterp-backup.log 2>&1
*/15 * * * * root $(pwd)/backup.sh log >> /var/log/kniterp-backup.log 2>&1
CRON
cat > /etc/cron.d/kniterp-monitor <<CRON
# knitERP: наблюдение каждую минуту — /health, диск, свежесть копий, сертификат (D62).
* * * * * root $(pwd)/monitor.sh >> /var/log/kniterp-monitor.log 2>&1
CRON
chmod +x backup.sh update.sh monitor.sh
./backup.sh full

for i in $(seq 1 30); do curl -fsS http://127.0.0.1:8080/health >/dev/null 2>&1 && break; sleep 2; done
echo
echo "Готово. Проверка: https://$DOMAIN/health"
echo "Дальше — создать организацию (ссылка Владельца печатается один раз):"
echo "  cd $(pwd) && docker compose run --rm app create-organization --name \"…\" --short-name \"…\" \\"
echo "     --inn … --kpp 770501001 --kpp-verified=yes --owner-email … --owner-name \"…\" --url https://$DOMAIN"
