// D336: доп. поле «Контрагент» в карточке — поиск по названию; Enter в поиске не сохраняет карточку.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();
const itemId = process.env.E2E_ITEM_ID ?? '1';
const supplier = process.env.E2E_SUPPLIER ?? 'Пряжа CPGYR';

await page.goto(`${BASE}/catalog/settings?tab=Fields`); await ready(page);
const fieldForm = page.locator('form.inline-form:has(button:has-text("Добавить поле"))');
await fieldForm.locator('label:has-text("Название") input').fill('Производитель ' + sfx);
await fieldForm.locator('label:has-text("Тип") select').selectOption('Counterparty');
await fieldForm.getByRole('button', { name: 'Добавить поле' }).click();
await page.waitForSelector(`td:has-text("Производитель ${sfx}")`);
ok('поле «Контрагент» создано');

await page.goto(`${BASE}/catalog/items/${itemId}`); await ready(page);
const label = page.locator(`label:has-text("Производитель ${sfx}")`);
const search = label.locator('input[aria-label="Найти контрагента"]');
await search.fill(supplier);
await search.press('Enter');
await label.locator('select option', { hasText: supplier }).first().waitFor({ state: 'attached' });
if (await page.locator('.flash:not(.error)').count() > 0) throw new Error('Enter в поиске сохранил карточку: ' + await page.locator('.flash').first().innerText());
ok('поиск по названию нашёл контрагента, Enter не сохранил карточку');

const value = await label.locator('select option', { hasText: supplier }).first().getAttribute('value');
await label.locator('select').selectOption(value);
await page.getByRole('button', { name: 'Сохранить сведения' }).click();
await page.waitForSelector('.flash:not(.error)');
await page.goto(`${BASE}/catalog/items/${itemId}`); await ready(page);
const selected = await page.locator(`label:has-text("Производитель ${sfx}") select`).inputValue();
if (selected !== value) throw new Error('не сохранилось: ' + selected);
ok('контрагент сохранён в карточке');

await page.setViewportSize({ width: 390, height: 844 });
await page.goto(`${BASE}/catalog/items/${itemId}`); await ready(page);
await assertNoHorizontalScroll(page, 'карточка @390');
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
console.log('✓ ошибок JS: 0');
