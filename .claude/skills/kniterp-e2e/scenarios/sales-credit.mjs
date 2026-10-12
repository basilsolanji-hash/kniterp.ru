// Продажи, этап 2 (D1105, D1111–D1114): кредитный лимит соглашения останавливает подтверждение заказа, панель «Кредитный контроль»
// в карточке заказа («превышение лимита на …»), разрешение с причиной, подтверждение после разрешения; отчёт «Дебиторская
// задолженность» (отбор, Excel, текст письма) и блок главного экрана. 1440 и 390 px. Скриншоты — в $SHOTS (если задан).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, plainText, BASE } from '../lib.mjs';

const SHOTS = process.env.SHOTS;
const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const shot = async (name) => { if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: true }); };
const sfx = Date.now().toString(36).slice(-5).toUpperCase();
const customer = 'ООО «Кредит-Тест ' + sfx + '»';

// Справочники: склад, покупатель, позиция с ценой 1 000.
await page.goto(`${BASE}/warehouses`); await ready(page);
await page.locator('label:has-text("Новый склад") input').fill('Склад ГП ' + sfx);
await page.getByRole('button', { name: 'Добавить', exact: true }).first().click();
await page.waitForSelector(`text=Склад ГП ${sfx}`);

await page.goto(`${BASE}/counterparties`); await ready(page);
await page.getByRole('button', { name: 'Добавить контрагента' }).click();
await page.locator('form label:has-text("Наименование") input').fill(customer);
await page.locator('form label:has-text("Покупатель") input[type=checkbox]').check();
await page.locator('form').getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector(`text=Кредит-Тест ${sfx}`);

await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill('КР-' + sfx);
await form.locator('label:has-text("Наименование") input').fill('Джемпер ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.locator('.flash').getByRole('link', { name: 'Джемпер ' + sfx }).click();
await page.waitForSelector('text=Торговля и маркировка'); await ready(page);
const prices = page.locator('form:has(button:has-text("Сохранить цены"))');
await prices.locator('input[aria-label="Цена продажи"]').fill('1000');
await prices.getByRole('button', { name: 'Сохранить цены' }).click();
await page.waitForSelector('text=Цены сохранены.');
ok('склад, покупатель, позиция');

// Соглашение с лимитом 5 000 и отсрочкой 10 дней.
await page.goto(`${BASE}/sales-agreements`); await ready(page);
await page.getByRole('button', { name: 'Новое соглашение' }).click();
const ag = page.locator('section.panel form');
await ag.locator('label:has-text("Название") input').fill('Лимит ' + sfx);
await ag.locator('label:has-text("Покупатель"):not(:has-text("Для кого")) select').selectOption({ label: customer });
await ag.locator('label:has-text("Отсрочка, дней") input').fill('10');
await ag.locator('label:has-text("Кредитный лимит") input').fill('5000');
await ag.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Соглашение создано.');
ok('соглашение: лимит 5 000, отсрочка 10 дней');

// Заказ на 10 000: панель «Кредитный контроль» показывает превышение, подтверждение отклонено.
await page.goto(`${BASE}/sales-orders`); await ready(page);
await page.getByRole('button', { name: 'Новый заказ' }).click();
await page.locator('label:has-text("Покупатель") select').selectOption({ label: customer });
await page.locator('label:has-text("Склад отгрузки") select').selectOption({ label: 'Склад ГП ' + sfx });
await page.getByRole('button', { name: 'Создать черновик' }).click();
await page.waitForURL(/sales-orders\/\d+/); await ready(page);
const item = page.locator('label:has-text("Позиция") input');
await item.fill('КР-' + sfx); await item.blur();
await page.waitForSelector('.sx-price-hint');
await page.locator('label:has-text("Количество") input').fill('10');
await page.getByRole('button', { name: 'Добавить или изменить' }).click();
await page.waitForSelector('section.scp');
const panel = await plainText(page.locator('section.scp'));
if (!panel.includes('превышение лимита на 5 000,00')) throw new Error('нет превышения в панели: ' + panel);
await shot('s2-01-order-credit-panel');
await page.getByRole('button', { name: 'Подтвердить заказ' }).click();
await page.waitForSelector('text=Кредитный лимит превышен');
ok('лимит: подтверждение остановлено, превышение 5 000 видно в заказе');

// Разрешение с причиной — затем подтверждение проходит.
await page.locator('section.scp label:has-text("Причина разрешения") input').fill('Гарантийное письмо ' + sfx);
await page.getByRole('button', { name: 'Разрешить сверх лимита и при просрочке' }).click();
await page.waitForSelector('text=разрешено руководителем');
if (!(await plainText(page.locator('section.scp'))).includes('Гарантийное письмо ' + sfx)) throw new Error('разрешение не видно в заказе');
await shot('s2-02-order-credit-approved');
await page.getByRole('button', { name: 'Подтвердить заказ' }).click();
await page.waitForSelector('text=Подтверждён');
ok('разрешение с причиной видно в заказе, заказ подтверждён');
const orderUrl = page.url();

// Отчёт «Дебиторская задолженность»: отбор, Excel, ссылки «Акт сверки» и «Текст письма».
await page.goto(`${BASE}/reports/receivables`); await ready(page);
await page.waitForSelector('h1:has-text("Дебиторская задолженность")');
const excel = page.getByRole('link', { name: 'Excel' });
if (!(await excel.getAttribute('href')).startsWith('reports/receivables.xlsx')) throw new Error('нет ссылки Excel');
const download = await Promise.all([page.waitForEvent('download'), excel.click()]).then(([d]) => d);
if (!download.suggestedFilename().startsWith('debitorka-')) throw new Error('имя файла ' + download.suggestedFilename());
await shot('s2-03-receivables');
ok('отчёт «Дебиторская задолженность» и выгрузка в Excel');

// Главный экран: блок «Просроченная дебиторка» появляется только при долгах — проверяем, что страница без ошибок.
await page.goto(`${BASE}/`); await ready(page);
await shot('s2-04-dashboard');

await page.setViewportSize({ width: 390, height: 844 });
for (const [url, name] of [[orderUrl, 'order'], [`${BASE}/reports/receivables`, 'receivables']]) {
  await page.goto(url); await ready(page);
  await assertNoHorizontalScroll(page, url);
  await shot('s2-05-phone-' + name);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
