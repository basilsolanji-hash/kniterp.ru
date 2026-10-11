// Общие шаги e2e knitERP: вход Владельца с 2FA, проверка страницы на ширине, сбор ошибок JS.
// Используется из сценариев: import { ownerSession, checkPage } from '<путь>/lib.mjs'.
import { createRequire } from 'node:module';
import crypto from 'node:crypto';
import fs from 'node:fs';
const require = createRequire('/opt/node22/lib/node_modules/');
const { chromium } = require('playwright');

export const BASE = process.env.E2E_BASE ?? 'https://localhost:5443';
const DIR = process.env.E2E_DIR ?? '/tmp/kniterp-e2e';
const PASSWORD = 'e2e-only-' + crypto.randomBytes(6).toString('hex'); // одноразовый пароль тестовой базы

function base32(s) {
  const a = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; let bits = '';
  for (const c of s.replace(/[\s=-]/g, '').toUpperCase()) bits += a.indexOf(c).toString(2).padStart(5, '0');
  const out = []; for (let i = 0; i + 8 <= bits.length; i += 8) out.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(out);
}
export function totp(secret, t = Date.now()) {
  const counter = Buffer.alloc(8); counter.writeBigUInt64BE(BigInt(Math.floor(t / 30000)));
  const h = crypto.createHmac('sha1', base32(secret)).update(counter).digest();
  const o = h[h.length - 1] & 15;
  return (((h.readUInt32BE(o) & 0x7fffffff) % 1_000_000) + '').padStart(6, '0');
}

/** Браузер и контекст с входом Владельца: приглашение → пароль → вход → подключение 2FA. Ключ 2FA хранится в $E2E_DIR. */
export async function ownerSession(width = 1440) {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width, height: 900 }, locale: 'ru-RU' });
  const page = await ctx.newPage();
  const state = `${DIR}/owner.json`;
  let saved = fs.existsSync(state) ? JSON.parse(fs.readFileSync(state, 'utf8')) : null;
  if (!saved) {
    const invite = fs.readFileSync(`${DIR}/invite.txt`, 'utf8').trim();
    await page.goto(invite);
    await page.locator('input[type=password]').nth(0).fill(PASSWORD);
    await page.locator('input[type=password]').nth(1).fill(PASSWORD);
    await page.getByRole('button', { name: 'Сохранить пароль' }).click();
    await page.waitForSelector('text=Пароль установлен');
    saved = { email: 'owner@kniterp.local', password: PASSWORD, key: null };
  }
  await page.goto(`${BASE}/account/login`);
  await page.locator('input[type=email], input[name*=Email]').first().fill(saved.email);
  await page.locator('input[type=password]').first().fill(saved.password);
  await page.getByRole('button', { name: 'Войти' }).click();
  await page.waitForLoadState('networkidle');
  // Код TOTP одноразовый: приложение отклоняет повтор в том же 30-секундном окне — ждём новое окно после прошлого входа.
  const freshStep = async () => {
    const step = () => Math.floor(Date.now() / 30000);
    while (saved.lastStep && step() <= saved.lastStep) await page.waitForTimeout(1000);
    saved.lastStep = step();
  };
  if (page.url().includes('setup-2fa')) {
    saved.key = (await page.locator('.key').innerText()).replace(/\s/g, '');
    await freshStep();
    await page.locator('input[autocomplete=one-time-code]').fill(totp(saved.key));
    await page.getByRole('button', { name: 'Подключить и войти' }).click();
  } else if (page.url().includes('login-2fa')) {
    await freshStep();
    await page.locator('input[autocomplete=one-time-code]').fill(totp(saved.key));
    await page.getByRole('button', { name: 'Подтвердить' }).click();
  }
  await page.waitForLoadState('networkidle');
  fs.writeFileSync(state, JSON.stringify(saved));
  if (page.url().includes('/account/')) throw new Error('вход не выполнен: ' + page.url());
  return { browser, ctx, page };
}

/** Ошибки JS страницы: console.error и необработанные исключения. */
export function collectErrors(page) {
  const errors = [];
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', e => errors.push(String(e)));
  return errors;
}

/** Нет горизонтальной прокрутки страницы (телефон 390 px). */
export async function assertNoHorizontalScroll(page, label) {
  const { sw, cw } = await page.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth }));
  if (sw > cw + 1) throw new Error(`${label}: горизонтальная прокрутка ${sw} > ${cw}`);
}

/** Дождаться интерактивности Blazor (кнопки работают после подключения схемы). */
export async function ready(page) {
  await page.waitForLoadState('networkidle');
  await page.waitForFunction(() => !document.querySelector('.loading-progress') && !!window.Blazor, null, { timeout: 15000 }).catch(() => {});
  await page.waitForTimeout(300);
}

/** Текст элемента с обычными пробелами: в ru-RU разделитель тысяч — неразрывный пробел. */
export async function plainText(locator) {
  return (await locator.innerText()).replace(/[\u00a0\u202f]/g, ' ');
}
