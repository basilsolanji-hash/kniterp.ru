import { ownerSession, ready, BASE } from '../lib.mjs';
import fs from 'node:fs';
const out = (process.env.E2E_DIR ?? '/tmp/kniterp-e2e') + '/bcshots/'; fs.rmSync(out, { recursive: true, force: true }); fs.mkdirSync(out);
const ids = fs.readFileSync((process.env.E2E_DIR ?? '/tmp/kniterp-e2e') + '/bc-items.txt', 'utf8').trim().split('\n').map(l => l.split('|')[0].trim());
const { browser, page } = await ownerSession(1280);
const presets = [['', 'BC 40x30'], ['', 'BC 43x25'], ['', 'BC 30x20'], ['', 'BC A4 105x74'], ['', 'BC 58x40'], ['', 'BC A4 70x37']];
const state = await page.context().storageState();
const templates = {};
for (const [, name] of presets) templates[name] = null;
// id шаблонов — из выпадающего списка страницы печати.
await page.goto(`${BASE}/catalog/labels/print?ids=${ids[0]}`); await ready(page);
const options = await page.locator('select[name=template] option').evaluateAll(os => os.map(o => [o.textContent.trim(), o.value]));
for (const [text, value] of options) if (text in templates) templates[text] = value;
for (const dpi of [203, 300]) {
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, storageState: state, deviceScaleFactor: dpi / 96, viewport: { width: 900, height: 900 } });
  const p = await ctx.newPage();
  for (const [name, id] of Object.entries(templates)) {
    await p.goto(`${BASE}/catalog/labels/print?ids=${ids.join(',')}&template=${id}`); await ready(p);
    await p.emulateMedia({ media: 'print' });
    const labels = p.locator('.label-sheet .item-label');
    const n = await labels.count();
    for (let i = 0; i < n; i++) {
      await labels.nth(i).screenshot({ path: `${out}${dpi}_${name.replace(/\W+/g, '_')}_${ids[i]}.png` });
    }
    console.log(dpi, name, n);
  }
  await ctx.close();
}
await browser.close();
