#!/bin/bash
# Подготовка облачной сессии Claude Code для knitERP: .NET 10, сертификат HTTPS, SQL Server в Docker, Playwright.
# Только в облаке; повторный запуск безопасен. Пароль SQL — одноразовый для тестового контейнера, не секрет рабочей среды.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

ROOT="${CLAUDE_PROJECT_DIR:-$(pwd)}"
SQL_PASSWORD='Test_Pass_123!'

# 1. .NET 10 SDK из репозитория Ubuntu (скрипт dotnet-install прокси не пропускает).
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-10.0 aspnetcore-runtime-10.0 >/dev/null
fi

# 2. Пакеты NuGet и инструмент dotnet-ef.
cd "$ROOT/kniterp"
dotnet restore KnitErp.slnx --locked-mode -v q
dotnet tool restore -v q

# 3. Сертификат разработки: cookie входа требуют HTTPS (нужно для e2e).
dotnet dev-certs https >/dev/null 2>&1 || true

# 4. SQL Server 2022 в Docker для интеграционных и e2e-тестов.
if command -v docker >/dev/null 2>&1; then
  if ! docker info >/dev/null 2>&1; then
    (dockerd >/tmp/dockerd.log 2>&1 &)
    for _ in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 1; done
  fi
  if docker info >/dev/null 2>&1; then
    if [ -z "$(docker ps -aq -f name=^kniterp-sql$)" ]; then
      docker run -d --name kniterp-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$SQL_PASSWORD" -p 1433:1433 \
        mcr.microsoft.com/mssql/server:2022-latest >/dev/null
    else
      docker start kniterp-sql >/dev/null
    fi
    for _ in $(seq 1 60); do
      docker logs kniterp-sql 2>&1 | grep -q 'ready for client connections' && break
      sleep 2
    done
    if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
      echo "export KNITERP_TEST_SQL='Server=localhost,1433;User Id=sa;Password=$SQL_PASSWORD;TrustServerCertificate=True'" >> "$CLAUDE_ENV_FILE"
    fi
  else
    echo "Docker недоступен: интеграционные тесты SQL Server будут пропущены." >&2
  fi
fi

# 5. Playwright для e2e (tests/KnitErp.E2ETests, .NET): браузер уже стоит в /opt/pw-browsers.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  echo "export PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers" >> "$CLAUDE_ENV_FILE"
fi
