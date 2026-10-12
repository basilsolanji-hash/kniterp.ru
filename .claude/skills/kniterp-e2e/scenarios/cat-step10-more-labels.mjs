// D325: «Этикетки» в заказе поставщику и инвентаризации, «Напечатать новые ценники» после переоценки; раздел «Выгрузка цен» (D327).
// Нужны: поставщик, склад с остатком (после cat-step9) и позиция с ценой (E2E_PRICED, по умолчанию ПЛ-CV9UL).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const [codeA] = (process.env.E2E_ITEMS ?? 'EGCQ1-0001,PGGCQ1-1').split(',');
const priced = process.env.E2E_PRICED ?? 'ПЛ-CV9UL';

// Заказ поставщику: строка 4 шт → 4 этикетки.
await page.goto(`${BASE}/purchase-orders`); await ready(page);
await page.getByRole('button', { name: 'Новый заказ' }).click();
await page.getByRole('button', { name: 'Создать черновик' }).click();
await page.waitForURL(/purchase-orders\/\d+/); await ready(page);
const line = page.locator('form.inline-form:has(label:has-text("Количество"))').first();
await line.locator('label:has-text("Позиция") input').fill(codeA);
await line.locator('label:has-text("Количество") input').fill('4');
await line.locator('label:has-text("Цена") input').fill('100');
await line.locator('button[type=submit]').click();
await page.waitForSelector(`td:has-text("${codeA}")`);
const po = await page.getByRole('link', { name: 'Этикетки', exact: true }).getAttribute('href');
if (!/ids=\d+x4$/.test(po)) throw new Error('заказ поставщику: ' + po);
ok('заказ поставщику: «Этикетки» по количеству строки (x4)');

// Инвентаризация склада с остатком: этикеток по учёту.
await page.goto(`${BASE}/inventory`); await ready(page);
await page.getByRole('button', { name: 'Новая инвентаризация' }).click();
const wh = page.locator('form.inline-form:has(button:has-text("Создать")) label:has-text("Склад") select');
// Склад с остатком: E2E_STOCK_WAREHOUSE (название) или последний в списке.
if (process.env.E2E_STOCK_WAREHOUSE) await wh.selectOption({ label: process.env.E2E_STOCK_WAREHOUSE });
else await wh.selectOption(await wh.locator('option').last().getAttribute('value'));
await page.locator('form.inline-form:has(button:has-text("Создать"))').getByRole('button', { name: 'Создать' }).click();
await page.waitForURL(/inventory\/\d+/); await ready(page);
// Найденная позиция с фактом 5 — этикеток по факту.
const found = page.locator('form:has(label:has-text("Позиции нет в списке"))');
await found.locator('label:has-text("Позиции нет в списке") input').fill(codeA);
await found.locator('label:has-text("Факт") input').fill('5');
await found.getByRole('button', { name: 'Добавить' }).click();
await page.waitForSelector(`td:has-text("${codeA}")`);
let inv = '';
for (let i = 0; i < 30 && !/x5(,|$)/.test(inv); i++) { inv = await page.getByRole('link', { name: 'Этикетки', exact: true }).getAttribute('href'); await page.waitForTimeout(200); }
if (!/x5(,|$)/.test(inv)) throw new Error('инвентаризация: ' + inv);
ok('инвентаризация: «Этикетки» по факту (x5)');

// Переоценка одной позиции +10 % → «Напечатать новые ценники».
await page.goto(`${BASE}/catalog/revaluation`); await ready(page);
await page.locator('label:has-text("Процент") input').fill('10');
await page.locator('label:has-text("Поиск") input').fill(priced);
await page.getByRole('button', { name: 'Предпросмотр' }).click();
await page.getByRole('button', { name: 'Применить переоценку' }).click();
await page.getByRole('button', { name: 'Да', exact: true }).click();
const tags = page.getByRole('link', { name: 'Напечатать новые ценники' });
await tags.waitFor();
if (!/ids=\d+$/.test(await tags.getAttribute('href'))) throw new Error('переоценка: ' + await tags.getAttribute('href'));
ok('переоценка: «Напечатать новые ценники»');

await page.setViewportSize({ width: 390, height: 844 });
for (const p of ['/purchase-orders', '/catalog/revaluation', '/exchange/moysklad']) {
  await page.goto(BASE + p); await ready(page);
  await assertNoHorizontalScroll(page, p + ' @390');
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
console.log('✓ ошибок JS: 0');
