import { defineConfig, devices } from '@playwright/test';

// Приложение поднимает global-setup: своя база на каждый запуск, вход Владельца с 2FA, состояние входа — в .auth/.
export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  globalSetup: './global-setup.ts',
  globalTeardown: './global-teardown.ts',
  use: {
    baseURL: process.env.KNITERP_E2E_URL ?? 'https://127.0.0.1:5199',
    storageState: '.auth/owner.json',
    locale: 'ru-RU',
    // Локальный сертификат разработки (dotnet dev-certs https): cookie входа требуют HTTPS.
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
    launchOptions: process.env.PLAYWRIGHT_CHROMIUM_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_PATH } : {},
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] } },
    // Телефон 390 px: без горизонтальной прокрутки страницы (ТЗ «Сотрудники» §8).
    { name: 'phone', use: { ...devices['Desktop Chrome'], viewport: { width: 390, height: 844 } }, grep: /@phone/ },
  ],
});
