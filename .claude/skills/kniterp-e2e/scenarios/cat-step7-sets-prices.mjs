// D321–D323: готовые наборы значений, модификации выбором набора, автоцена от закупки, страница прайс-листа.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, plainText, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();

await page.goto(`${BASE}/catalog/settings?tab=Characteristics`); await ready(page);
await page.getByRole('button', { name: 'Добавить готовые' }).click();
await page.waitForSelector('td:text-is("Женские 42–56")');
ok('готовые наборы значений добавлены');

// Позиция и модификации из набора.
await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill('СВ-' + sfx);
await form.locator('label:has-text("Наименование") input').fill('Свитер ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.locator('.flash').getByRole('link', { name: 'Свитер ' + sfx }).click();
await page.waitForSelector('h2:has-text("Модификации")'); await ready(page);
const sizeLabel = page.locator('form:has(button:has-text("Создать модификации")) label', { hasText: 'Размер' });
await sizeLabel.locator('select').selectOption({ label: 'Женские 42–56' });
for (let i = 0; i < 20 && await sizeLabel.locator('input').inputValue() !== '42, 44, 46, 48, 50, 52, 54, 56'; i++) await page.waitForTimeout(150);
if (await sizeLabel.locator('input').inputValue() !== '42, 44, 46, 48, 50, 52, 54, 56') throw new Error('набор не подставился: ' + await sizeLabel.locator('input').inputValue());
await page.getByRole('button', { name: 'Создать модификации' }).click();
await page.waitForSelector('text=Создано модификаций: 8');
ok('8 модификаций из набора «Женские 42–56»');
const cardUrl = page.url();

// Автоцена: розница = закупка × 2, окончание на 9.
await page.goto(`${BASE}/catalog/settings?tab=Prices`); await ready(page);
const row = page.locator('table:has(th:has-text("Коэффициент")) tbody tr').first();
await row.locator('input[type=number]').fill('2');
await row.locator('select').selectOption('Nine');
await row.locator('input[type=checkbox]').check();
await row.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Правило сохранено');
await page.goto(cardUrl); await ready(page);
await page.locator('label:has-text("Закупочная цена") input').fill('1000');
await page.getByRole('button', { name: 'Сохранить сведения' }).click();
await page.waitForTimeout(800);
await page.reload(); await ready(page);
const priceInput = page.locator('input[inputmode="decimal"][size="12"]').first();
const price = (await priceInput.inputValue()).replace(/\s| /g, '');
if (!/^1999([.,]00?)?$/.test(price)) throw new Error('автоцена: ' + price);
ok('автоцена 1000 × 2 → 1 999');

// Выключить правило — чтобы другие сценарии не зависели от него.
await page.goto(`${BASE}/catalog/settings?tab=Prices`); await ready(page);
const row2 = page.locator('table:has(th:has-text("Коэффициент")) tbody tr').first();
await row2.locator('input[type=checkbox]').uncheck();
await row2.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Правило сохранено и выключено.');

await page.goto(`${BASE}/catalog/price-list`); await ready(page);
await page.waitForSelector('h1:has-text("Прайс-лист поставщика")');
ok('страница прайс-листа открывается');

await page.setViewportSize({ width: 390, height: 844 });
for (const u of [`${BASE}/catalog/settings?tab=Characteristics`, `${BASE}/catalog/settings?tab=Prices`, `${BASE}/catalog/price-list`, cardUrl]) {
  await page.goto(u); await ready(page);
  await assertNoHorizontalScroll(page, u);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
