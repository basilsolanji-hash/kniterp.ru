// D331: фид и подменённый заголовок Host. Без App:PublicUrl — Host запроса только в своём ответе (кэш не отравляется);
// с App__PublicUrl (переменная E2E_PUBLIC_URL та же, что задана приложению) — адреса всегда от публичного адреса.
import { ownerSession, collectErrors, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const sfx = Date.now().toString(36).slice(-5).toUpperCase();
const pub = process.env.E2E_PUBLIC_URL;
await page.goto(`${BASE}/exchange/feeds`); await ready(page);
await page.locator('label:has-text("Название") input').fill('Host ' + sfx);
await page.locator('label:has-text("Только позиции в наличии") input').uncheck();
await page.getByRole('button', { name: 'Создать фид' }).click();
await page.waitForSelector('text=Сохраните ссылку сейчас');
const url = await page.locator('input[aria-label="Ссылка на фид"]').inputValue();
const anon = (await browser.newContext({ ignoreHTTPSErrors: true })).request;
const urlOf = async (headers) => (await (await anon.get(url, { headers })).text()).match(/<url>([^<]*)<\/url>/)?.[1];
const evil = await urlOf({ Host: 'evil.example' });   // первым — пытается положить в кэш файл с чужим адресом
const normal = await urlOf({});
console.log(`с чужим Host: ${evil}; обычный запрос: ${normal}`);
if (pub) {
  if (evil !== pub + '/' || normal !== pub + '/') throw new Error('ожидался публичный адрес ' + pub);
  console.log('✓ адреса фида — от App:PublicUrl, Host запроса не влияет');
} else {
  if (!normal?.startsWith('https://localhost:5443')) throw new Error('кэш отравлен: ' + normal);
  console.log('✓ чужой Host не попал в ответ другим (кэш раздельный)');
}
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
