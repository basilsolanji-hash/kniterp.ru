// D352 (Р2): комплект в заказе покупателя отгружается компонентами — в документе отгрузки комплект заголовком над компонентами,
// после проведения заказ отгружен. Данные создаются сценарием. 1440 и 390 px.
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

const [hat, scarf, set] = ['КШ-' + sfx, 'КФ-' + sfx, 'КН-' + sfx];
await createItem(hat, 'Шапка ' + sfx, 'Finished');
await createItem(scarf, 'Шарф ' + sfx, 'Finished');
await createItem(set, 'Набор ' + sfx, 'Bundle');
await page.locator('.flash').getByRole('link', { name: 'Набор ' + sfx }).click();
await page.waitForSelector('text=Состав комплекта'); await ready(page);
const comp = page.locator('form.inline-form:has(button:has-text("Добавить в состав"))');
for (const code of [hat, scarf]) {
  await comp.locator('input[list=bundle-options]').fill(code);
  await comp.locator('label:has-text("Количество в комплекте") input').fill('1');
  await comp.getByRole('button', { name: 'Добавить в состав' }).click();
  await page.locator('table:has(th:has-text("Компонент")) tbody a', { hasText: code }).first().waitFor();
}
ok('комплект «шапка + шарф»');

// Склад и поступление компонентов.
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
for (const code of [hat, scarf]) {
  const line = page.locator('form.inline-form:has(label:has-text("Количество"))').first();
  await line.locator('label:has-text("Позиция") input').fill(code);
  await line.locator('label:has-text("Количество") input').fill('5');
  await line.getByRole('button', { name: 'Добавить' }).click();
  await page.waitForSelector(`td:has-text("${code}")`);
}
await page.getByRole('button', { name: 'Провести', exact: true }).click();
await page.waitForSelector('text=Проведён');
ok('поступление 5 шапок и 5 шарфов');

// Покупатель и заказ с комплектом.
await page.goto(`${BASE}/counterparties`); await ready(page);
await page.getByRole('button', { name: 'Добавить контрагента' }).click();
await page.locator('label:has-text("Наименование") input').first().fill('Магазин ' + sfx);
await page.locator('label.inline:has-text("Покупатель") input').check();
await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
await page.waitForSelector(`td:has-text("Магазин ${sfx}")`);

await page.goto(`${BASE}/sales-orders`); await ready(page);
await page.getByRole('button', { name: 'Новый заказ' }).click();
const orderForm = page.locator('.panel form');
await orderForm.waitFor();
await orderForm.locator('label:has-text("Покупатель") select').selectOption({ label: 'Магазин ' + sfx });
await orderForm.locator('label:has-text("Склад") select').selectOption({ label: 'Склад ' + sfx });
await orderForm.getByRole('button', { name: 'Создать черновик' }).click();
await page.waitForURL(/sales-orders\/\d+/); await ready(page);
const lineForm = page.locator('form.inline-form:has(label:has-text("Количество"))').first();
await lineForm.locator('label:has-text("Позиция") input').fill(set);
await lineForm.locator('label:has-text("Позиция") input').blur();
await lineForm.locator('label:has-text("Количество") input').fill('2');
await lineForm.locator('label:has-text("Цена") input').fill('1500');
await lineForm.getByRole('button', { name: 'Добавить или изменить' }).click();
await page.waitForSelector(`td:has-text("${set}")`);
await page.getByRole('button', { name: 'Подтвердить заказ' }).click();
await page.waitForSelector('text=Заказ подтверждён');
ok('заказ на 2 набора подтверждён');

await page.getByRole('button', { name: 'Оформить отгрузку' }).click();
await page.waitForURL(/stock-documents\/\d+/); await ready(page);
const doc = await page.locator('main').innerText();
if (!doc.includes('Набор ' + sfx) || !doc.includes('комплект — компоненты ниже')) throw new Error('нет заголовка комплекта: ' + doc.slice(0, 600));
if (!doc.includes('↳ ' + hat) || !doc.includes('↳ ' + scarf)) throw new Error('нет компонентов: ' + doc.slice(0, 600));
ok('отгрузка: комплект заголовком, компоненты ↳ ниже');
await page.getByRole('button', { name: 'Провести', exact: true }).click();
await page.waitForSelector('text=Проведён');
ok('отгрузка проведена');

await page.setViewportSize({ width: 390, height: 844 });
await page.reload(); await ready(page);
await assertNoHorizontalScroll(page, 'отгрузка с комплектом @390');
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
console.log('✓ ошибок JS: 0');
