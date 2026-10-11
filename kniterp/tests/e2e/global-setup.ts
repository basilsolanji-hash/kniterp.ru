import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { chromium, type FullConfig } from '@playwright/test';
import { totp } from './totp';

/**
 * Запуск приложения в режиме разработки на новой базе: миграции и организация «Солвер» создаются при старте,
 * ссылка установки пароля Владельца берётся из лога. Затем Владелец задаёт пароль и подключает 2FA.
 */
export default async function globalSetup(config: FullConfig) {
  const baseURL = (config.projects[0].use.baseURL as string) ?? 'https://127.0.0.1:5199';
  const sql = process.env.KNITERP_TEST_SQL;
  if (!sql) throw new Error('Не задан KNITERP_TEST_SQL — строка подключения к SQL Server для e2e.');
  const database = `kniterp_e2e_${Date.now()}`;
  const root = join(__dirname, '..', '..');

  const server = spawn('dotnet', ['run', '--project', join(root, 'src', 'KnitErp.Web'), '--no-launch-profile', '--no-build'], {
    cwd: root,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: baseURL,
      ConnectionStrings__KnitErp: `${sql};Database=${database}`,
    },
    detached: true,
  });
  writeFileSync(join(__dirname, '.server.pid'), String(server.pid));

  const token = await new Promise<string>((resolve, reject) => {
    let log = '';
    const timer = setTimeout(() => reject(new Error('Приложение не запустилось за 180 с:\n' + log.slice(-4000))), 180_000);
    const onData = (chunk: Buffer) => {
      log += chunk.toString();
      const m = log.match(/\/account\/invite\?token=([\w%-]+)/);
      if (m && log.includes('Now listening on')) {
        clearTimeout(timer);
        resolve(decodeURIComponent(m[1]));
      }
    };
    server.stdout.on('data', onData);
    server.stderr.on('data', onData);
    server.on('exit', (code) => reject(new Error(`Приложение завершилось с кодом ${code}:\n${log.slice(-4000)}`)));
  });

  const browser = await chromium.launch(config.projects[0].use.launchOptions ?? {});
  const page = await browser.newPage({ baseURL, locale: 'ru-RU', ignoreHTTPSErrors: true });
  await page.goto(`/account/invite?token=${encodeURIComponent(token)}`);
  const password = `E2e-${Date.now()}-пароль`;
  await page.getByLabel('Новый пароль').fill(password);
  await page.getByLabel('Повторите пароль').fill(password);
  await page.getByRole('button', { name: 'Сохранить пароль' }).click();

  await page.getByText('Пароль установлен.').waitFor();
  await page.goto('/account/login');
  await page.getByLabel('Эл. почта').fill('owner@kniterp.local');
  await page.getByLabel('Пароль').fill(password);
  await page.getByRole('button', { name: 'Войти' }).click();
  await page.waitForURL(/setup-2fa/);

  const key = (await page.locator('.key').innerText()).trim();
  await page.getByLabel('Код из приложения').fill(totp(key));
  await page.getByRole('button', { name: 'Подключить и войти' }).click();
  await page.waitForURL((url) => !url.pathname.startsWith('/account'));

  mkdirSync(join(__dirname, '.auth'), { recursive: true });
  await page.context().storageState({ path: join(__dirname, '.auth', 'owner.json') });
  writeFileSync(join(__dirname, '.auth', 'run.json'), JSON.stringify({ database, password }));
  await browser.close();
}
