// D328: характеристика с типом «Число», доп. поле «Ссылка» — в настройках и в карточке (javascript: не принимается).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();
const itemId = process.env.E2E_ITEM_ID ?? '1';

await page.goto(`${BASE}/catalog/settings?tab=Characteristics`); await ready(page);
const charForm = page.locator('form.inline-form:has(label:has-text("Значения"))').first();
await charForm.locator('label:has-text("Название") input').fill('Длина ' + sfx);
await charForm.locator('label:has-text("Значения") select').selectOption('Number');
await charForm.getByRole('button', { name: 'Добавить' }).click();
await page.waitForSelector(`tr:has-text("Длина ${sfx}") td:has-text("Число")`);
ok('характеристика с типом «Число» создана');

await page.goto(`${BASE}/catalog/settings?tab=Fields`); await ready(page);
const fieldForm = page.locator('form.inline-form:has(button:has-text("Добавить поле"))');
await fieldForm.locator('label:has-text("Название") input').fill('Сайт ' + sfx);
await fieldForm.locator('label:has-text("Тип") select').selectOption('Link');
await fieldForm.getByRole('button', { name: 'Добавить поле' }).click();
await page.waitForSelector(`td:has-text("Сайт ${sfx}")`);
ok('доп. поле «Ссылка» создано');

await page.goto(`${BASE}/catalog/items/${itemId}`); await ready(page);
const link = page.locator(`label:has-text("Сайт ${sfx}") input[type=url]`);
await link.fill('javascript:alert(1)');
await page.getByRole('button', { name: 'Сохранить сведения' }).click();
await page.waitForSelector('text=укажите ссылку, начинающуюся с https://');
ok('javascript: отклонён понятным сообщением');
await page.goto(`${BASE}/catalog/items/${itemId}`); await ready(page);
await page.locator(`label:has-text("Сайт ${sfx}") input[type=url]`).fill('https://shop.example/item');
await page.getByRole('button', { name: 'Сохранить сведения' }).click();
await page.locator(`label:has-text("Сайт ${sfx}") a[href="https://shop.example/item"]`).waitFor();
ok('ссылка сохранена, рядом — «открыть»');

await page.setViewportSize({ width: 390, height: 844 });
for (const p of [`/catalog/items/${itemId}`, '/catalog/settings?tab=Characteristics', '/exchange/moysklad']) {
  await page.goto(BASE + p); await ready(page);
  await assertNoHorizontalScroll(page, p + ' @390');
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
console.log('✓ ошибок JS: 0');
