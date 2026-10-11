import { expect, test } from '@playwright/test';
import { hasNoHorizontalScroll, openPage } from './blazor';

// Этап 1 модуля «Сотрудники»: карточка, персональные данные, согласие, Т-2 (D200–D202). Данные вымышленные.
test.describe.configure({ mode: 'serial' });

const stamp = Date.now().toString().slice(-6);
const department = `Вязальный цех ${stamp}`;
const position = `Вязальщица ${stamp}`;
const lastName = `Тестова${stamp}`;

test('кадровик принимает сотрудника, заполняет ПДн и согласие, печатает Т-2', async ({ page }) => {
  await openPage(page, '/structure');
  await page.getByLabel('Новое подразделение').fill(department);
  await page.getByRole('button', { name: 'Добавить' }).first().click();
  await expect(page.getByText(department).filter({ visible: true }).first()).toBeVisible();
  await page.getByLabel('Новая должность').fill(position);
  await page.getByRole('button', { name: 'Добавить' }).nth(1).click();
  await expect(page.getByText(position).filter({ visible: true }).first()).toBeVisible();

  // Если в организации уже есть штатное расписание (D203), принять можно только на должность из штата.
  await openPage(page, '/hr/staffing');
  await page.getByLabel('Подразделение').selectOption({ label: department });
  await page.getByLabel('Должность').selectOption({ label: position });
  await page.getByRole('button', { name: 'Добавить в штат' }).click();
  await expect(page.getByRole('status')).toContainText('Строка добавлена');

  await openPage(page, '/employees');
  await page.getByRole('button', { name: 'Принять сотрудника' }).click();
  await page.getByLabel('Табельный номер').fill(`E${stamp}`);
  await page.getByLabel('Фамилия').fill(lastName);
  await page.getByLabel('Имя', { exact: true }).fill('Анна');
  await page.getByLabel('Подразделение').selectOption({ label: department });
  await page.getByLabel('Должность').selectOption({ label: position });
  await page.getByRole('button', { name: 'Принять', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('принят');

  await page.getByRole('link', { name: new RegExp(lastName) }).click();
  await page.waitForURL(/\/employees\/\d+$/);
  await page.waitForLoadState('networkidle');
  await expect(page.getByRole('heading', { level: 1 })).toContainText(lastName);
  await expect(page.getByRole('alert')).toContainText('Нет действующего согласия');

  await page.getByRole('button', { name: 'Показать персональные данные' }).click();
  await page.getByLabel('СНИЛС').fill('112-233-445 96');
  await page.getByRole('button', { name: 'Сохранить персональные данные' }).click();
  await expect(page.locator('form .error')).toContainText('СНИЛС: 11 цифр');

  await page.getByLabel('СНИЛС').fill('112-233-445 95');
  await page.getByLabel('Телефон').fill('+7 (900) 000-00-00');
  await page.getByLabel('Место рождения').fill('г. Тестовск');
  await page.getByRole('button', { name: 'Сохранить персональные данные' }).click();
  await expect(page.getByRole('status')).toContainText('Персональные данные сохранены');

  await page.getByRole('button', { name: 'Отметить согласие' }).click();
  await expect(page.getByRole('status')).toContainText('Согласие отмечено');
  await expect(page.getByText('Согласие действует')).toBeVisible();
  await expect(page.getByText('Нет действующего согласия')).toHaveCount(0);

  const id = page.url().match(/employees\/(\d+)/)![1];
  await page.goto(`/print/employee-t2/${id}`);
  await expect(page.getByText('ЛИЧНАЯ КАРТОЧКА РАБОТНИКА')).toBeVisible();
  await expect(page.getByText('112-233-445 95')).toBeVisible();
  await expect(page.getByText('0301002')).toBeVisible();
});

test('просмотр ПДн попадает в журнал аудита', async ({ page }) => {
  await openPage(page, '/audit');
  await expect(page.getByText('Просмотр персональных данных').first()).toBeVisible();
  await expect(page.getByText('Печать личной карточки Т-2').first()).toBeVisible();
  // В журнале нет самого СНИЛС — только названия изменённых полей.
  await expect(page.getByText('11223344595')).toHaveCount(0);
});

test('карточка сотрудника на телефоне без горизонтальной прокрутки @phone', async ({ page }) => {
  await openPage(page, '/employees');
  expect(await hasNoHorizontalScroll(page)).toBe(true);
  await page.locator('table a[href^="employees/"]').first().click();
  await page.waitForURL(/\/employees\/\d+$/);
  await page.waitForLoadState('networkidle');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  expect(await hasNoHorizontalScroll(page)).toBe(true);
});
