// Этап 3, часть 2 (D307): упаковки в карточке — добавление, ошибка точности, удаление. 1440 и 390 px.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, plainText, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();

// Единица «коробка» — в справочнике единиц.
await page.goto(`${BASE}/catalog/settings`); await ready(page);
const unitsTab = page.getByRole('button', { name: /Единицы/ });
if (await unitsTab.count()) await unitsTab.first().click();

await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill('НС-' + sfx);
await form.locator('label:has-text("Наименование") input').fill('Носки ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.getByRole('link', { name: 'Носки ' + sfx }).click();
await page.waitForSelector('h2:has-text("Упаковки")'); await ready(page);

const packForm = page.locator('form:has(button:has-text("Сохранить упаковку"))');
await packForm.locator('select').selectOption({ label: 'Упаковка (упак)' });
await packForm.locator('label:has-text("Сколько") input').fill('1,5');
await packForm.getByRole('button', { name: 'Сохранить упаковку' }).click();
await page.waitForSelector('text=целое число основных единиц');
ok('дробное число штук в упаковке отклонено');
await packForm.locator('select').selectOption({ label: 'Упаковка (упак)' });
await packForm.locator('label:has-text("Сколько") input').fill('12');
await packForm.getByRole('button', { name: 'Сохранить упаковку' }).click();
await page.waitForSelector('text=Упаковка сохранена.');
const table = await plainText(page.locator('table:has(th:has-text("Основных единиц")) tbody'));
if (!table.includes('Упаковка (упак)') || !table.includes('12 шт')) throw new Error('упаковки: ' + table);
ok('упаковка «упак = 12 шт» добавлена');
const cardUrl = page.url();

await page.setViewportSize({ width: 390, height: 844 });
await page.goto(cardUrl); await ready(page);
await assertNoHorizontalScroll(page, cardUrl);
ok('390 px: без горизонтальной прокрутки');

await page.locator('table:has(th:has-text("Основных единиц")) button:has-text("Убрать")').click();
await page.locator('.confirm button.danger').click();
await page.waitForSelector('text=Упаковка убрана.');
ok('упаковка убрана');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
