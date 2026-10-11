#!/usr/bin/env bash
# Поднимает окружение knitERP для тестов: .NET 10 SDK, Docker, SQL Server (контейнер sql), инструменты dotnet-ef.
# Пароль sa — одноразовый для локального контейнера, задаётся KNITERP_SA_PASSWORD (не секрет рабочей среды).
#   KNITERP_SA_PASSWORD='…' bash .claude/skills/kniterp-stage/env-up.sh
# После — export KNITERP_TEST_SQL="Server=localhost,1433;User Id=sa;Password=$KNITERP_SA_PASSWORD;TrustServerCertificate=true;Encrypt=false"
set -euo pipefail
pw="${KNITERP_SA_PASSWORD:?задайте KNITERP_SA_PASSWORD}"
root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)/kniterp"
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | grep -q '^10\.'; then
  # dot.net закрыт прокси — SDK из репозитория Ubuntu.
  apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-10.0 aspnetcore-runtime-10.0 >/dev/null
fi
if ! docker info >/dev/null 2>&1; then
  (dockerd >/tmp/dockerd.log 2>&1 &)
  for i in $(seq 1 60); do docker info >/dev/null 2>&1 && break; sleep 1; done
fi
if docker ps -a --format '{{.Names}}' | grep -qx sql; then
  docker start sql >/dev/null
else
  docker run -d --name sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$pw" -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest >/dev/null
fi
for i in $(seq 1 120); do
  docker exec sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$pw" -Q 'SELECT 1' >/dev/null 2>&1 && break; sleep 2
done
(cd "$root" && dotnet tool restore >/dev/null && dotnet dev-certs https >/dev/null 2>&1 || true)
echo "окружение готово: $(dotnet --version), SQL Server в контейнере sql"
