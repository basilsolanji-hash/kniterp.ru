// D315, D316: EAN-13 в карточке, конструктор этикеток с живым предпросмотром, печать по шаблону (рулон и A4).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();

await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill('ЭТ-' + sfx);
await form.locator('label:has-text("Наименование") input').fill('Шапка для этикетки ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.locator('.flash').getByRole('link', { name: 'Шапка для этикетки ' + sfx }).click();
await page.waitForSelector('h2:has-text("Штрихкоды")'); await ready(page);
const itemId = page.url().split('/').pop();
await page.getByRole('button', { name: 'Создать EAN-13' }).click();
await page.waitForSelector('text=Штрихкод создан.');
const code = (await page.locator('td.barcode').first().innerText()).trim();
if (!/^2\d{12}$/.test(code)) throw new Error('штрихкод: ' + code);
ok('EAN-13 создан в карточке: ' + code);

// Конструктор: начать с образца, изменить, увидеть предпросмотр, сохранить.
await page.goto(`${BASE}/catalog/labels`); await ready(page);
await page.locator('label:has-text("Новый из образца") select').selectOption('price-58x40');
await page.getByRole('button', { name: 'Начать с образца' }).click();
await page.locator('label:has-text("Название") input').first().fill('Ценник ' + sfx);
await page.locator('label:has-text("Позиция для примера") input').fill('ЭТ-' + sfx);
await page.getByRole('button', { name: 'Показать' }).click();
await page.waitForSelector(`.label-zoom .item-label:has-text("Шапка для этикетки ${sfx}")`);
if (await page.locator('.label-zoom svg.barcode').count() !== 1) throw new Error('в предпросмотре нет штрихкода');
await page.locator('label:has-text("Добавить строку") select').selectOption('CustomText');
await page.getByRole('button', { name: 'Добавить', exact: true }).click();
await page.waitForSelector('.label-zoom .item-label:has-text("Сделано с любовью")');
await page.locator('label:has-text("Ширина, мм") input').fill('10'); // неверный размер — предпросмотр скрыт, подсказка
await page.waitForSelector('text=предпросмотр появится, когда шаблон будет корректным');
await page.locator('label:has-text("Ширина, мм") input').fill('58');
await page.waitForSelector('.label-zoom .item-label');
await page.getByRole('button', { name: 'Сохранить шаблон' }).click();
await page.waitForSelector('text=Шаблон сохранён.');
ok('конструктор: образец, своя строка, живой предпросмотр, сохранение');

// Печать: рулон и лист A4.
await page.goto(`${BASE}/catalog/labels/print?ids=${itemId}&copies=3`); await ready(page);
if (await page.locator('.label-sheet .item-label').count() !== 3) throw new Error('этикеток не 3');
if (!(await page.locator('.label-sheet').innerText()).includes('Сделано с любовью')) throw new Error('нет своей строки в печати');
await page.locator('select[name=template]').count() || (() => { throw new Error('нет выбора шаблона'); })();
ok('печать: 3 копии по сохранённому шаблону');

await page.goto(`${BASE}/catalog/labels`); await ready(page);
await page.locator('label:has-text("Новый из образца") select').selectOption('a4-70x37');
await page.getByRole('button', { name: 'Начать с образца' }).click();
await page.locator('label:has-text("Название") input').first().fill('Лист ' + sfx);
await page.getByRole('button', { name: 'Сохранить шаблон' }).click();
// Сообщение «Шаблон сохранён.» могло остаться от прошлого сохранения — ждём сам шаблон в списке.
await page.locator('.plain-list button', { hasText: 'Лист ' + sfx }).first().waitFor({ timeout: 10000 }).catch(() => {});
const a4 = await page.locator('.plain-list button', { hasText: 'Лист ' + sfx }).count();
if (a4 !== 1) console.log(await page.locator('.plain-list').innerText());
if (a4 !== 1) throw new Error('шаблон A4 не в списке');
ok('шаблон A4 сохранён');

await page.setViewportSize({ width: 390, height: 844 });
for (const u of [`${BASE}/catalog/labels`, `${BASE}/catalog/settings?tab=General`, `${BASE}/catalog/items/${itemId}`]) {
  await page.goto(u); await ready(page);
  await assertNoHorizontalScroll(page, u);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
