// Продажи, этап 4 (D1106, D1109): акция на группу, скидка от количества, порядок применения, расшифровка скидки в строке заказа,
// страница «Настройки продаж». 1440 и 390 px. Скриншоты — в $SHOTS (если задан).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, plainText, BASE } from '../lib.mjs';

const SHOTS = process.env.SHOTS;
const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const shot = async (name) => { if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: true }); };
const sfx = Date.now().toString(36).slice(-5).toUpperCase();

// Группа «Свитеры», склад, покупатель, позиция в группе с ценой 1 000.
await page.goto(`${BASE}/catalog/settings`); await ready(page);
await page.locator('form:has(button:has-text("Добавить группу")) label:has-text("Название") input').fill('Свитеры ' + sfx);
await page.getByRole('button', { name: 'Добавить группу' }).click();
await page.waitForSelector(`td:has-text("Свитеры ${sfx}")`);
await page.goto(`${BASE}/warehouses`); await ready(page);
await page.locator('label:has-text("Новый склад") input').fill('Склад ' + sfx);
await page.getByRole('button', { name: 'Добавить', exact: true }).first().click();
await page.waitForSelector(`text=Склад ${sfx}`);
await page.goto(`${BASE}/counterparties`); await ready(page);
await page.getByRole('button', { name: 'Добавить контрагента' }).click();
await page.locator('form label:has-text("Наименование") input').fill('ИП Петров ' + sfx);
await page.locator('form label:has-text("Покупатель") input[type=checkbox]').check();
await page.locator('form').getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector(`text=ИП Петров ${sfx}`);
await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill('СВ4-' + sfx);
await form.locator('label:has-text("Наименование") input').fill('Свитер зимний ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.locator('label:has-text("Группа") select').selectOption({ label: 'Свитеры ' + sfx });
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.locator('.flash').getByRole('link', { name: 'Свитер зимний ' + sfx }).click();
await page.waitForSelector('text=Торговля и маркировка'); await ready(page);
await page.locator('form:has(button:has-text("Сохранить цены")) input[aria-label="Цена продажи"]').fill('1000');
await page.getByRole('button', { name: 'Сохранить цены' }).click();
await page.waitForSelector('text=Цены сохранены.');
ok('справочники');

// Правила: акция 10 % на группу, от 10 шт. — 3 %.
await page.goto(`${BASE}/sales-discounts`); await ready(page);
for (const [kind, name, pct, extra] of [['Promotion', 'Осень ' + sfx, '10', null], ['Quantity', 'От 10 шт. ' + sfx, '3', '10']]) {
  await page.getByRole('button', { name: 'Новое правило' }).click();
  const f = page.locator('section.panel form');
  await f.locator('label:has-text("Вид") select').selectOption(kind);
  await f.locator('label:has-text("Название") input').fill(name);
  await f.locator('label:has-text("Скидка, %") input').fill(pct);
  if (extra) await f.locator('label:has-text("Количество в строке от") input').fill(extra);
  await f.locator('label:has-text("Группа позиций") select').selectOption({ label: 'Свитеры ' + sfx });
  await f.getByRole('button', { name: 'Сохранить' }).click();
  await page.waitForSelector('text=Правило сохранено.');
}
await shot('s4-01-discounts');
ok('правила скидок');

// Заказ: 12 шт. — акция 10 % и 3 % от количества, расшифровка в строке.
await page.goto(`${BASE}/sales-orders`); await ready(page);
await page.getByRole('button', { name: 'Новый заказ' }).click();
await page.locator('label:has-text("Покупатель") select').selectOption({ label: 'ИП Петров ' + sfx });
await page.locator('label:has-text("Склад отгрузки") select').selectOption({ label: 'Склад ' + sfx });
await page.getByRole('button', { name: 'Создать черновик' }).click();
await page.waitForURL(/sales-orders\/\d+/); await ready(page);
const item = page.locator('label:has-text("Позиция") input');
await item.fill('СВ4-' + sfx); await item.blur();
await page.waitForSelector('.sx-price-hint');
await page.locator('label:has-text("Количество") input').fill('12');
await page.getByRole('button', { name: 'Добавить или изменить' }).click();
await page.waitForSelector('.sx-discount-src');
const src = await plainText(page.locator('.sx-discount-src').first());
if (!src.includes('Осень') || !src.includes('От 10 шт.')) throw new Error('нет расшифровки скидки: ' + src);
await shot('s4-02-order-discount-breakdown');
ok('расшифровка скидки в строке: ' + src);
const orderUrl = page.url();

await page.goto(`${BASE}/sales-setup`); await ready(page);
await page.waitForSelector('text=Скидки и акции');
await shot('s4-03-sales-setup');
ok('страница «Настройки продаж»');

await page.setViewportSize({ width: 390, height: 844 });
for (const [url, name] of [[`${BASE}/sales-discounts`, 'discounts'], [orderUrl, 'order'], [`${BASE}/sales-setup`, 'setup']]) {
  await page.goto(url); await ready(page);
  await assertNoHorizontalScroll(page, url);
  await shot('s4-04-phone-' + name);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
