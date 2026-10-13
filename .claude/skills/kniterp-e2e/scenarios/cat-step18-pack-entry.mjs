// D353 (Р4): ввод количества в упаковках в строке складского документа — «3 упак → в количество» = 36 шт; 1440 и 390 px.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();
const code = 'УП-' + sfx;

// Позиция с упаковкой «упак = 12 шт».
await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill(code);
await form.locator('label:has-text("Наименование") input').fill('Носки упаковками ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.locator('.flash').getByRole('link', { name: 'Носки упаковками ' + sfx }).click();
await page.waitForSelector('h2:has-text("Упаковки")'); await ready(page);
const packForm = page.locator('form:has(button:has-text("Сохранить упаковку"))');
await packForm.locator('select').selectOption({ label: 'Упаковка (упак)' });
await packForm.locator('label:has-text("Сколько") input').fill('12');
await packForm.getByRole('button', { name: 'Сохранить упаковку' }).click();
await page.waitForSelector('text=Упаковка сохранена.');
ok('позиция с упаковкой «упак = 12 шт»');

// Черновик поступления.
await page.goto(`${BASE}/warehouses`); await ready(page);
await page.locator('label:has-text("Новый склад") input').fill('Склад ' + sfx);
await page.locator('form:has(label:has-text("Новый склад"))').getByRole('button', { name: 'Добавить' }).click();
await page.waitForSelector(`text=Склад ${sfx}`);
await page.goto(`${BASE}/stock-documents?kind=1`); await ready(page);
await page.locator('.page-head .actions button').first().click();
const head = page.locator('.panel form');
await head.waitFor();
await head.locator('label:has-text("Склад") select').first().selectOption({ label: 'Склад ' + sfx }).catch(() => {});
await head.locator('label:has-text("Причина") select').selectOption({ label: 'Закупка у поставщика' });
await head.getByRole('button', { name: 'Создать черновик' }).click();
await page.waitForURL(/stock-documents\/\d+/); await ready(page);

const line = page.locator('form.inline-form:has(label:has-text("Количество"))').first();
await line.locator('label:has-text("Позиция") input').fill(code);
await line.locator('label:has-text("Позиция") input').blur();
const packs = line.locator('label:has-text("Упаковок")');
await packs.waitFor();
await packs.locator('input').fill('3');
await packs.getByRole('button', { name: 'в количество' }).click();
await page.waitForFunction(() => document.querySelector('form.inline-form label input[inputmode=decimal]')?.value === '36');
ok('3 упак × 12 = 36 в поле «Количество»');
await line.getByRole('button', { name: 'Добавить' }).click();
await page.waitForSelector(`tr:has(td:has-text("${code}")) td:has-text("36")`);
ok('строка документа: 36 шт');

await page.setViewportSize({ width: 390, height: 844 });
await page.reload(); await ready(page);
await assertNoHorizontalScroll(page, 'документ с вводом упаковок @390');
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
console.log('✓ ошибок JS: 0');
