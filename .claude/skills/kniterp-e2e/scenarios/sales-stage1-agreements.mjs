// Продажи, этап 1 (D1101–D1104): соглашение с покупателем, ограничения цен роли, автоподстановка цены в заказ,
// предупреждение о цене ниже допустимой и подтверждение с причиной; вкладка «Условия продаж» в карточке контрагента. 1440 и 390 px.
// Скриншоты — в $SHOTS (если задан).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, plainText, BASE } from '../lib.mjs';

const SHOTS = process.env.SHOTS;
const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const shot = async (name) => { if (SHOTS) await page.screenshot({ path: `${SHOTS}/${name}.png`, fullPage: true }); };
const sfx = Date.now().toString(36).slice(-5).toUpperCase();

// Справочники: склад, виды цен, покупатель, позиция с ценами и минимальной ценой.
await page.goto(`${BASE}/warehouses`); await ready(page);
await page.locator('label:has-text("Новый склад") input').fill('Склад ГП ' + sfx);
await page.getByRole('button', { name: 'Добавить', exact: true }).first().click();
await page.waitForSelector(`text=Склад ГП ${sfx}`);

await page.goto(`${BASE}/catalog/settings?tab=prices`); await ready(page);
for (const name of ['Опт ' + sfx, 'Крупный опт ' + sfx]) {
  await page.locator('form:has(button:has-text("Добавить вид цены")) label:has-text("Название") input').fill(name);
  await page.getByRole('button', { name: 'Добавить вид цены' }).click();
  await page.waitForSelector(`text=${name}`);
}
ok('склад и виды цен');

await page.goto(`${BASE}/counterparties`); await ready(page);
await page.getByRole('button', { name: 'Добавить контрагента' }).click();
await page.locator('form label:has-text("Наименование") input').fill('ООО «Трикотаж-Опт ' + sfx + '»');
await page.locator('form label:has-text("Покупатель") input[type=checkbox]').check();
await page.locator('form').getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector(`text=Трикотаж-Опт ${sfx}`);
ok('покупатель');

await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill('СВ-' + sfx);
await form.locator('label:has-text("Наименование") input').fill('Свитер оверсайз ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.locator('.flash').getByRole('link', { name: 'Свитер оверсайз ' + sfx }).click();
await page.waitForSelector('text=Торговля и маркировка'); await ready(page);
const prices = page.locator('form:has(button:has-text("Сохранить цены"))');
await prices.locator('input[aria-label="Цена продажи"]').fill('1000');
await prices.locator(`input[aria-label="Опт ${sfx}"]`).fill('900');
await prices.locator(`input[aria-label="Крупный опт ${sfx}"]`).fill('800');
await prices.getByRole('button', { name: 'Сохранить цены' }).click();
await page.waitForSelector('text=Цены сохранены.');
const trade = page.locator('form:has(button:has-text("Сохранить торговые сведения"))');
await trade.locator('label:has-text("Минимальная цена продажи") input').fill('700');
await trade.getByRole('button', { name: 'Сохранить торговые сведения' }).click();
await page.waitForSelector('text=Торговые сведения сохранены.');
ok('позиция: цены 1000 / 900 / 800, минимальная 700');

// Соглашение покупателя: «Крупный опт», скидка 5 %, отсрочка 14 дней, лимит.
await page.goto(`${BASE}/sales-agreements`); await ready(page);
await page.getByRole('button', { name: 'Новое соглашение' }).click();
const ag = page.locator('section.panel form');
await ag.locator('label:has-text("Название") input').fill('Опт 2026 ' + sfx);
await ag.locator('label:has-text("Покупатель"):not(:has-text("Для кого")) select').selectOption({ label: 'ООО «Трикотаж-Опт ' + sfx + '»' });
await ag.locator('label:has-text("Вид цены") select').selectOption({ label: 'Крупный опт ' + sfx });
await ag.locator('label:has-text("Скидка, %") input').fill('5');
await ag.locator('label:has-text("Отсрочка, дней") input').fill('14');
await ag.locator('label:has-text("Кредитный лимит") input').fill('500000');
await shot('s1-01-agreement-form');
await ag.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Соглашение создано.');
await page.locator('label:has-text("Поиск") input').fill(sfx);
await page.getByRole('button', { name: 'Показать' }).click();
await page.waitForSelector(`td:has-text("Опт 2026 ${sfx}")`);
await shot('s1-02-agreements-list');
ok('соглашение создано и видно в списке');

// Ограничения роли «Менеджер продаж»: скидка до 3 %, граница — «Крупный опт».
await page.getByRole('tab', { name: 'Ограничения цен по ролям' }).click();
const managerRow = page.locator('tr:has(td:has-text("Менеджер продаж"))');
await managerRow.locator('input').fill('3');
await managerRow.locator('select').first().selectOption({ label: 'Крупный опт ' + sfx });
await managerRow.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Ограничения роли сохранены.');
await shot('s1-03-role-limits');
ok('ограничения роли сохранены');

// Заказ: цена подставилась по соглашению (800, скидка 5 %), подсказка источника.
await page.goto(`${BASE}/sales-orders`); await ready(page);
await page.getByRole('button', { name: 'Новый заказ' }).click();
await page.locator('label:has-text("Покупатель") select').selectOption({ label: 'ООО «Трикотаж-Опт ' + sfx + '»' });
await page.locator('label:has-text("Склад отгрузки") select').selectOption({ label: 'Склад ГП ' + sfx });
await page.getByRole('button', { name: 'Создать черновик' }).click();
await page.waitForURL(/sales-orders\/\d+/); await ready(page);
if (!(await plainText(page.locator('dl.facts'))).includes('Опт 2026 ' + sfx)) throw new Error('в заказе нет соглашения');
const item = page.locator('label:has-text("Позиция") input');
await item.fill('СВ-' + sfx); await item.blur();
await page.waitForSelector('.sx-price-hint');
const priceValue = await page.locator('label:has-text("Цена с НДС") input').inputValue();
if (priceValue !== '800') throw new Error('подставлена цена ' + priceValue);
if ((await page.locator('label:has-text("Скидка, %") input').first().inputValue()) !== '5') throw new Error('нет скидки соглашения');
await page.locator('label:has-text("Количество") input').fill('10');
await shot('s1-04-order-autoprice');
await page.getByRole('button', { name: 'Добавить или изменить' }).click();
await page.waitForSelector('td:has-text("7 600,00")');
ok('цена 800 и скидка 5 % подставлены по соглашению, сумма 7 600');

// Цена 650 ниже минимальной 700 — предупреждение и подтверждение с причиной (у Владельца есть право).
await page.getByRole('button', { name: 'Изменить: СВ-' + sfx }).click();
await page.locator('label:has-text("Цена с НДС") input').fill('650');
await page.locator('label:has-text("Скидка, %") input').first().fill('');
await page.getByRole('button', { name: 'Добавить или изменить' }).click();
await page.waitForSelector('.sx-issues');
if (!(await plainText(page.locator('.sx-issues'))).includes('700')) throw new Error('нет предупреждения о минимальной цене');
await shot('s1-05-order-price-issue');
await page.locator('.sx-issues label:has-text("Причина") input').fill('Распродажа остатков, согласовано');
await page.getByRole('button', { name: 'Подтвердить с ценой ниже допустимой' }).click();
await page.waitForSelector('text=причина записана в журнал');
if (!(await plainText(page.locator('dl.facts'))).includes('Распродажа остатков')) throw new Error('причина не видна в заказе');
await shot('s1-06-order-confirmed-override');
ok('подтверждение с причиной, причина видна в заказе');
const orderUrl = page.url();

// Карточка контрагента → «Условия продаж».
await page.goto(`${BASE}/counterparties`); await ready(page);
await page.locator('label:has-text("Поиск") input').fill(sfx);
await page.getByRole('button', { name: 'Найти' }).click();
await page.getByRole('link', { name: new RegExp('Трикотаж-Опт ' + sfx) }).first().click();
await ready(page);
await page.getByRole('tab', { name: 'Условия продаж' }).click();
await page.waitForSelector('text=Действующее соглашение');
await shot('s1-07-counterparty-terms');
const cardUrl = page.url();
ok('вкладка «Условия продаж»');

await page.setViewportSize({ width: 390, height: 844 });
for (const [url, name] of [[`${BASE}/sales-agreements`, 'agreements'], [orderUrl, 'order'], [cardUrl, 'card']]) {
  await page.goto(url); await ready(page);
  await assertNoHorizontalScroll(page, url);
  await shot('s1-08-phone-' + name);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
