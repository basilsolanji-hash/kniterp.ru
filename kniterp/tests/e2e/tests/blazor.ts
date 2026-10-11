import type { Page } from '@playwright/test';

/** Интерактивная страница Blazor готова: меню загрузилось (оно стартует первым), цепь SignalR подключена. */
export async function openPage(page: Page, path: string) {
  await page.goto(path);
  await page.waitForLoadState('networkidle');
  await page.waitForFunction(() => (window as any).Blazor !== undefined);
  await page.waitForTimeout(300);
}

/** Страница не прокручивается по горизонтали (телефон 390 px). */
export async function hasNoHorizontalScroll(page: Page) {
  return page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1);
}
