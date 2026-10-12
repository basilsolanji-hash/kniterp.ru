// D317–D320: значения группы, подсказка о похожих, массовое изменение, «Создать по образцу», скан в поиске.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();
const group = 'Авто ' + sfx;

// Группа и её значения по умолчанию.
await page.goto(`${BASE}/catalog/settings?tab=Groups`); await ready(page);
await page.locator('form.inline-form label:has-text("Название") input').fill(group);
await page.getByRole('button', { name: 'Добавить группу' }).click();
await page.waitForSelector(`td:has-text("${group}")`);
await page.locator('tr', { hasText: group }).getByRole('button', { name: 'По умолчанию' }).click();
await page.locator('label:has-text("Код ТН ВЭД") input').fill('6505009000');
await page.locator('label:has-text("Страна (код ОКСМ)") input').fill('643');
await page.getByRole('button', { name: 'Сохранить значения' }).click();
await page.waitForSelector('text=Значения по умолчанию группы сохранены.');
ok('значения по умолчанию группы сохранены');

// Новая позиция в группе: ТН ВЭД и страна подставились; при повторном названии — подсказка о похожих.
const create = async (code, name) => {
  await page.goto(`${BASE}/catalog`); await ready(page);
  await page.getByRole('button', { name: 'Добавить позицию' }).click();
  const form = page.locator('.panel form');
  await form.locator('label:has-text("Код") input').fill(code);
  await form.locator('label:has-text("Наименование") input').fill(name);
  await form.locator('label:has-text("Наименование") input').press('Tab');
  await form.locator('label:has-text("Группа") select').selectOption({ label: group });
  await page.waitForTimeout(400);
  return form;
};
let form = await create('АВ1-' + sfx, 'Шапка авто ' + sfx);
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.locator('.flash').getByRole('link', { name: 'Шапка авто ' + sfx }).click();
await page.waitForSelector('h2:has-text("Сведения")'); await ready(page);
if (await page.locator('label:has-text("Код ТН ВЭД") input').inputValue() !== '6505009000') throw new Error('ТН ВЭД не из группы');
const cardUrl = page.url();
ok('новая позиция получила ТН ВЭД и страну группы');

form = await create('АВ2-' + sfx, 'Шапка авто ' + sfx);
await page.waitForSelector('.hint.warn:has-text("Похожие позиции уже есть")');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
ok('подсказка о похожих появилась, сохранение не запрещено');

// Массовое изменение: обе позиции группы — маркировка «одежда».
await page.goto(`${BASE}/catalog`); await ready(page);
await page.locator('label:has-text("Поиск") input').fill('Шапка авто ' + sfx);
await page.getByRole('button', { name: 'Найти' }).click();
await page.waitForTimeout(600);
await page.locator('th.select-col input').check();
await page.waitForSelector('text=Выбрано: 2');
await page.getByRole('button', { name: 'Изменить выбранные' }).click();
await page.locator('.bulk-bar label:has-text("Маркировка") input').check();
await page.locator('.bulk-bar select[aria-label="Маркировка"]').selectOption('Clothes');
await page.locator('.bulk-bar').getByRole('button', { name: 'Применить' }).click();
await page.locator('.bulk-bar .confirm').getByRole('button', { name: 'Да' }).click();
await page.waitForSelector('text=Изменено: 2');
ok('массовое изменение: 2 позиции');

// «Создать по образцу» из карточки.
await page.goto(cardUrl); await ready(page);
await page.getByRole('button', { name: 'Создать по образцу' }).click();
await page.locator('label:has-text("Код новой позиции") input').fill('АВ3-' + sfx);
await page.getByRole('button', { name: 'Создать копию' }).click();
await page.waitForSelector(`h1:has-text("Шапка авто ${sfx} (копия)")`);
if (await page.locator('label:has-text("Код ТН ВЭД") input').inputValue() !== '6505009000') throw new Error('копия без ТН ВЭД');
ok('копия создана и открыта');

// Скан EAN-13 в поиске открывает карточку.
await page.getByRole('button', { name: 'Создать EAN-13' }).click();
await page.waitForSelector('text=Штрихкод создан.');
const code = (await page.locator('td.barcode').first().innerText()).trim();
await page.goto(`${BASE}/catalog`); await ready(page);
await page.locator('label:has-text("Поиск") input').fill(code);
await page.getByRole('button', { name: 'Найти' }).click();
await page.waitForSelector(`h1:has-text("Шапка авто ${sfx} (копия)")`);
ok('скан штрихкода в поиске открыл карточку');

await page.setViewportSize({ width: 390, height: 844 });
for (const u of [`${BASE}/catalog`, `${BASE}/catalog/settings?tab=Groups`, cardUrl]) {
  await page.goto(u); await ready(page);
  await assertNoHorizontalScroll(page, u);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
