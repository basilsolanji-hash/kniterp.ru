#!/bin/bash
# Подготовка облачной сессии Claude Code для knitERP: .NET 10, сертификат HTTPS, SQL Server в Docker, Playwright.
# Только в облаке; повторный запуск безопасен. Пароль SQL — одноразовый для тестового контейнера, не секрет рабочей среды.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

ROOT="${CLAUDE_PROJECT_DIR:-$(pwd)}"

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

# 4. SQL Server 2022 в Docker для интеграционных и e2e-тестов (тот же скрипт поднимает его после простоя посреди сессии).
if SQL=$(bash "$ROOT/.claude/skills/kniterp-tests/ensure-sql.sh"); then
  if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
    echo "export KNITERP_TEST_SQL='$SQL'" >> "$CLAUDE_ENV_FILE"
  fi
fi

# 5. Playwright для e2e (tests/KnitErp.E2ETests, .NET): браузер уже стоит в /opt/pw-browsers.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  echo "export PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers" >> "$CLAUDE_ENV_FILE"
fi
