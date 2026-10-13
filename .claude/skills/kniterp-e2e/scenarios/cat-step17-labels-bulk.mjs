// Аудит каталога (D349): печать на предел — 1000 позиций × 5 копий = 5000 этикеток открывается за секунды, без ошибок JS;
// 5001-я этикетка — понятный отказ. Позиции берутся из базы e2e (sqlcmd, база — из $E2E_DB).
import { ownerSession, collectErrors, BASE } from '../lib.mjs';
import { execFileSync } from 'node:child_process';

const db = process.env.E2E_DB ?? 'kniterp_e2e_set2';
const ids = execFileSync('docker', ['exec', 'sql', '/opt/mssql-tools18/bin/sqlcmd', '-C', '-I', '-b', '-h', '-1', '-W', '-S', 'localhost', '-U', 'sa',
  '-P', process.env.KNITERP_SA_PASSWORD, '-d', db, '-Q',
  `SET NOCOUNT ON; SELECT STRING_AGG(CAST(Id AS varchar(20)), ',') FROM (SELECT TOP 1000 i.Id FROM kniterp.items i
   WHERE i.IsArchived = 0 AND i.Type <> 10 AND i.OrganizationId = (SELECT TOP 1 OrganizationId FROM kniterp.items GROUP BY OrganizationId ORDER BY COUNT(*) DESC) ORDER BY i.Id) x`]).toString().trim();
const count = ids.split(',').length;
if (count < 1000) throw new Error('в базе e2e меньше 1000 позиций: ' + count);

const { browser, page } = await ownerSession(1440);
const errors = collectErrors(page);
const ok = (m) => console.log('✓ ' + m);
const started = Date.now();
await page.goto(`${BASE}/catalog/labels/print?ids=${ids}&copies=5`, { timeout: 120000 });
await page.waitForFunction(() => document.querySelectorAll('.label-sheet .item-label').length === 5000, null, { timeout: 120000 });
const ms = Date.now() - started;
ok(`5000 этикеток (1000 позиций × 5) открылись за ${ms} мс`);
if (ms > 30000) throw new Error(`слишком долго: ${ms} мс`);

await page.goto(`${BASE}/catalog/labels/print?ids=${ids}&copies=6`, { timeout: 120000 });
await page.waitForSelector('text=За раз — не больше 5000 этикеток');
ok('6000 этикеток — понятный отказ');
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
console.log('✓ ошибок JS: 0');
