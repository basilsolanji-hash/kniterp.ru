#!/bin/bash
# SQL Server 2022 в Docker для интеграционных и e2e-тестов knitERP: поднимает демон docker и контейнер kniterp-sql, если они
# остановились (после простоя облачной среды тесты падают с «A network-related or instance-specific error»). Повторный запуск безопасен.
# Пароль — одноразовый для тестового контейнера, не секрет рабочей среды. Печатает строку подключения для KNITERP_TEST_SQL.
set -euo pipefail
SQL_PASSWORD='Test_Pass_123!'

command -v docker >/dev/null 2>&1 || { echo "Docker не установлен: интеграционные тесты SQL Server будут пропущены." >&2; exit 1; }
if ! docker info >/dev/null 2>&1; then
  (dockerd >/tmp/dockerd.log 2>&1 &)
  for _ in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 1; done
fi
docker info >/dev/null 2>&1 || { echo "Docker недоступен: интеграционные тесты SQL Server будут пропущены." >&2; exit 1; }

started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
if [ -z "$(docker ps -aq -f name=^kniterp-sql$)" ]; then
  docker run -d --name kniterp-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$SQL_PASSWORD" -p 1433:1433 \
    mcr.microsoft.com/mssql/server:2022-latest >/dev/null
elif [ -z "$(docker ps -q -f name=^kniterp-sql$)" ]; then
  docker start kniterp-sql >/dev/null
else
  started=""  # уже работает
fi

if [ -n "$started" ]; then
  # Ждём «ready» только в свежем логе: после перезапуска в старом логе уже есть прежнее сообщение.
  for _ in $(seq 1 60); do
    docker logs --since "$started" kniterp-sql 2>&1 | grep -q 'ready for client connections' && break
    sleep 2
  done
fi

echo "Server=localhost,1433;User Id=sa;Password=$SQL_PASSWORD;TrustServerCertificate=True"
