// Этап 3, часть 3 (D308, D309): поставщик в карточке позиции; выгрузка Excel с листом «Состав комплектов» и загрузка обратно.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';
import fs from 'node:fs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();

// Поставщик — контрагент с ролью «Поставщик».
await page.goto(`${BASE}/counterparties`); await ready(page);
await page.getByRole('button', { name: 'Добавить контрагента' }).click();
await page.locator('label:has-text("Наименование") input').first().fill('ООО «Пряжа ' + sfx + '»');
await page.locator('label.inline:has-text("Поставщик") input').check();
await page.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForTimeout(800);

await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill('ВР-' + sfx);
await form.locator('label:has-text("Наименование") input').fill('Варежки ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.getByRole('link', { name: 'Варежки ' + sfx }).click();
await page.waitForSelector('h2:has-text("Поставщики")'); await ready(page);

const sup = page.locator('form:has(button:has-text("Сохранить поставщика"))');
await sup.locator('select').selectOption({ label: 'ООО «Пряжа ' + sfx + '»' });
await sup.locator('label:has-text("Его артикул") input').fill('M-' + sfx);
await sup.getByRole('button', { name: 'Сохранить поставщика' }).click();
await page.waitForSelector('text=Поставщик сохранён.');
const supTable = await page.locator('table:has(th:has-text("Поставщик")) tbody').innerText();
if (!supTable.includes('M-' + sfx) || !supTable.includes('основной')) throw new Error('поставщики: ' + supTable);
ok('поставщик добавлен и стал основным');
const cardUrl = page.url();

// Выгрузка → та же загрузка: «без изменений», лист «Состав комплектов» прочитан.
await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: /Excel: выгрузка и загрузка/ }).click();
const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('link', { name: 'Выгрузить номенклатуру' }).click()]);
const path = `${process.env.E2E_DIR ?? '/tmp/kniterp-e2e'}/catalog-${sfx}.xlsx`;
await download.saveAs(path);
if (fs.statSync(path).size < 1000) throw new Error('выгрузка пустая');
await page.locator('input[type=file]').setInputFiles(path);
await page.waitForSelector('text=Без изменений:');
const totals = await page.locator('.totals').first().innerText();
if (!/С ошибками:\s*0/.test(totals)) throw new Error('протокол загрузки: ' + totals);
ok('выгрузка загружается обратно без ошибок');

await page.setViewportSize({ width: 390, height: 844 });
for (const url of [cardUrl, `${BASE}/catalog`]) {
  await page.goto(url); await ready(page);
  await assertNoHorizontalScroll(page, url);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
