// Этап 3, часть 1 (D305, D306): торговые сведения и маркировка в карточке, история цен, массовая переоценка. 1440 и 390 px.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
// Повторяемость на одной базе: свой код и случайный GTIN-14 с верной контрольной цифрой.
const sfx = Date.now().toString(36).slice(-5).toUpperCase();
const body = '0460' + String(Math.floor(Math.random() * 1e9)).padStart(9, '0');
let sum = 0; for (let i = body.length - 1, w = 3; i >= 0; i--, w = 4 - w) sum += Number(body[i]) * w;
const gtin = body + ((10 - sum % 10) % 10);
const wrongGtin = body + ((10 - sum % 10 + 1) % 10);
const text = async (loc) => (await loc.innerText()).replace(/[\u00a0\u202f]/g, ' ');

await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill('ПЛ-' + sfx);
await form.locator('label:has-text("Наименование") input').fill('Плед вязаный ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.locator('.flash').getByRole('link', { name: 'Плед вязаный ' + sfx }).click(); // ссылка на новую позицию — в сообщении (список листается страницами)
await page.waitForSelector('text=Торговля и маркировка'); await ready(page);

// Неверный GTIN — понятная ошибка; верный EAN-13 становится GTIN-14.
const trade = page.locator('form:has(button:has-text("Сохранить торговые сведения"))');
await trade.locator('label:has-text("Маркируемый товар") input').check();
await trade.locator('label:has-text("Вид продукции") select').selectOption('Linens');
await trade.locator('label:has-text("GTIN") input').fill(wrongGtin.slice(1));
await trade.getByRole('button', { name: 'Сохранить торговые сведения' }).click();
await page.waitForSelector('text=неверный');
ok('неверный GTIN отклонён');
await trade.locator('label:has-text("GTIN") input').fill(gtin.slice(1));
await trade.locator('label:has-text("Минимальная цена продажи") input').fill('2000');
await trade.getByRole('button', { name: 'Сохранить торговые сведения' }).click();
await page.waitForSelector('text=Торговые сведения сохранены.');
if ((await trade.locator('label:has-text("GTIN") input').inputValue()) !== gtin) throw new Error('GTIN не приведён к 14 цифрам');
ok('маркировка, GTIN-14 и минимальная цена сохранены');

// Цена 2 100 → история цен.
const prices = page.locator('form:has(button:has-text("Сохранить цены"))');
await prices.locator('td.num input').first().fill('2100');
await prices.getByRole('button', { name: 'Сохранить цены' }).click();
await page.waitForSelector('text=Цены сохранены.');
await page.locator('details.history summary').click();
if (!(await text(page.locator('details.history'))).includes('2 100')) throw new Error('история цен без новой цены');
ok('изменение цены попало в историю');
const cardUrl = page.url();

// Переоценка −10 %: 1 890 ниже минимальной 2 000 — предупреждение; применение и журнал.
await page.goto(`${BASE}/catalog/revaluation`); await ready(page);
await page.locator('label:has-text("Процент") input').fill('-10');
await page.locator('label:has-text("Поиск") input').fill('ПЛ-' + sfx);
await page.getByRole('button', { name: 'Предпросмотр' }).click();
await page.waitForSelector('text=Изменится:');
const preview = await text(page.locator('main'));
if (!preview.includes('1 890') || !preview.includes('ниже минимальной')) throw new Error('предпросмотр: ' + preview.slice(0, 600));
ok('предпросмотр: новая цена и предупреждение о минимальной цене');
await page.getByRole('button', { name: 'Применить переоценку' }).click();
await page.locator('.confirm button.danger').click();
await page.waitForSelector('text=/Цены изменены: \\d+/');
if (!(await page.locator('table:has(th:has-text("Правило")) tbody').innerText()).includes('10 % к текущей цене')) throw new Error('журнал переоценок пуст');
ok('переоценка применена, запись в журнале');

await page.setViewportSize({ width: 390, height: 844 });
for (const url of [cardUrl, `${BASE}/catalog/revaluation`]) {
  await page.goto(url); await ready(page);
  await assertNoHorizontalScroll(page, url);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
