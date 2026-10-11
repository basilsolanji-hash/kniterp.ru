// D314: фид YML — создание, ссылка один раз, файл по ссылке без входа, 404 на чужой ключ. D312 (часть 2): скрытый блок карточки.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();

// Позиция с ценой основного вида — чтобы фид был не пустым.
await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
await form.locator('label:has-text("Код") input').fill('ФД-' + sfx);
await form.locator('label:has-text("Наименование") input').fill('Шапка для фида ' + sfx);
await form.locator('label:has-text("Тип") select').selectOption('Finished');
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.locator('.flash').getByRole('link', { name: 'Шапка для фида ' + sfx }).click();
await page.waitForSelector('h2:has-text("Цены")'); await ready(page);
const cardUrl = page.url();
// Цена 1234 во всех видах цен — фид берёт выбранный вид.
const priceInputs = page.locator('input[inputmode="decimal"][size="12"]');
for (let i = 0; i < await priceInputs.count(); i++) { await priceInputs.nth(i).fill('1234'); await priceInputs.nth(i).press('Tab'); }
await page.getByRole('button', { name: 'Сохранить цены' }).click();
await page.waitForSelector('text=Цены сохранены.');
ok('позиция с ценой');

await page.goto(`${BASE}/exchange/feeds`); await ready(page);
await page.locator('label:has-text("Название") input').fill('Маркет ' + sfx);
await page.locator('label:has-text("Только позиции в наличии") input').uncheck();
await page.getByRole('button', { name: 'Создать фид' }).click();
await page.waitForSelector('text=Сохраните ссылку сейчас');
const url = await page.locator('input[aria-label="Ссылка на фид"]').inputValue();
if (!/\/feeds\/kf_[A-Za-z0-9_-]{43}\.yml$/.test(url)) throw new Error('ссылка: ' + url);
await page.getByRole('button', { name: 'Я сохранил, скрыть' }).click();
await page.locator('input[aria-label="Ссылка на фид"]').waitFor({ state: 'detached', timeout: 5000 });
ok('ссылка показана один раз');

// Площадка: запрос без cookie входа.
const anonCtx = await browser.newContext({ ignoreHTTPSErrors: true }); // новый контекст — без cookie входа
const anon = anonCtx.request;
const res = await anon.get(url);
const body = await res.text();
if (res.status() !== 200 || !body.includes('<yml_catalog') || !body.includes('Шапка для фида ' + sfx) || !body.includes('<price>1234</price>'))
  throw new Error(`фид ${res.status()}: ${body.slice(0, 300)}`);
const bad = await anon.get(url.replace(/kf_.{5}/, 'kf_ZZZZZ'));
const junk = await anon.get(`${BASE}/feeds/..%2F..%2Fetc.yml`);
if (bad.status() !== 404 || junk.status() !== 404) throw new Error(`чужой ключ ${bad.status()}, мусор ${junk.status()}`);
await anonCtx.close();
ok('файл YML по ссылке без входа; чужой и неверный ключ — 404');

// Настройки: скрыть «Упаковки» → в карточке блока нет.
await page.goto(`${BASE}/catalog/settings?tab=General`); await ready(page);
await page.locator('fieldset:has(legend:has-text("Блоки карточки")) label:has-text("Упаковки") input').uncheck();
await page.getByRole('button', { name: 'Сохранить настройки' }).click();
await page.waitForSelector('text=Настройки каталога сохранены.');
await page.goto(cardUrl); await ready(page);
await page.waitForSelector('h2:has-text("Цены")');
if (await page.locator('h2:has-text("Упаковки")').count() !== 0) throw new Error('скрытый блок «Упаковки» виден');
await page.goto(`${BASE}/catalog/settings?tab=General`); await ready(page);
await page.locator('fieldset:has(legend:has-text("Блоки карточки")) label:has-text("Упаковки") input').check();
await page.getByRole('button', { name: 'Сохранить настройки' }).click();
await page.waitForSelector('text=Настройки каталога сохранены.');
ok('скрытый блок карточки не показывается и возвращается');

await page.setViewportSize({ width: 390, height: 844 });
for (const u of [`${BASE}/exchange/feeds`, `${BASE}/catalog/settings?tab=General`]) {
  await page.goto(u); await ready(page);
  await assertNoHorizontalScroll(page, u);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
