import { expect, test } from '@playwright/test';
import { hasNoHorizontalScroll, openPage } from './blazor';

// Этап 4 модуля «Сотрудники»: расценки, расчёт заработка, расчётный листок (D211–D213). Данные вымышленные.
test.describe.configure({ mode: 'serial' });

const stamp = Date.now().toString().slice(-6);

test('расценка, расчёт за месяц и расчётный листок', async ({ page }) => {
  await openPage(page, '/hr/piece-rates');
  await page.getByLabel('Код операции').fill(stamp);
  await page.getByLabel('Операция').fill(`Вязка шарфа ${stamp}`);
  await page.getByLabel('Цена, руб.').fill('85.5');
  await page.getByRole('button', { name: 'Добавить расценку' }).click();
  await expect(page.getByRole('status')).toContainText('Расценка добавлена');
  await expect(page.getByRole('cell', { name: `Вязка шарфа ${stamp}` })).toBeVisible();

  await openPage(page, '/hr/payroll');
  await page.getByRole('button', { name: /^(Рассчитать|Пересчитать)$/ }).click();
  await expect(page.getByRole('status')).toContainText('Рассчитано');
  await expect(page.getByText('Черновик')).toBeVisible();

  const slip = page.getByRole('link', { name: 'Листок' }).first();
  if (await slip.count()) {
    await page.goto('/' + (await slip.getAttribute('href')));
    await expect(page.getByText('РАСЧЁТНЫЙ ЛИСТОК', { exact: false })).toBeVisible();
    await expect(page.getByText('ЧЕРНОВИК')).toBeVisible();
  }
});

test('заработок на телефоне без горизонтальной прокрутки @phone', async ({ page }) => {
  await openPage(page, '/hr/payroll');
  await expect(page.getByRole('heading', { name: 'Заработок' })).toBeVisible();
  expect(await hasNoHorizontalScroll(page)).toBe(true);
  await openPage(page, '/hr/piece-rates');
  expect(await hasNoHorizontalScroll(page)).toBe(true);
});
