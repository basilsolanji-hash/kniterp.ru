// D343: позиция с остатком уходит в архив только после подтверждения — панель с последствиями, «Отмена» ничего не меняет.
// Позиция с остатком берётся из базы e2e (sqlcmd, база — из $E2E_DB); в архив сценарий её не отправляет.
import { ownerSession, collectErrors, assertNoHorizontalScroll, ready, BASE } from '../lib.mjs';
import { execFileSync } from 'node:child_process';

const db = process.env.E2E_DB ?? 'kniterp_e2e_set2';
const code = execFileSync('docker', ['exec', 'sql', '/opt/mssql-tools18/bin/sqlcmd', '-C', '-I', '-b', '-h', '-1', '-W', '-f', '65001', '-S', 'localhost', '-U', 'sa',
  '-P', process.env.KNITERP_SA_PASSWORD, '-d', db, '-Q',
  `SET NOCOUNT ON; SELECT TOP 1 i.Code FROM kniterp.items i JOIN kniterp.stock_movements m ON m.ItemId = i.Id
   WHERE i.IsArchived = 0 AND i.ParentItemId IS NULL GROUP BY i.Id, i.Code HAVING SUM(m.Quantity) > 0 ORDER BY i.Id`]).toString().trim();
if (!code || code.includes(' ')) throw new Error('нет позиции с остатком: ' + code);

for (const width of [1440, 390]) {
  const { browser, page } = await ownerSession(width);
  const errors = collectErrors(page);
  const ok = (m) => console.log(`✓ ${width}: ${m}`);
  await page.goto(`${BASE}/catalog?search=${encodeURIComponent(code)}`); await ready(page);
  const row = page.locator('table tbody tr').filter({ hasText: code }).first();
  await row.getByRole('button', { name: 'В архив' }).click();
  await row.getByRole('button', { name: 'Да' }).click();
  const ask = page.locator('.archive-ask');
  await ask.waitFor();
  if (!(await ask.innerText()).includes('На складе остаток')) throw new Error('в предупреждении нет остатка: ' + await ask.innerText());
  ok('позиция с остатком: панель «уже в учёте» с причинами');
  if (width === 390) await assertNoHorizontalScroll(page, 'панель архива @390');
  await ask.getByRole('button', { name: 'Отмена' }).click();
  await page.waitForTimeout(300);
  if (await page.locator('.archive-ask').count()) throw new Error('панель не закрылась');
  if (!(await row.getByRole('button', { name: 'В архив' }).count())) throw new Error('позиция ушла в архив без подтверждения');
  ok('«Отмена» — позиция остаётся действующей');
  await browser.close();
  if (errors.length) { console.error(errors); process.exit(1); }
}
console.log('✓ ошибок JS: 0');
