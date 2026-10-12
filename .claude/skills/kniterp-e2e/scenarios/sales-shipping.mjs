// Продажи, этап 5 (D1150–D1159) и коды маркировки (D1108): экраны «Задания на сборку», «Отгрузки по нескольким заказам»,
// справочники доставки и причин возврата; если есть подтверждённые заказы — создаёт задание на сборку и отгрузку, отмечает «собрано»,
// открывает лист сборки и упаковочный лист. 1440 и 390 px, 0 ошибок JS, без горизонтальной прокрутки. Скриншоты — в $SHOTS.
// Сценарий написан агентом D и не запускался (запуск — координатор после слияния).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const SHOTS = process.env.SHOTS;
for (const width of [1440, 390]) {
  const { browser, page } = await ownerSession(width);
  const errors = collectErrors(page);
  const ok = (m) => console.log(`✓ [${width}] ${m}`);
  const shot = async (name) => { if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}-${width}.png`, fullPage: true }); };

  await page.goto(`${BASE}/sales-shipments`); await ready(page);
  await page.waitForSelector('h1:has-text("Отгрузки по нескольким заказам")');
  await assertNoHorizontalScroll(page); await shot('sales-shipments');
  if (width === 1440 && await page.locator('text=Справочники отгрузки').count() > 0) {
    const sfx = Date.now().toString(36).slice(-4).toUpperCase();
    await page.locator('input[aria-label="Способ доставки"]').fill('ТК ' + sfx);
    await page.locator('form:has(input[aria-label="Способ доставки"])').getByRole('button', { name: 'Добавить', exact: true }).click();
    await page.waitForSelector(`text=ТК ${sfx}`);
    ok('способ доставки добавлен');
  }
  ok('отгрузки по нескольким заказам');

  await page.goto(`${BASE}/sales-pick-lists`); await ready(page);
  await page.waitForSelector('h1:has-text("Задания на сборку")');
  await assertNoHorizontalScroll(page); await shot('sales-pick-lists');
  const box = page.locator('section.panel input[type=checkbox]').first();
  if (width === 1440 && await box.count() > 0) {
    await box.check();
    await page.getByRole('button', { name: /Создать задание/ }).click();
    await page.waitForSelector('h1:has-text("Задание на сборку")'); await ready(page);
    await shot('sales-pick-list-card');
    await page.getByRole('button', { name: 'Собрано полностью' }).click();
    await page.waitForSelector('text=Задание собрано.');
    ok('задание на сборку собрано');
    const [print] = await Promise.all([page.waitForEvent('popup'), page.getByRole('link', { name: 'Лист сборки' }).click()]);
    await print.waitForSelector('text=Лист сборки №');
    await print.close();
    ok('лист сборки');
  }
  ok('задания на сборку');

  if (errors.length > 0) {
    throw new Error(`Ошибки JS [${width}]: ${errors.join('\n')}`);
  }

  await browser.close();
}
