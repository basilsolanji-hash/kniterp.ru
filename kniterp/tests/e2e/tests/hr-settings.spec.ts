import { expect, test } from '@playwright/test';
import { hasNoHorizontalScroll, openPage } from './blazor';

// Настройки «Кадры и заработок» (D216): минимумы ТК, сохранение, Excel для сверки. В конце значения возвращаются.
test.describe.configure({ mode: 'serial' });

test('доплаты не ниже минимума ТК, сохранение и Excel для сверки', async ({ page }) => {
  await openPage(page, '/hr/settings');
  const night = page.getByLabel('Ночные часы, % часовой ставки');
  const save = page.getByRole('button', { name: 'Сохранить параметры заработка' });

  await night.fill('15');
  await save.click();
  await expect(page.getByRole('alert')).toContainText('не меньше 20 %');

  await night.fill('25');
  await save.click();
  await expect(page.getByRole('status')).toContainText('Параметры заработка сохранены');

  await openPage(page, '/hr/settings');
  await expect(page.getByLabel('Ночные часы, % часовой ставки')).toHaveValue(/^25(\.0+)?$/);

  await page.getByLabel('Месяц').selectOption({ index: 0 });
  const download = page.waitForEvent('download');
  await page.getByRole('link', { name: 'Расчёт в Excel для сверки' }).click();
  expect((await download).suggestedFilename()).toMatch(/\.xlsx$/);

  await page.getByLabel('Ночные часы, % часовой ставки').fill('20');
  await save.click();
  await expect(page.getByRole('status')).toContainText('Параметры заработка сохранены');
});

test('настройки кадров на телефоне без горизонтальной прокрутки @phone', async ({ page }) => {
  await openPage(page, '/hr/settings');
  await expect(page.getByRole('heading', { name: 'Кадры и заработок: настройки' })).toBeVisible();
  expect(await hasNoHorizontalScroll(page)).toBe(true);
});
