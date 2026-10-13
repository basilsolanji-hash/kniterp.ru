// D354 (Р1): заказ «Свитер + Доставка» — отгрузка свитера и акт оказания услуг на доставку; после обоих заказ отгружен полностью
// (кнопок «Оформить отгрузку» и «Акт на услуги» нет). Данные создаются сценарием. 1440 и 390 px.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();

async function createItem(code, name, type) {
  await page.goto(`${BASE}/catalog`); await ready(page);
  await page.getByRole('button', { name: 'Добавить позицию' }).click();
  const form = page.locator('.panel form');
  await form.locator('label:has-text("Код") input').fill(code);
  await form.locator('label:has-text("Наименование") input').fill(name);
  await form.locator('label:has-text("Тип") select').selectOption(type);
  await page.waitForTimeout(200);
  await form.getByRole('button', { name: 'Сохранить' }).click();
  await page.waitForSelector('text=Позиция сохранена.');
}

const [sweater, delivery] = ['АС-' + sfx, 'АД-' + sfx];
await createItem(sweater, 'Свитер ' + sfx, 'Finished');
// Услуга: ставка НДС обязательна.
await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const sf = page.locator('.panel form');
await sf.locator('label:has-text("Код") input').fill(delivery);
await sf.locator('label:has-text("Наименование") input').fill('Доставка ' + sfx);
await sf.locator('label:has-text("Тип") select').selectOption('Service');
await page.waitForTimeout(300);
await sf.locator('label:has-text("Ставка НДС") select').selectOption({ index: 1 });
await sf.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
ok('свитер и услуга «Доставка»');

await page.goto(`${BASE}/warehouses`); await ready(page);
await page.locator('label:has-text("Новый склад") input').fill('Склад ' + sfx);
await page.locator('form:has(label:has-text("Новый склад"))').getByRole('button', { name: 'Добавить' }).click();
await page.waitForSelector(`text=Склад ${sfx}`);
await page.goto(`${BASE}/stock-documents?kind=1`); await ready(page);
await page.locator('.page-head .actions button').first().click();
const head = page.locator('.panel form');
await head.waitFor();
await head.locator('label:has-text("Склад") select').first().selectOption({ label: 'Склад ' + sfx });
await head.locator('label:has-text("Причина") select').selectOption({ label: 'Закупка у поставщика' });
await head.getByRole('button', { name: 'Создать черновик' }).click();
await page.waitForURL(/stock-documents\/\d+/); await ready(page);
const rl = page.locator('form.inline-form:has(label:has-text("Количество"))').first();
await rl.locator('label:has-text("Позиция") input').fill(sweater);
await rl.locator('label:has-text("Количество") input').fill('3');
await rl.getByRole('button', { name: 'Добавить' }).click();
await page.waitForSelector(`td:has-text("${sweater}")`);
await page.getByRole('button', { name: 'Провести', exact: true }).click();
await page.waitForSelector('text=Проведён');

await page.goto(`${BASE}/counterparties`); await ready(page);
await page.getByRole('button', { name: 'Добавить контрагента' }).click();
await page.locator('label:has-text("Наименование") input').first().fill('Ателье ' + sfx);
await page.locator('label.inline:has-text("Покупатель") input').check();
await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
await page.waitForSelector(`td:has-text("Ателье ${sfx}")`);

await page.goto(`${BASE}/sales-orders`); await ready(page);
await page.getByRole('button', { name: 'Новый заказ' }).click();
const orderForm = page.locator('.panel form');
await orderForm.waitFor();
await orderForm.locator('label:has-text("Покупатель") select').selectOption({ label: 'Ателье ' + sfx });
await orderForm.locator('label:has-text("Склад") select').selectOption({ label: 'Склад ' + sfx });
await orderForm.getByRole('button', { name: 'Создать черновик' }).click();
await page.waitForURL(/sales-orders\/\d+/); await ready(page);
const orderUrl = page.url();
for (const [code, qty, price] of [[sweater, '1', '3000'], [delivery, '1', '500']]) {
  const lf = page.locator('form.inline-form:has(label:has-text("Количество"))').first();
  await lf.locator('label:has-text("Позиция") input').fill(code);
  await lf.locator('label:has-text("Позиция") input').blur();
  await lf.locator('label:has-text("Количество") input').fill(qty);
  await lf.locator('label:has-text("Цена") input').fill(price);
  await lf.getByRole('button', { name: 'Добавить или изменить' }).click();
  await page.waitForSelector(`td:has-text("${code}")`);
}
await page.getByRole('button', { name: 'Подтвердить заказ' }).click();
await page.waitForSelector('text=Заказ подтверждён');
ok('заказ «свитер + доставка» подтверждён');

await page.getByRole('button', { name: 'Оформить отгрузку' }).click();
await page.waitForURL(/stock-documents\/\d+/); await ready(page);
if ((await page.locator('main').innerText()).includes(delivery)) throw new Error('услуга попала в отгрузку');
await page.getByRole('button', { name: 'Провести', exact: true }).click();
await page.waitForSelector('text=Проведён');
ok('отгрузка свитера без услуги');

await page.goto(orderUrl); await ready(page);
await page.getByRole('button', { name: 'Акт на услуги' }).click();
await page.waitForURL(/stock-documents\/\d+/); await ready(page);
const act = await page.locator('main').innerText();
if (!act.includes(delivery) || act.includes(sweater)) throw new Error('акт: ' + act.slice(0, 500));
ok('акт оказания услуг: только доставка');
await page.getByRole('button', { name: 'Провести', exact: true }).click();
await page.waitForSelector('text=Проведён');
ok('акт проведён');

await page.goto(orderUrl); await ready(page);
if (await page.getByRole('button', { name: 'Оформить отгрузку' }).count() || await page.getByRole('button', { name: 'Акт на услуги' }).count())
  throw new Error('заказ не закрыт отгрузкой и актом');
ok('заказ отгружен полностью: отгрузка + акт');

// API (D354): POST /sales-orders/{id}/service-act — для закрытого заказа понятный отказ 422, без токена — 401.
const orderId = orderUrl.split('/').pop();
await page.goto(`${BASE}/integrations`); await ready(page);
await page.locator('label:has-text("Название") input').fill('Акты ' + sfx);
for (const box of await page.locator('fieldset label.check input').all()) await box.check();
await page.getByRole('button', { name: 'Выпустить токен' }).click();
const secretBox = page.locator('input[readonly]');
await secretBox.waitFor();
const token = await secretBox.inputValue();
const api = page.request;
const closed = await api.post(`${BASE}/api/v1/sales-orders/${orderId}/service-act`, { headers: { Authorization: `Bearer ${token}` } });
if (closed.status() !== 422) throw new Error('API акта по закрытому заказу: ' + closed.status() + ' ' + (await closed.text()).slice(0, 300));
const unauth = await api.post(`${BASE}/api/v1/sales-orders/${orderId}/service-act`);
if (unauth.status() !== 401) throw new Error('API без токена: ' + unauth.status());
ok('API акта: закрытый заказ — 422 с причиной, без токена — 401');

await page.setViewportSize({ width: 390, height: 844 });
await page.reload(); await ready(page);
await assertNoHorizontalScroll(page, 'заказ с актом @390');
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
console.log('✓ ошибок JS: 0');
