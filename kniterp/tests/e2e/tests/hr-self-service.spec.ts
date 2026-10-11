import { expect, test } from '@playwright/test';
import { openPage } from './blazor';

// Полный месяц глазами сотрудника: приём → табель → закрытие → расчёт → утверждение → вход сотрудника → «Мой табель» и
// «Мой расчётный листок» (D208, D213). Отдельный месяц (март 2027): утверждение требует закрытых табелей всей организации.
test.describe.configure({ mode: 'serial' });

const stamp = Date.now().toString().slice(-6);
const department = `Цех самообслуживания ${stamp}`;
const position = `Петельщица ${stamp}`;
const lastName = `Самсонова${stamp}`;
const email = `worker-${stamp}@test.local`;
const password = `Сотрудник-${stamp}-пароль`;
let inviteLink = '';

test('кадровик и бухгалтер проводят месяц и приглашают сотрудника', async ({ page }) => {
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
  await page.getByLabel('Дата приёма').fill('2027-03-01');
  await page.getByLabel('Табельный номер').fill(`S${stamp}`);
  await page.getByLabel('Фамилия').fill(lastName);
  await page.getByLabel('Имя', { exact: true }).fill('Лидия');
  await page.getByLabel('Подразделение').selectOption({ label: department });
  await page.getByLabel('Должность').selectOption({ label: position });
  await page.getByLabel('Оклад (тарифная ставка), руб.').fill('30000');
  await page.getByRole('button', { name: 'Создать черновик' }).click();
  await page.waitForURL(/\/hr\/orders\/\d+$/);
  await page.waitForLoadState('networkidle');
  await page.getByRole('button', { name: 'Провести' }).click();
  await expect(page.getByRole('status')).toContainText('Приказ проведён');

  await openPage(page, '/hr/timesheet');
  await page.getByLabel('Подразделение').selectOption({ label: department });
  await page.getByLabel('Месяц').fill('2027-03');
  await page.getByRole('button', { name: 'Показать' }).click();
  await page.getByRole('button', { name: 'Составить табель по графикам' }).click();
  await expect(page.getByRole('status')).toContainText('Табель составлен');
  await page.getByRole('button', { name: 'Закрыть табель' }).click();
  await expect(page.getByRole('status')).toContainText('Табель закрыт');

  await openPage(page, '/hr/payroll');
  await page.getByLabel('Месяц').fill('2027-03');
  await page.getByRole('button', { name: 'Показать' }).click();
  await page.getByRole('button', { name: 'Рассчитать' }).click();
  await expect(page.getByRole('status')).toContainText('Рассчитано');
  await expect(page.getByRole('row', { name: new RegExp(lastName) })).toContainText('30 000,00');
  await page.getByRole('button', { name: 'Утвердить' }).click();
  await expect(page.getByRole('status')).toContainText('Расчёт утверждён');

  await openPage(page, '/users');
  await page.getByRole('button', { name: 'Добавить пользователя' }).click();
  await page.getByRole('combobox', { name: 'Сотрудник', exact: true }).selectOption({ label: `${lastName} Лидия` });
  await page.getByLabel('Email для приглашения').fill(email);
  await page.getByRole('combobox', { name: 'Роль (шаблон)' }).selectOption({ label: 'Сотрудник' });
  await page.getByRole('button', { name: 'Пригласить' }).click();
  inviteLink = await page.locator('#invite-link').inputValue();
  expect(inviteLink).toContain('/account/invite?token=');
});

test('сотрудник видит только свой табель и расчётный листок', async ({ browser, baseURL }) => {
  const context = await browser.newContext({ baseURL, storageState: { cookies: [], origins: [] }, ignoreHTTPSErrors: true, locale: 'ru-RU' });
  const page = await context.newPage();
  await page.goto(inviteLink);
  await page.getByLabel('Новый пароль').fill(password);
  await page.getByLabel('Повторите пароль').fill(password);
  await page.getByRole('button', { name: 'Сохранить пароль' }).click();
  await page.getByText('Пароль установлен.').waitFor();
  await page.goto('/account/login');
  await page.getByLabel('Эл. почта').fill(email);
  await page.getByLabel('Пароль').fill(password);
  await page.getByRole('button', { name: 'Войти' }).click();
  await page.waitForURL((url) => !url.pathname.startsWith('/account'));

  await openPage(page, '/hr/timesheet');
  await expect(page.getByRole('heading', { name: 'Мой табель' })).toBeVisible();
  await page.getByLabel('Месяц').fill('2027-03');
  await page.getByRole('button', { name: 'Показать' }).click();
  await expect(page.getByText('табель закрыт')).toBeVisible();

  await openPage(page, '/hr/payroll');
  await expect(page.getByRole('heading', { name: 'Мой расчётный листок' })).toBeVisible();
  await page.getByLabel('Месяц').fill('2027-03');
  await page.getByRole('button', { name: 'Показать' }).click();
  await expect(page.getByText('Всего начислено')).toBeVisible();
  await expect(page.getByText('30 000,00').first()).toBeVisible();

  // Чужие разделы закрыты: приказы и расчёт организации.
  await openPage(page, '/hr/orders');
  await expect(page.getByRole('alert')).toContainText('Недостаточно прав');
  await expect(page.getByRole('button', { name: 'Новый приказ' })).toHaveCount(0);
  await openPage(page, '/hr/staffing');
  await expect(page.getByRole('alert')).toContainText('Недостаточно прав');
  await context.close();
});
