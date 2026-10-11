// Этап 1 каталога (D300): услуга в номенклатуре — создание, отбор «Услуги», карточка без складских блоков. 1440 и 390 px.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);

async function createItem(code, name, type) {
  await page.goto(`${BASE}/catalog`); await ready(page);
  await page.getByRole('button', { name: 'Добавить позицию' }).click();
  const form = page.locator('.panel form');
  await form.locator('label:has-text("Код") input').fill(code);
  await form.locator('label:has-text("Наименование") input').fill(name);
  await form.locator('label:has-text("Тип") select').selectOption(type);
  await page.waitForTimeout(300);
  return form;
}

let form = await createItem('СВ-1', 'Свитер', 'Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');

form = await createItem('УС-1', 'Доставка по городу', 'Service');
const unitText = await form.locator('label:has-text("Единица") select option:checked').innerText();
if (!unitText.includes('усл. ед')) throw new Error('единица услуги по умолчанию: ' + unitText);
ok('у новой услуги единица «усл. ед»');
if (!(await form.innerText()).includes('Ставка НДС (обязательна для услуги)')) throw new Error('нет подсказки про НДС');
await form.locator('label:has-text("Ставка НДС") select').selectOption({ index: 0 }); // «не указана»
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=У услуги ставка НДС обязательна');
ok('без ставки НДС услуга не сохраняется');
await form.locator('label:has-text("Ставка НДС") select').selectOption({ index: 1 });
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
ok('услуга создана');

await page.locator('form.inline-form label:has-text("Вид") select').selectOption({ label: 'Услуги' });
await page.getByRole('button', { name: 'Найти' }).click();
await page.waitForTimeout(500);
const rows = await page.locator('table tbody tr').allInnerTexts();
if (rows.length !== 1 || !rows[0].includes('Доставка по городу')) throw new Error('отбор «Услуги»: ' + JSON.stringify(rows));
ok('отбор «Услуги» показывает только услугу');

await page.getByRole('link', { name: 'Доставка по городу' }).click();
await page.waitForSelector('text=Предмет расчёта (чек)'); await ready(page);
const card = await page.locator('main').innerText();
for (const absent of ['Остаток на всех складах', 'Вес, кг', 'Неснижаемый остаток', 'Модификации']) {
  if (card.includes(absent)) throw new Error('в карточке услуги есть «' + absent + '»');
}
if (!card.includes('не хранится на складе')) throw new Error('нет пометки «не хранится на складе»');
ok('карточка услуги без складских блоков, предмет расчёта — услуга');
const cardUrl = page.url();

await page.setViewportSize({ width: 390, height: 844 });
for (const url of [`${BASE}/catalog`, cardUrl]) {
  await page.goto(url); await ready(page);
  await assertNoHorizontalScroll(page, url);
}
ok('390 px: без горизонтальной прокрутки');

await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
