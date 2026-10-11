// API каталога (D307): токен из интерфейса (без права переоценки), GET /api/v1/catalog/items, /items/{id}/availability, /barcodes/{code}.
import { ownerSession, collectErrors, ready, BASE } from '../lib.mjs';

const { browser, ctx, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);

await page.goto(`${BASE}/integrations`); await ready(page);
const rights = await page.locator('fieldset label.check').allInnerTexts();
if (rights.some(r => r.includes('Цены: переоценка'))) throw new Error('право переоценки предлагается для токена');
ok('право «Цены: переоценка» токену не предлагается');
await page.locator('label:has-text("Название") input').fill('Магазин e2e');
await page.locator('fieldset label.check', { hasText: 'Справочники: просмотр' }).locator('input').check();
await page.getByRole('button', { name: 'Выпустить токен' }).click();
const secretBox = page.locator('input[readonly]');
await secretBox.waitFor();
const token = await secretBox.inputValue();
ok('токен выпущен');

const api = await ctx.request;
const get = async (path) => {
  const r = await api.get(`${BASE}/api/v1${path}`, { headers: { Authorization: `Bearer ${token}` } });
  return { status: r.status(), body: r.status() === 200 ? await r.json() : await r.text() };
};
const list = await get('/catalog/items?limit=50');
if (list.status !== 200 || !Array.isArray(list.body.items)) throw new Error('catalog/items: ' + JSON.stringify(list).slice(0, 300));
const withPacks = list.body.items.find(i => (i.packs ?? []).length > 0 || i.kind !== undefined);
if (!withPacks || !('trackingType' in withPacks) || !('paymentSubject' in withPacks)) throw new Error('нет полей обмена: ' + JSON.stringify(list.body.items[0]));
if (list.body.items.some(i => i.minPrice != null || i.salePrice != null)) throw new Error('цены видны токену без права «Цены и суммы»');
ok(`catalog/items: ${list.body.items.length} позиций с видом, маркировкой, упаковками; цены скрыты`);

const first = list.body.items[0];
const av = await get(`/items/${first.id}/availability`);
if (av.status !== 200 || !('total' in av.body)) throw new Error('availability: ' + JSON.stringify(av).slice(0, 300));
ok(`availability: ${first.code} — ${av.body.total}`);
const missing = await get('/barcodes/0000000000000');
if (missing.status !== 404) throw new Error('barcodes: ожидался 404, получено ' + missing.status);
const unauth = await api.get(`${BASE}/api/v1/catalog/items`);
if (unauth.status() !== 401) throw new Error('без токена: ' + unauth.status());
ok('неизвестный штрихкод — 404, без токена — 401');

await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
