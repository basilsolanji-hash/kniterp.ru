// D330: «Проверить связь по разделам» на экране МойСклад — таблица разделов с ответом МойСклад (токен — вымышленный, из теста).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
await page.goto(`${BASE}/exchange/moysklad`); await ready(page);
const tokenInput = page.locator('label:has-text("Токен МойСклад") input');
if (await tokenInput.count()) await tokenInput.fill('test-token-0000000000000000abcd');
await page.getByRole('button', { name: 'Проверить связь по разделам' }).click();
await page.locator('th:has-text("Раздел")').waitFor({ timeout: 180000 });
const rows = await page.locator('table:has(th:has-text("Раздел")) tbody tr').allInnerTexts();
console.log(rows.join('\n'));
if (rows.length !== 10) throw new Error('разделов: ' + rows.length);
if (rows.some(r => r.includes('test-token'))) throw new Error('токен в тексте ошибки');
console.log('✓ таблица разделов показана, токена в тексте нет');
await page.screenshot({ path: `${process.env.E2E_DIR}/shots/probe.png` });
await page.setViewportSize({ width: 390, height: 844 });
await assertNoHorizontalScroll(page, 'МойСклад @390');
console.log('✓ 390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
console.log('✓ ошибок JS: 0');
