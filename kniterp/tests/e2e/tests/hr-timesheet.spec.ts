import { expect, test } from '@playwright/test';
import { hasNoHorizontalScroll, openPage } from './blazor';

// Этап 3 модуля «Сотрудники»: графики работы и табель Т-12/Т-13 (D207–D209). Данные вымышленные.
test.describe.configure({ mode: 'serial' });

const stamp = Date.now().toString().slice(-6);
const department = `Цех табеля ${stamp}`;
const position = `Оверлочница ${stamp}`;
const lastName = `Табелева${stamp}`;
const now = new Date();
const monthStart = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-01`;

test('табель по графику 2/2: составить, исправить день, закрыть, исправительный табель, Т-13', async ({ page }) => {
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
  await page.getByRole('button', { name: 'Добавить в штат' }).click();
  await expect(page.getByRole('status')).toContainText('Строка добавлена');

  await openPage(page, '/hr/orders');
  await page.getByRole('button', { name: 'Новый приказ' }).click();
  await page.getByLabel('Дата приказа').fill(monthStart);
  await page.getByLabel('Дата приёма').fill(monthStart);
  await page.getByLabel('Табельный номер').fill(`T${stamp}`);
  await page.getByLabel('Фамилия').fill(lastName);
  await page.getByLabel('Имя', { exact: true }).fill('Вера');
  await page.getByLabel('Подразделение').selectOption({ label: department });
  await page.getByLabel('Должность').selectOption({ label: position });
  await page.getByRole('button', { name: 'Создать черновик' }).click();
  await page.waitForURL(/\/hr\/orders\/\d+$/);
  await page.waitForLoadState('networkidle');
  await page.getByRole('button', { name: 'Провести' }).click();
  await expect(page.getByRole('status')).toContainText('Приказ проведён');

  await openPage(page, '/hr/schedules');
  await page.getByLabel('Название').fill(`2/2 по 12 ч ${stamp}`);
  await page.getByLabel('Вид').selectOption({ label: 'Сменный' });
  await page.getByLabel('Часов в смене').fill('12');
  await page.getByLabel('Первый день цикла').fill(monthStart);
  await page.getByRole('button', { name: 'Добавить график' }).click();
  await expect(page.getByRole('status')).toContainText('График добавлен');
  const row = page.getByRole('row', { name: new RegExp(lastName) });
  await row.getByRole('combobox').selectOption({ label: `2/2 по 12 ч ${stamp}` });
  await row.locator('input[type=date]').fill(monthStart);
  await row.getByRole('button', { name: 'Назначить' }).click();
  await expect(page.getByRole('status')).toContainText('График назначен');

  await openPage(page, '/hr/timesheet');
  await page.getByLabel('Подразделение').selectOption({ label: department });
  await page.getByRole('button', { name: 'Показать' }).click();
  await page.getByRole('button', { name: 'Составить табель по графикам' }).click();
  await expect(page.getByRole('status')).toContainText('Табель составлен');
  const line = page.getByRole('row', { name: new RegExp(lastName) });
  await expect(line.getByRole('button', { name: 'Я 12' }).first()).toBeVisible();

  await line.getByRole('button', { name: 'Я 12' }).first().click();
  await page.getByLabel('Код').selectOption('Б');
  await page.getByLabel('Часов', { exact: true }).fill('0');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('День исправлен');
  await expect(line.getByRole('button', { name: 'Б*' })).toBeVisible();

  await page.getByRole('button', { name: 'Закрыть табель' }).click();
  await expect(page.getByRole('status')).toContainText('Табель закрыт');
  await expect(line.getByRole('button')).toHaveCount(0);
  await page.getByRole('button', { name: 'Исправительный табель' }).click();
  await expect(page.getByRole('status')).toContainText('исправительный');
  await expect(page.getByText('Исправительный № 1', { exact: true }).first()).toBeVisible();

  const href = await page.getByRole('link', { name: 'Печать Т-13' }).getAttribute('href');
  await page.goto(`/${href}`);
  await expect(page.getByText('ТАБЕЛЬ УЧЁТА РАБОЧЕГО ВРЕМЕНИ (ИСПРАВИТЕЛЬНЫЙ № 1)')).toBeVisible();
  await expect(page.getByText('0301008')).toBeVisible();
});

test('табель на телефоне: прокрутка внутри таблицы, не страницы @phone', async ({ page }) => {
  await openPage(page, '/hr/timesheet');
  await page.getByLabel('Подразделение').selectOption({ index: 1 });
  await page.getByRole('button', { name: 'Показать' }).click();
  await page.waitForTimeout(500);
  expect(await hasNoHorizontalScroll(page)).toBe(true);
});
