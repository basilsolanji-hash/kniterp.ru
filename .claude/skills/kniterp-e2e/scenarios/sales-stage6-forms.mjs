// Продажи, этап 6 (D1141): печатные формы по отгрузке — УПД (статус 1 и 2), счёт-фактура, ТОРГ-12, транспортная накладная,
// печать комплекта документов заказа. Поступление → заказ → отгрузка → формы. 1440 и 390 px. Скриншоты — в $SHOTS (если задан).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, plainText, BASE } from '../lib.mjs';

const SHOTS = process.env.SHOTS;
const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const shot = async (name) => { if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: true }); };
const sfx = Date.now().toString(36).slice(-5).toUpperCase();
// Вымышленный ИНН юрлица с верной контрольной цифрой.
const inn = (() => {
  const body = String(Date.now()).slice(-9).padStart(9, '7');
  const w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
  const sum = [...body].reduce((s, c, i) => s + Number(c) * w[i], 0);
  return body + (sum % 11 % 10);
})();

// Склад, покупатель с ИНН и адресом, позиция с ценой.
await page.goto(`${BASE}/warehouses`); await ready(page);
await page.locator('label:has-text("Новый склад") input').fill('Склад Ф ' + sfx);
await page.getByRole('button', { name: 'Добавить', exact: true }).first().click();
await page.waitForSelector(`text=Склад Ф ${sfx}`);
await page.goto(`${BASE}/counterparties`); await ready(page);
await page.getByRole('button', { name: 'Добавить контрагента' }).click();
await page.locator('form label:has-text("Наименование") input').fill('ООО «Формы ' + sfx + '»');
await page.locator('form .inn-fill input').fill(inn);
await page.locator('form label:has-text("Покупатель") input[type=checkbox]').check();
await page.locator('form').getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector(`text=Формы ${sfx}`);
await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill('ФМ-' + sfx);
await form.locator('label:has-text("Наименование") input').fill('Кардиган ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.locator('.flash').getByRole('link', { name: 'Кардиган ' + sfx }).click();
await page.waitForSelector('text=Торговля и маркировка'); await ready(page);
await page.locator('form:has(button:has-text("Сохранить цены")) input[aria-label="Цена продажи"]').fill('2450');
await page.getByRole('button', { name: 'Сохранить цены' }).click();
await page.waitForSelector('text=Цены сохранены.');
ok('справочники');

// Поступление 20 шт.
await page.goto(`${BASE}/stock-documents?kind=1`); await ready(page);
await page.getByRole('button', { name: 'Новое поступление' }).click();
await page.locator('.panel label:has-text("Склад-получатель") select').selectOption({ label: 'Склад Ф ' + sfx });
const reason = page.locator('.panel label:has-text("Причина") select');
await reason.selectOption({ index: 1 });
await page.getByRole('button', { name: 'Создать черновик' }).click();
await page.waitForURL(/stock-documents\/\d+/); await ready(page);
await page.locator('label:has-text("Позиция") input').fill(`ФМ-${sfx} — Кардиган ${sfx}`);
await page.locator('label:has-text("Количество") input').fill('20');
await page.getByRole('button', { name: 'Добавить', exact: true }).click();
await page.waitForSelector(`td:has-text("ФМ-${sfx}")`);
await page.getByRole('button', { name: 'Провести' }).click();
await page.waitForSelector('text=Проведён');
ok('поступление проведено');

// Заказ 5 шт. → подтверждение → отгрузка → проведение.
await page.goto(`${BASE}/sales-orders`); await ready(page);
await page.getByRole('button', { name: 'Новый заказ' }).click();
await page.locator('label:has-text("Покупатель") select').selectOption({ label: 'ООО «Формы ' + sfx + '»' });
await page.locator('label:has-text("Склад отгрузки") select').selectOption({ label: 'Склад Ф ' + sfx });
await page.getByRole('button', { name: 'Создать черновик' }).click();
await page.waitForURL(/sales-orders\/\d+/); await ready(page);
const orderUrl = page.url();
const item = page.locator('label:has-text("Позиция") input');
await item.fill('ФМ-' + sfx); await item.blur();
await page.waitForSelector('.sx-price-hint');
await page.locator('label:has-text("Количество") input').fill('5');
await page.getByRole('button', { name: 'Добавить или изменить' }).click();
await page.waitForSelector(`td:has-text("ФМ-${sfx}")`);
await page.getByRole('button', { name: 'Подтвердить заказ' }).click();
await page.waitForSelector('button:has-text("Оформить отгрузку")');
await page.getByRole('button', { name: 'Оформить отгрузку' }).click();
await page.waitForURL(/stock-documents\/\d+/); await ready(page);
const shipId = page.url().match(/stock-documents\/(\d+)/)[1];
await page.getByRole('button', { name: 'Провести' }).click();
await page.waitForSelector('text=Проведён');
ok('отгрузка проведена: ' + shipId);

// Ссылки форм в карточке заказа.
await page.goto(orderUrl); await ready(page);
for (const name of ['УПД', 'ТОРГ-12', 'ТН', 'XML для ЭДО']) await page.getByRole('link', { name, exact: true }).first().waitFor();
await page.getByRole('link', { name: 'Печать комплекта' }).waitFor();
ok('ссылки форм и «Печать комплекта» в заказе');

// Формы по отгрузке.
const forms = [
  [`print/upd/${shipId}?form=upd1`, 'Универсальный передаточный документ', 'Статус: 1', 'f-01-upd1'],
  [`print/upd/${shipId}?form=upd2`, 'Универсальный передаточный документ', 'Статус: 2', 'f-02-upd2'],
  [`print/upd/${shipId}?form=sf`, 'Счёт-фактура', '1137', 'f-03-vat-invoice'],
  [`print/torg12/${shipId}`, 'ТОВАРНАЯ НАКЛАДНАЯ', '0330212', 'f-04-torg12'],
  [`print/ttn/${shipId}`, 'Транспортная накладная', '2200', 'f-05-ttn'],
];
for (const [url, title, marker, name] of forms) {
  await page.goto(`${BASE}/${url}`); await ready(page);
  await page.waitForSelector('.sheet table');
  const text = await plainText(page.locator('.sheet').first());
  if (!text.includes(title) || !text.includes(marker) || !text.includes('Кардиган ' + sfx)) throw new Error(`${url}: нет «${title}» / «${marker}» / позиции`);
  await shot(name);
}
ok('УПД 1 и 2, счёт-фактура, ТОРГ-12, транспортная накладная');

// Комплект: УПД + ТОРГ-12 + накладная, по листу с новой страницы.
const orderId = orderUrl.match(/sales-orders\/(\d+)/)[1];
await page.goto(`${BASE}/print/sales-set/${orderId}?docs=upd,torg12,ttn`); await ready(page);
await page.waitForSelector('.sheet.torg12');
const sheets = await page.locator('.sheet').count();
if (sheets !== 3) throw new Error('в комплекте листов: ' + sheets);
await shot('f-06-set');
await page.getByRole('link', { name: 'Счёт-фактура', exact: true }).click();
await page.waitForURL(/docs=upd%2Csf|docs=upd,sf/); await page.waitForFunction(() => document.querySelectorAll('.sheet').length === 4);
ok('комплект: 3 листа, переключатель форм добавляет счёт-фактуру');

// D1142: прайс-лист — позиция с ценой, отбор «только в наличии», ссылки PDF и Excel.
await page.goto(`${BASE}/sales-price-list`); await ready(page);
await page.locator('label:has-text("Поиск") input').fill('ФМ-' + sfx);
await page.getByRole('button', { name: 'Показать' }).click();
await page.waitForSelector(`td:has-text("ФМ-${sfx}")`);
await page.locator('label:has-text("Только в наличии") input').check();
await page.getByRole('button', { name: 'Показать' }).click();
await page.waitForSelector(`td:has-text("ФМ-${sfx}")`);
const pdfHref = await page.getByRole('link', { name: 'PDF', exact: true }).getAttribute('href');
const pdf = await page.request.get(`${BASE}/${pdfHref}`);
if (pdf.status() !== 200 || !(await pdf.body()).subarray(0, 4).toString().startsWith('%PDF')) throw new Error('PDF прайс-листа: ' + pdf.status());
await shot('f-08-price-list');
ok('прайс-лист: отбор, PDF');

// D1143: журнал «не отгружены полностью» — заказ отгружен полностью, в отборе его нет.
await page.goto(`${BASE}/sales-orders?unshipped=1`); await ready(page);
await page.waitForSelector('table');
if ((await plainText(page.locator('table').first())).includes('Формы ' + sfx)) throw new Error('отгруженный заказ в отборе «не отгружены»');
ok('журнал: отбор «Не отгружены полностью»');

await page.setViewportSize({ width: 390, height: 844 });
for (const [url, name] of [[orderUrl, 'order'], [`${BASE}/sales-price-list`, 'price-list'], [`${BASE}/print/sales-set/${orderId}`, 'set']]) {
  await page.goto(url); await ready(page);
  if (name !== 'set') await assertNoHorizontalScroll(page, url);
  await shot('f-07-phone-' + name);
}
ok('390 px: карточка заказа без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
