// Снимки экранов для проверки раскладки: node screens.mjs [ширина] [путь …]. Снимки — в $E2E_DIR/shots/<ширина>-<путь>.png.
// Без путей — основные экраны каталога и общие. Отчёт: доля пустой ширины справа (самый правый край содержимого .main).
import { ownerSession, collectErrors, ready, BASE } from '../lib.mjs';
import { mkdirSync } from 'node:fs';

const width = Number(process.argv[2] ?? 1920);
const paths = process.argv.slice(3).length ? process.argv.slice(3) : [
  '/', '/catalog', '/catalog/items/1', '/catalog/settings', '/catalog/labels', '/catalog/price-list', '/exchange/feeds',
  '/exchange/moysklad', '/counterparties', '/warehouses', '/stock', '/sales/orders',
];
const dir = `${process.env.E2E_DIR}/shots`;
mkdirSync(dir, { recursive: true });
const { browser, page } = await ownerSession(width);
await page.setViewportSize({ width, height: 1000 });
const errors = collectErrors(page);
for (const p of paths) {
  await page.goto(BASE + p); await ready(page); await page.waitForTimeout(600);
  const m = await page.evaluate(() => {
    const main = document.querySelector('.main') ?? document.body;
    const box = main.getBoundingClientRect();
    let right = box.left;
    for (const el of main.querySelectorAll('*')) {
      const r = el.getBoundingClientRect();
      if (r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden' && el.children.length === 0) right = Math.max(right, r.right);
    }
    return { main: Math.round(box.width), used: Math.round(right - box.left) };
  });
  const name = `${width}-${p.replace(/\W+/g, '_') || 'root'}.png`;
  await page.screenshot({ path: `${dir}/${name}`, fullPage: false });
  console.log(`${p}\tширина .main ${m.main}px, занято до ${m.used}px (${Math.round(100 * m.used / m.main)} %)\t${name}`);
}
await browser.close();
if (errors.length) { console.error(errors); process.exit(1); }
