// Этап 2 каталога (D302): комплект — создание, состав в карточке, «Можно собрать», отбор «Комплекты». 1440 и 390 px.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase(); // повторяемость на одной базе

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

await createItem('Ш-' + sfx, 'Шапка ' + sfx, 'Finished');
await createItem('ШФ-' + sfx, 'Шарф ' + sfx, 'Finished');
await createItem('НБ-' + sfx, 'Набор шапка + шарф ' + sfx, 'Bundle');
ok('товары и комплект созданы');

await page.locator('form.inline-form label:has-text("Вид") select').selectOption({ label: 'Комплекты' });
await page.locator('form.inline-form label:has-text("Поиск") input').fill(sfx);
await page.getByRole('button', { name: 'Найти' }).click();
await page.waitForTimeout(500);
const rows = await page.locator('table tbody tr').allInnerTexts();
if (rows.length !== 1 || !rows[0].includes('Набор шапка + шарф ' + sfx)) throw new Error('отбор «Комплекты»: ' + JSON.stringify(rows));
ok('отбор «Комплекты»');

await page.getByRole('link', { name: 'Набор шапка + шарф ' + sfx }).click();
await page.waitForSelector('text=Состав комплекта'); await ready(page);
if (!(await page.locator('main').innerText()).includes('Состав не задан')) throw new Error('пустой состав не показан');
const form = page.locator('form.inline-form:has(button:has-text("Добавить в состав"))');
for (const [code, qty] of [['Ш-' + sfx, '1'], ['ШФ-' + sfx, '1']]) {
  await form.locator('input[list=bundle-options]').fill(code);
  await form.locator('label:has-text("Количество в комплекте") input').fill(qty);
  await form.getByRole('button', { name: 'Добавить в состав' }).click();
  // Ждём строку именно этого компонента: сообщение «Состав сохранён.» остаётся от прошлого добавления.
  await page.locator('table:has(th:has-text("Компонент")) tbody a', { hasText: code }).first().waitFor();
}
const table = await page.locator('table:has(th:has-text("Компонент")) tbody').innerText();
if (!table.includes('Шапка ' + sfx) || !table.includes('Шарф ' + sfx)) throw new Error('состав: ' + table);
const totals = await page.locator('.totals').innerText();
if (!totals.includes('Можно собрать:') || !totals.includes('0')) throw new Error('доступность: ' + totals);
ok('состав из двух компонентов, «Можно собрать: 0» без остатков');

await form.locator('input[list=bundle-options]').fill('НБ-' + sfx);
await form.getByRole('button', { name: 'Добавить в состав' }).click();
await page.waitForSelector('text=Выберите компонент из списка');
ok('комплект в свой состав не добавляется');

const card = await page.locator('main').innerText();
if (card.includes('Остаток на всех складах') || card.includes('Неснижаемый остаток')) throw new Error('у комплекта складские поля');
ok('у комплекта нет остатка и неснижаемого остатка');
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
