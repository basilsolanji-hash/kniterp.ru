import { expect, test } from '@playwright/test';
import { hasNoHorizontalScroll, openPage } from './blazor';

// Этап 2 модуля «Сотрудники»: штатное расписание Т-3 и приказы Т-1…Т-8 (D203–D206). Данные вымышленные.
test.describe.configure({ mode: 'serial' });

const stamp = Date.now().toString().slice(-6);
const department = `Цех приказов ${stamp}`;
const position = `Кеттлёрщица ${stamp}`;

test('штат на одну ставку: первый приказ о приёме проводится, второй — отказ «сверх штата»', async ({ page }) => {
  await openPage(page, '/structure');
  await page.getByLabel('Новое подразделение').fill(department);
  await page.getByRole('button', { name: 'Добавить' }).first().click();
  await expect(page.getByText(department).filter({ visible: true }).first()).toBeVisible();
  await page.getByLabel('Новая должность').fill(position);
  await page.getByRole('button', { name: 'Добавить' }).nth(1).click();
  await expect(page.getByText(position).filter({ visible: true }).first()).toBeVisible();

  await openPage(page, '/hr/staffing');
  await page.getByLabel('Подразделение').selectOption({ label: department });
  await page.getByLabel('Должность').selectOption({ label: position });
  await page.getByLabel('Ставок').fill('1');
  await page.getByLabel('Оклад, руб.').fill('42000');
  await page.getByRole('button', { name: 'Добавить в штат' }).click();
  await expect(page.getByRole('status')).toContainText('Строка добавлена');
  await expect(page.getByRole('cell', { name: position })).toBeVisible();

  for (const [number, lastName] of [[`P${stamp}`, `Приказова${stamp}`], [`Q${stamp}`, `Лишняя${stamp}`]]) {
    await openPage(page, '/hr/orders');
    await page.getByRole('button', { name: 'Новый приказ' }).click();
    await page.getByLabel('Табельный номер').fill(number);
    await page.getByLabel('Фамилия').fill(lastName);
    await page.getByLabel('Имя', { exact: true }).fill('Ольга');
    await page.getByLabel('Подразделение').selectOption({ label: department });
    await page.getByLabel('Должность').selectOption({ label: position });
    await page.getByLabel('Оклад (тарифная ставка), руб.').fill('42000');
    await page.getByLabel('Основание (документ)').fill('Трудовой договор № 1');
    await page.getByRole('button', { name: 'Создать черновик' }).click();
    await page.waitForURL(/\/hr\/orders\/\d+$/);
    await page.waitForLoadState('networkidle');
    await expect(page.getByText('Черновик').first()).toBeVisible();
    await page.getByRole('button', { name: 'Провести' }).click();
    if (number.startsWith('P')) {
      await expect(page.getByRole('status')).toContainText('Приказ проведён');
      await expect(page.getByRole('link', { name: new RegExp(lastName) })).toBeVisible();
      // Карточка сотрудника показывает историю назначений со ссылкой на приказ (D214).
      await page.getByRole('link', { name: new RegExp(lastName) }).click();
      await page.waitForURL(/\/employees\/\d+$/);
      await page.waitForLoadState('networkidle');
      await expect(page.getByRole('heading', { name: 'История назначений' })).toBeVisible();
      await expect(page.getByRole('link', { name: /Приём на работу №/ })).toBeVisible();
    } else {
      await expect(page.getByRole('alert')).toContainText('Сверх штата');
    }
  }

  await openPage(page, '/hr/staffing');
  const row = page.getByRole('row', { name: new RegExp(position) });
  await expect(row).toContainText('42 000,00');
});

test('печать приказа Т-1 и штатного расписания Т-3', async ({ page }) => {
  await openPage(page, '/hr/orders');
  await page.getByRole('row', { name: /Приказова/ }).getByRole('link').first().click();
  await page.waitForURL(/\/hr\/orders\/\d+$/);
  const id = page.url().match(/orders\/(\d+)/)![1];
  await page.goto(`/print/hr-order/${id}`);
  await expect(page.getByText('о приёме работника на работу')).toBeVisible();
  await expect(page.getByText('0301001')).toBeVisible();
  await expect(page.getByText('ЧЕРНОВИК')).toHaveCount(0);

  await page.goto('/print/staffing');
  await expect(page.getByText('ШТАТНОЕ РАСПИСАНИЕ')).toBeVisible();
  await expect(page.getByText(position)).toBeVisible();
});

test('список приказов на телефоне без горизонтальной прокрутки @phone', async ({ page }) => {
  await openPage(page, '/hr/orders');
  await expect(page.getByRole('heading', { name: 'Кадровые приказы' })).toBeVisible();
  expect(await hasNoHorizontalScroll(page)).toBe(true);
  await openPage(page, '/hr/staffing');
  expect(await hasNoHorizontalScroll(page)).toBe(true);
});
