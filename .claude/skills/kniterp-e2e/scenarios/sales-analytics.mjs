// Продажи, этапы 7–8 (D1107, D1160–D1171): аналитика продаж (разрезы, ABC, план-факт, воронка, Excel), план продаж,
// каналы-маркетплейсы (FBS/FBO) и загрузка отчёта Wildberries из Excel с предпросмотром. 1440 и 390 px, 0 ошибок JS.
// Не запускался агентом E (приложение поднимает координатор). Скриншоты — в $SHOTS (если задан).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const SHOTS = process.env.SHOTS;
const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const shot = async (name) => { if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: true }); };
const sfx = Date.now().toString(36).slice(-5).toUpperCase();

// Аналитика: все четыре вкладки открываются, Excel — ссылка на книгу.
await page.goto(`${BASE}/reports/sales`); await ready(page);
await page.waitForSelector('h1:has-text("Аналитика продаж")');
for (const tab of ['ABC-анализ', 'План-факт', 'Воронка', 'Выручка и маржа']) {
  await page.getByRole('tab', { name: tab }).click(); await ready(page);
  await shot('an-' + tab);
}
await page.locator('form.sx-form label:has-text("Разрез") select').selectOption({ label: 'Каналы' });
await page.getByRole('button', { name: 'Показать' }).click(); await ready(page);
const [xlsx] = await Promise.all([page.waitForEvent('download'), page.getByRole('link', { name: 'Excel' }).click()]);
if (!xlsx.suggestedFilename().startsWith('analitika-prodazh-')) throw new Error('Excel: ' + xlsx.suggestedFilename());
const analyticsFile = `${process.env.E2E_DIR ?? '/tmp/kniterp-e2e'}/analytics-${sfx}.xlsx`;
await xlsx.saveAs(analyticsFile);
ok('аналитика: вкладки и Excel');

// План продаж: ввод плана Владельцу.
await page.goto(`${BASE}/sales-plan`); await ready(page);
await page.locator('table.sp-table tbody tr').first().locator('input').fill('150000');
await page.locator('table.sp-table tbody tr').first().getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=План сохранён.');
await shot('plan');
ok('план продаж');

// Канал продаж → маркетплейс Wildberries, FBO.
await page.goto(`${BASE}/sales-settings?section=channels`); await ready(page);
await page.locator('form:has(button:has-text("Добавить")) input').first().fill('WB ' + sfx);
await page.locator('form:has(button:has-text("Добавить"))').getByRole('button', { name: 'Добавить' }).first().click();
await page.waitForSelector(`text=WB ${sfx}`);
await page.goto(`${BASE}/marketplace-reports`); await ready(page);
await page.getByRole('tab', { name: 'Каналы' }).click(); await ready(page);
const row = page.locator(`tr:has(td:has-text("WB ${sfx}"))`);
await row.locator(`select[aria-label="Площадка канала WB ${sfx}"]`).selectOption('Wildberries');
await row.locator(`select[aria-label="Схема канала WB ${sfx}"]`).selectOption('Fbo');
await row.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Свойства канала сохранены.');
ok('канал WB, FBO');

// Загрузка: файл не того формата (книга аналитики) — понятная ошибка о строке заголовков; разбор настоящих отчётов WB и Ozon
// проверяют интеграционные тесты MarketplaceReportTests (файлы собираются там через ISpreadsheetFormat.Write).
await page.getByRole('tab', { name: 'Отчёты' }).click(); await ready(page);
await page.locator('form.sx-form label:has-text("Канал") select').selectOption({ label: `WB ${sfx} (Wildberries)` });
await page.locator('input[type=file]').setInputFiles(analyticsFile);
await page.waitForSelector('text=не найдена строка заголовков');
await shot('mp-wrong-file');
await page.getByRole('tab', { name: 'Столбцы файла' }).click(); await ready(page);
await page.waitForSelector('text=Заголовки столбцов');
await shot('mp-columns');
const cardUrl = `${BASE}/marketplace-reports`;
ok('отчёты маркетплейсов: ошибка формата, столбцы файла');

await page.setViewportSize({ width: 390, height: 844 });
for (const url of [`${BASE}/reports/sales`, `${BASE}/sales-plan`, `${BASE}/marketplace-reports`, cardUrl, `${BASE}/`]) {
  await page.goto(url); await ready(page);
  await assertNoHorizontalScroll(page, url);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
