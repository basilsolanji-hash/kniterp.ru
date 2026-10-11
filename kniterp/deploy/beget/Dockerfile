# Образ knitERP. Сборка из папки kniterp: docker build -f deploy/beget/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
COPY . .
# D109: пакеты — только точные версии и хеши из packages.lock.json.
RUN dotnet restore src/KnitErp.Web --locked-mode && dotnet publish src/KnitErp.Web -c Release -o /app --nologo --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Коммит исходников в метаданных образа (аудит 10.10.2026, п. 9): docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.revision"}}'.
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.revision=$SOURCE_COMMIT org.opencontainers.image.title=knitERP
ENV KNITERP_COMMIT=$SOURCE_COMMIT
WORKDIR /app
COPY --from=build /app .
# Приложение слушает HTTP внутри сети Docker; HTTPS снаружи даёт Caddy на сервере.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "KnitErp.Web.dll"]
