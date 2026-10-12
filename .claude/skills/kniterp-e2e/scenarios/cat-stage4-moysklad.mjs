// Этап 4 (D310): страница «Загрузка из МойСклад» — пункт меню, проверка формата токена, пустой список загрузок, телефон 390 px.
// Подключение к настоящему МойСклад не проверяется (в тестах нет реального токена); сценарий загрузки — интеграционные тесты с подделкой.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);

await page.goto(`${BASE}/exchange/moysklad`); await ready(page);
await page.waitForSelector('h1:has-text("Загрузка из МойСклад")');
// Группа меню текущей страницы раскрыта: пункт есть и отмечен текущим.
if (await page.locator('a[href="exchange/moysklad"][aria-current="page"]').count() === 0) throw new Error('нет пункта меню «Загрузка из МойСклад»');
ok('пункт меню у Владельца есть');
await page.waitForSelector('text=Загрузок ещё не было.');
const tokenInput = page.locator('label:has-text("Токен МойСклад") input');
if (await tokenInput.getAttribute('type') !== 'password') throw new Error('поле токена не скрывает ввод');
if (await tokenInput.getAttribute('autocomplete') !== 'off') throw new Error('поле токена с автозаполнением');
ok('токен вводится скрыто, без автозаполнения');

await tokenInput.fill('короткий токен');
await page.getByRole('button', { name: 'Проверить и подключить' }).click();
await page.waitForSelector('.flash.error:has-text("Токен — строка из латинских букв")');
if (await tokenInput.inputValue() !== '') throw new Error('токен остался в поле после ошибки');
if (await page.getByRole('button', { name: 'Загрузить' }).count() !== 0) throw new Error('кнопка загрузки без подключения');
ok('неверный формат токена: понятная ошибка, поле очищено, загрузка недоступна');

// Запрос с cookie сессии, но вне страницы — чтобы ожидаемый 404 не попал в ошибки консоли.
const resp = await page.request.get(`${BASE}/exchange/moysklad/runs/999999/report.xlsx`);
if (resp.status() !== 404) throw new Error('отчёт чужой/несуществующей загрузки: ' + resp.status());
ok('отчёт несуществующей загрузки — 404');

await page.setViewportSize({ width: 390, height: 844 });
await page.goto(`${BASE}/exchange/moysklad`); await ready(page);
await assertNoHorizontalScroll(page, 'moysklad');
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
