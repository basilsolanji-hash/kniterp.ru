// D325 и раскладка: печать «12x5» — столько этикеток; карточка позиции на широком экране — разделы в две колонки,
// таблицы на всю ширину; на 390 px нет горизонтальной прокрутки.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, plainText, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1920);
await page.setViewportSize({ width: 1920, height: 1000 });
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);

await page.goto(`${BASE}/catalog/labels/print?ids=1x3,2`); await ready(page);
const total = await plainText(page.locator('.print-bar span').first());
if (!total.includes('Этикеток: 4')) throw new Error('ожидалось 4 этикетки: ' + total);
const labels = await page.locator('.item-label').count();
if (labels !== 4) throw new Error('на листе этикеток: ' + labels);
await page.locator('input[name=copies]').fill('2'); await page.getByRole('button', { name: 'Обновить' }).click(); await ready(page);
if (await page.locator('.item-label').count() !== 8) throw new Error('копии ×2 не применились');
ok('печать по количеству: 1x3 + 2 = 4 этикетки, ×2 копии = 8');

await page.goto(`${BASE}/catalog/items/1`); await ready(page);
const boxes = await page.locator('.card-flow > .card-sec').evaluateAll(els => els.map(e => Math.round(e.getBoundingClientRect().left)));
if (new Set(boxes).size < 2) throw new Error('карточка не в две колонки: ' + boxes);
ok('карточка позиции на 1920 — две колонки разделов: ' + [...new Set(boxes)].join(', '));

await page.goto(`${BASE}/catalog/settings`); await ready(page);
const tw = await page.locator('table').first().evaluate(t => t.getBoundingClientRect().width / t.closest('.main').getBoundingClientRect().width);
if (tw < 0.9) throw new Error('таблица групп не на всю ширину: ' + tw);
ok('таблица групп на всю ширину');

for (const p of ['/catalog/items/1', '/catalog/settings', '/stock', '/catalog']) {
  await page.setViewportSize({ width: 390, height: 800 });
  await page.goto(BASE + p); await ready(page);
  await assertNoHorizontalScroll(page, p + ' @390');
}
ok('390 px без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
