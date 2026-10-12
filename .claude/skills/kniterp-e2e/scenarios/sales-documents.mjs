// Продажи, документы и письма (D1120–D1125): настройки оформления (логотип, печать — загрузка PNG), шаблон и автоотправка письма,
// почта покупателя во вкладке карточки контрагента, заказ → подтверждение → письмо в журнале заказа, печать заказа с логотипом,
// ручная связь с другим заказом и отвязка. 1440 и 390 px. Скриншоты — в $SHOTS (если задан).
// Сценарий написан агентом B и НЕ запускался (запуск — координатор после слияния).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, plainText, BASE } from '../lib.mjs';

const SHOTS = process.env.SHOTS;
const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const shot = async (name) => { if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: true }); };
const sfx = Date.now().toString(36).slice(-5).toUpperCase();
// Настоящий PNG 1×1.
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');

// 1. Оформление документов: логотип и печать, флажки.
await page.goto(`${BASE}/sales-documents-settings`); await ready(page);
await page.waitForSelector('text=Что выводить в документах');
for (const kind of ['Логотип', 'Печать']) {
  const card = page.locator(`.sx-image:has(b:text-is("${kind}"))`);
  await card.locator('input[type=file]').setInputFiles({ name: 'image.png', mimeType: 'image/png', buffer: png });
  await page.waitForSelector('text=Изображение загружено.');
}
// Файл не той сигнатуры — понятный отказ.
await page.locator('.sx-image:has(b:text-is("Подпись руководителя")) input[type=file]')
  .setInputFiles({ name: 'evil.png', mimeType: 'image/png', buffer: Buffer.from('<html><script>alert(1)</script>') });
await page.waitForSelector('text=Изображение — PNG или JPEG.');
await page.getByRole('button', { name: 'Сохранить' }).first().click();
await page.waitForSelector('text=Оформление документов сохранено.');
await shot('docs-01-settings');
ok('логотип и печать загружены, неверный файл отклонён');

// 2. Письмо «Заказ» — автоматически.
await page.getByRole('tab', { name: 'Письма клиентам' }).click();
const orderTemplate = page.locator('form:has(.sx-legend:has-text("Заказ —"))');
await orderTemplate.locator('label:has-text("Отправлять автоматически") input').check();
await orderTemplate.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Шаблон письма сохранён.');
await shot('docs-02-mail-templates');
ok('автоотправка письма о заказе включена');

// 3. Склад, покупатель с почтой (вкладка «Почта для документов»), позиция.
await page.goto(`${BASE}/warehouses`); await ready(page);
await page.locator('label:has-text("Новый склад") input').fill('Склад Д ' + sfx);
await page.getByRole('button', { name: 'Добавить', exact: true }).first().click();
await page.waitForSelector(`text=Склад Д ${sfx}`);

await page.goto(`${BASE}/counterparties`); await ready(page);
await page.getByRole('button', { name: 'Добавить контрагента' }).click();
await page.locator('form label:has-text("Наименование") input').fill('ООО «Документы ' + sfx + '»');
await page.locator('form label:has-text("Покупатель") input[type=checkbox]').check();
await page.locator('form').getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector(`text=Документы ${sfx}`);
await page.locator('label:has-text("Поиск") input').fill(sfx);
await page.getByRole('button', { name: 'Найти' }).click();
await page.getByRole('link', { name: new RegExp('Документы ' + sfx) }).first().click();
await ready(page);
await page.getByRole('tab', { name: 'Почта для документов' }).click();
await page.locator('label:has-text("Почта (до 5 адресов") input').fill('buh@example.org');
await page.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Почта для документов сохранена.');
await shot('docs-03-customer-mail');
const cardUrl = page.url();
ok('почта покупателя сохранена');

await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill('ДК-' + sfx);
await form.locator('label:has-text("Наименование") input').fill('Джемпер ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');

// 4. Два заказа; первый подтверждаем — письмо в журнале заказа.
const orders = [];
for (let i = 0; i < 2; i++) {
  await page.goto(`${BASE}/sales-orders`); await ready(page);
  await page.getByRole('button', { name: 'Новый заказ' }).click();
  await page.locator('label:has-text("Покупатель") select').selectOption({ label: 'ООО «Документы ' + sfx + '»' });
  await page.locator('label:has-text("Склад отгрузки") select').selectOption({ label: 'Склад Д ' + sfx });
  await page.getByRole('button', { name: 'Создать черновик' }).click();
  await page.waitForURL(/sales-orders\/\d+/); await ready(page);
  const item = page.locator('label:has-text("Позиция") input');
  await item.fill('ДК-' + sfx); await item.blur();
  await page.locator('label:has-text("Цена с НДС") input').fill('1500');
  await page.locator('label:has-text("Количество") input').fill('2');
  await page.getByRole('button', { name: 'Добавить или изменить' }).click();
  await page.waitForSelector('td:has-text("3 000,00")');
  orders.push(page.url());
}
await page.goto(orders[0]); await ready(page);
await page.getByRole('button', { name: 'Подтвердить заказ' }).click();
await page.waitForSelector('text=Журнал писем');
if (!(await plainText(page.locator('table:has(th:has-text("Кому"))'))).includes('buh@example.org')) throw new Error('нет письма в журнале');
await shot('docs-04-order-mail-log');
ok('подтверждение поставило письмо в очередь, журнал виден в заказе');

// 5. Отправить вручную: форма письма с шаблоном.
await page.getByRole('button', { name: 'Отправить клиенту' }).click();
await page.waitForSelector('text=Письмо клиенту');
await page.getByRole('button', { name: 'Отправить', exact: true }).click();
await page.waitForSelector('text=Письмо поставлено в очередь');
ok('ручная отправка');

// 6. Печать заказа — с логотипом и печатью.
const print = await page.context().newPage();
await print.goto(orders[0].replace(/sales-orders\/(\d+)/, 'print/sales-order/$1')); await ready(print);
if (await print.locator('img.sx-logo').count() !== 1) throw new Error('нет логотипа в печатной форме заказа');
if (await print.locator('img.sx-stamp').count() !== 1) throw new Error('нет печати в форме заказа');
if (SHOTS) await print.screenshot({ path: `${SHOTS}/docs-05-print-order.png`, fullPage: true });
await print.close();
ok('печатная форма заказа с логотипом и печатью');

// 7. Связать со вторым заказом и отвязать.
const secondId = orders[1].split('/').pop();
await page.locator('form:has(.sx-legend:has-text("Связать документ")) label:has-text("Вид документа") select').selectOption('SalesOrder');
await page.getByRole('button', { name: 'Найти' }).click();
await page.locator('tr:has(button:has-text("Связать"))').first().getByRole('button', { name: 'Связать' }).click();
await page.waitForSelector('text=Документ связан с заказом.');
await shot('docs-06-links');
await page.locator('tr:has(.sx-line-flag)').getByRole('button', { name: 'Отвязать' }).click();
await page.getByRole('button', { name: 'Да', exact: true }).click();
await page.waitForSelector('text=Документ отвязан.');
ok('связь с заказом создана и снята (заказ id ' + secondId + ')');

await page.setViewportSize({ width: 390, height: 844 });
for (const [url, name] of [[`${BASE}/sales-documents-settings`, 'settings'], [orders[0], 'order'], [cardUrl, 'card']]) {
  await page.goto(url); await ready(page);
  await assertNoHorizontalScroll(page, url);
  await shot('docs-07-phone-' + name);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
