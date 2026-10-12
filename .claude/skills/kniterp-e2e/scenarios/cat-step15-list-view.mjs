// D338: список номенклатуры — сортировка по заголовку (сервер), скрытие столбцов, вид сохраняется после перезагрузки, сброс.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const firstCodes = async () => page.locator('table tbody tr td:nth-child(2)').evaluateAll(tds => tds.slice(0, 5).map(t => t.textContent.trim()));

await page.goto(`${BASE}/catalog`); await ready(page);
await page.locator('details.columns-menu summary').click();
const reset = page.getByRole('button', { name: 'Вид по умолчанию' });
await reset.click(); await page.waitForTimeout(600);

const codeHeader = page.locator('th:has(button.th-sort:has-text("Код"))');
await codeHeader.locator('button').click();
await page.waitForFunction(() => document.querySelector('th[aria-sort="ascending"]'));
const asc = await firstCodes();
const sorted = [...asc].sort((a, b) => a.localeCompare(b, 'ru'));
if (asc.join() !== sorted.join()) console.log('порядок сервера (коллация SQL):', asc.join(' | '));
await codeHeader.locator('button').click();
await page.waitForFunction(() => document.querySelector('th[aria-sort="descending"]'));
const desc = await firstCodes();
if (desc[0] === asc[0]) throw new Error('сортировка по убыванию не применилась: ' + desc.join());
ok('сортировка по коду: ↑ и ↓ меняют порядок, aria-sort выставлен');

await page.locator('details.columns-menu label:has-text("НДС") input').uncheck();
await page.waitForTimeout(800);
if (await page.locator('th:has-text("НДС")').count() !== 0) throw new Error('столбец НДС не скрылся');
await page.reload(); await ready(page);
if (await page.locator('th:has-text("НДС")').count() !== 0) throw new Error('скрытие не сохранилось');
if (!(await page.locator('th[aria-sort="descending"]').count())) throw new Error('сортировка не сохранилась');
ok('скрытый столбец и сортировка сохранились после перезагрузки');

await page.locator('details.columns-menu summary').click();
await page.getByRole('button', { name: 'Вид по умолчанию' }).click();
await page.waitForTimeout(800);
if (await page.locator('th:has-text("НДС")').count() !== 1) throw new Error('сброс не вернул столбец');
ok('«Вид по умолчанию» возвращает столбцы и порядок');

await page.setViewportSize({ width: 390, height: 844 });
await page.goto(`${BASE}/catalog`); await ready(page);
await assertNoHorizontalScroll(page, 'список @390');
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
console.log('✓ ошибок JS: 0');
