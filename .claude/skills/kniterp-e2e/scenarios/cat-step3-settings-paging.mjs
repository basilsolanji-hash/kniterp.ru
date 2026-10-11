// D312, D313: общие настройки каталога (автокод, выключенные комплекты) и серверные страницы списка номенклатуры.
// Для пейджера в базу добавляются 150 позиций одним INSERT через sqlcmd в контейнере sql (база — из $E2E_DB).
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';
import { execFileSync } from 'node:child_process';

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const sfx = Date.now().toString(36).slice(-4).toUpperCase();
const prefix = 'E' + sfx + '-';

await page.goto(`${BASE}/catalog/settings?tab=General`); await ready(page);
await page.locator('label:has-text("Префикс автокода") input').fill(prefix.toLowerCase());
await page.locator('label:has-text("Цифр в номере") select').selectOption('4');
await page.locator('label:has-text("Следующий номер") input').fill('1');
await page.keyboard.press('Tab'); // @bind срабатывает по change — при уходе из поля
await page.locator('label:has-text("Комплекты") input[type=checkbox]').uncheck();
await page.waitForSelector(`text=Пример кода: ${prefix}0001`);
await page.getByRole('button', { name: 'Сохранить настройки' }).click();
await page.waitForSelector('text=Настройки каталога сохранены.');
ok('настройки сохранены, пример автокода виден');

await page.goto(`${BASE}/catalog`); await ready(page);
await page.getByRole('button', { name: 'Добавить позицию' }).click();
const form = page.locator('.panel form');
const code = form.locator('label:has-text("Код") input');
if (await code.getAttribute('placeholder') !== `авто: ${prefix}0001`) throw new Error('нет подсказки автокода: ' + await code.getAttribute('placeholder'));
const types = await form.locator('label:has-text("Тип") select option').allTextContents();
if (types.some(t => t.includes('Комплект'))) throw new Error('выключенный вид в списке типов: ' + types.join(', '));
await form.locator('label:has-text("Наименование") input').fill('Позиция с автокодом ' + sfx);
await form.getByRole('button', { name: 'Сохранить' }).click();
await page.waitForSelector('text=Позиция сохранена.');
await page.locator('label:has-text("Поиск") input').fill(prefix);
await page.getByRole('button', { name: 'Найти' }).click();
await page.waitForSelector(`td:text-is("${prefix}0001")`);
ok('новая позиция без кода получила автокод, комплектов нет в выборе типа');

// 150 позиций для пейджера — копии только что созданной (латиница: sqlcmd без кодировки).
const db = process.env.E2E_DB ?? 'kniterp_e2e_set2';
const sql = `SET NOCOUNT ON; DECLARE @t bigint = (SELECT Id FROM kniterp.items WHERE Code = '${prefix}0001');
DECLARE @cols nvarchar(max) = (SELECT STRING_AGG(QUOTENAME(name), ',') FROM sys.columns WHERE object_id = OBJECT_ID('kniterp.items') AND is_identity = 0 AND is_computed = 0 AND TYPE_NAME(system_type_id) <> 'timestamp');
DECLARE @sel nvarchar(max) = (SELECT STRING_AGG(CASE name WHEN 'Code' THEN 'CONCAT(''PG${sfx}-'', n.n)' WHEN 'Name' THEN 'CONCAT(''Pager ${sfx} '', RIGHT(CONCAT(''000'', n.n), 3))' ELSE 't.' + QUOTENAME(name) END, ',') FROM sys.columns WHERE object_id = OBJECT_ID('kniterp.items') AND is_identity = 0 AND is_computed = 0 AND TYPE_NAME(system_type_id) <> 'timestamp');
DECLARE @q nvarchar(max) = N'WITH n AS (SELECT TOP (150) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects) INSERT INTO kniterp.items (' + @cols + ') SELECT ' + @sel + ' FROM n CROSS JOIN kniterp.items t WHERE t.Id = ' + CAST(@t AS nvarchar(20));
EXEC(@q);`;
execFileSync('docker', ['exec', 'sql', '/opt/mssql-tools18/bin/sqlcmd', '-C', '-I', '-b', '-S', 'localhost', '-U', 'sa', '-P', process.env.KNITERP_SA_PASSWORD, '-d', db, '-Q', sql]);

await page.goto(`${BASE}/catalog?search=PG${sfx}-`); await ready(page);
const pager = page.locator('nav.pager');
await pager.waitFor();
if (!/Позиции 1–100 из 150/.test(await pager.innerText())) throw new Error('пейджер: ' + await pager.innerText());
const firstPage = await page.locator('table tbody tr td:first-child').allTextContents();
await pager.getByRole('button', { name: 'Далее →' }).click();
await page.waitForSelector('nav.pager:has-text("Позиции 101–150 из 150")');
const secondPage = await page.locator('table tbody tr td:first-child').allTextContents();
if (secondPage.length !== 50 || secondPage.some(c => firstPage.includes(c))) throw new Error('вторая страница: ' + secondPage.length);
if (!(await pager.getByRole('button', { name: 'Далее →' }).isDisabled())) throw new Error('«Далее» доступна на последней странице');
ok('серверные страницы: 100 + 50, без повторов, «Далее» выключена на последней');

await page.setViewportSize({ width: 390, height: 844 });
for (const url of [`${BASE}/catalog/settings?tab=General`, `${BASE}/catalog?search=PG${sfx}-`]) {
  await page.goto(url); await ready(page);
  await assertNoHorizontalScroll(page, url);
}
ok('390 px: без горизонтальной прокрутки');
await browser.close();
if (errors.length) { console.error('Ошибки JS:', errors); process.exit(1); }
ok('ошибок JS: 0');
