# Сквозные тесты knitERP (Playwright)

Приложение запускается в режиме разработки на новой базе SQL Server (`kniterp_e2e_<время>`), Владелец «Солвер» задаёт пароль
по ссылке из лога и подключает 2FA (код считает `totp.ts`). Состояние входа — в `.auth/` (не в репозитории).

```bash
dotnet build KnitErp.slnx
dotnet dev-certs https          # cookie входа требуют HTTPS
cd tests/e2e && npm ci
KNITERP_TEST_SQL='Server=localhost,1433;User Id=sa;Password=…;TrustServerCertificate=True' npx playwright test
```

Браузер — Chromium версии Playwright 1.56.1; если он установлен в другом месте, укажите `PLAYWRIGHT_BROWSERS_PATH`
или `PLAYWRIGHT_CHROMIUM_PATH`. Проект `phone` проверяет экраны с отметкой `@phone` в ширине 390 px.
