// D325: «Этикетки» в складском документе — по количеству строк (3 + 2 = 5 этикеток), склад и документ создаются через экран.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, plainText, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();
const [codeA, codeB] = (process.env.E2E_ITEMS ?? 'EGCQ1-0001,PGGCQ1-1').split(',');

await page.goto(`${BASE}/warehouses`); await ready(page);
await page.locator('label:has-text("Новый склад") input').fill('Склад ' + sfx);
await page.locator('form:has(label:has-text("Новый склад"))').getByRole('button', { name: 'Добавить' }).click();
await page.waitForSelector(`text=Склад ${sfx}`);
ok('склад создан');

await page.goto(`${BASE}/stock-documents?kind=1`); await ready(page);
await page.locator('.page-head .actions button').first().click();
const form = page.locator('.panel form');
await form.waitFor();
const wh = form.locator('label:has-text("Склад") select').first();
await wh.selectOption({ label: 'Склад ' + sfx }).catch(() => {});
await form.locator('label:has-text("Причина") select').selectOption({ label: 'Закупка у поставщика' }); // без причины не проводится
await form.getByRole('button', { name: 'Создать черновик' }).click();
await page.waitForURL(/stock-documents\/\d+/); await ready(page);
ok('черновик поступления создан: ' + page.url());

for (const [code, qty] of [[codeA, '3'], [codeB, '2']]) {
  const line = page.locator('form.inline-form:has(label:has-text("Количество"))').first();
  await line.locator('label:has-text("Позиция") input').fill(code);
  await line.locator('label:has-text("Количество") input').fill(qty);
  await line.getByRole('button', { name: 'Добавить' }).click();
  await page.waitForSelector(`td:has-text("${code}")`);
}
ok('строки добавлены');

const link = page.getByRole('link', { name: 'Этикетки', exact: true });
const href = await link.getAttribute('href');
if (!/ids=\d+x3,\d+x2$/.test(href)) throw new Error('ссылка этикеток: ' + href);
await page.goto(`${BASE}/${href}`); await ready(page);
const n = await page.locator('.item-label').count();
const total = await plainText(page.locator('.print-bar span').first());
if (n !== 5 || !total.includes('Этикеток: 5')) throw new Error(`этикеток ${n}, «${total}»`);
ok('кнопка «Этикетки» в документе: 3 + 2 = 5 этикеток');

await page.goBack(); await ready(page);
await page.getByRole('button', { name: 'Провести', exact: true }).click();
await page.waitForSelector('text=Проведён', { timeout: 15000 }).catch(() => {});
await page.goto(`${BASE}/stock`); await ready(page);
await page.locator('label:has-text("Склад") select').selectOption({ label: 'Склад ' + sfx });
await page.getByRole('button', { name: 'Показать' }).click(); await page.waitForTimeout(800);
const tags = await page.getByRole('link', { name: 'Ценники', exact: true }).getAttribute('href');
const byStock = await page.getByRole('link', { name: 'Этикетки по остатку' }).getAttribute('href');
const counts = byStock.split('ids=')[1].split(',').map(x => x.split('x')[1]).sort().join(',');
if (!/ids=\d+,\d+$/.test(tags) || counts !== '2,3') throw new Error(`остатки: ${tags} | ${byStock}`);
ok('остатки: «Ценники» по одной, «Этикетки по остатку» 3 и 2');
await page.goBack(); await ready(page);
await page.setViewportSize({ width: 390, height: 844 });
await assertNoHorizontalScroll(page, 'документ @390');
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
console.log('✓ ошибок JS: 0');
