import { expect, test, type Page } from '@playwright/test';
import { hasNoHorizontalScroll, openPage } from './blazor';

// D400: карточка контрагента с договорами; меню на телефоне не прокручивает страницу под собой. Данные вымышленные.
test.describe.configure({ mode: 'serial' });

const stamp = Date.now().toString().slice(-6);
const customer = `ООО «Клиент ${stamp}»`;

/** Покупатель через форму списка и переход в его карточку по ссылке из списка. */
async function openNewCustomerCard(page: Page, name: string) {
  await openPage(page, '/counterparties?new=1');
  await page.getByLabel('Наименование').fill(name);
  await page.getByRole('checkbox', { name: 'Поставщик' }).uncheck();
  await page.getByRole('checkbox', { name: 'Покупатель' }).check();
  await page.getByRole('button', { name: 'Сохранить' }).click();
  await expect(page.getByRole('link', { name })).toBeVisible();
  await page.getByRole('link', { name }).click();
  await page.waitForURL(/\/counterparties\/\d+$/);
  await page.waitForLoadState('networkidle');
}

test('карточка контрагента: договор добавляется, закрывается и открывается снова', async ({ page }) => {
  await openNewCustomerCard(page, customer);
  await expect(page.getByRole('heading', { name: customer })).toBeVisible();
  await expect(page.getByRole('tab', { name: /Договоры/ })).toHaveAttribute('aria-selected', 'true');

  await page.getByRole('button', { name: 'Добавить договор' }).click();
  await page.getByLabel('Номер').fill(`Д-${stamp}`);
  await page.getByLabel('Отсрочка оплаты, дней').fill('30');
  await page.getByLabel('Предмет договора').fill('Поставка джемперов');
  await page.getByRole('button', { name: 'Сохранить' }).click();
  await expect(page.getByRole('status')).toContainText('Договор добавлен');
  const row = page.getByRole('row', { name: new RegExp(`Д-${stamp}`) });
  await expect(row).toContainText('30 дн.');
  await expect(row).toContainText('Действует');

  await row.getByRole('button', { name: 'Закрыть' }).click();
  await page.getByRole('button', { name: 'Да', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('Договор закрыт');
  await expect(row).toContainText('Закрыт');
  await row.getByRole('button', { name: 'Открыть снова' }).click();
  await expect(page.getByRole('status')).toContainText('Договор снова действует');

  // Вкладки истории открываются без ошибок.
  await page.getByRole('tab', { name: 'Заказы' }).click();
  await expect(page.getByText('Заказов нет.').first()).toBeVisible();
  await page.getByRole('tab', { name: 'Документы' }).click();
  await expect(page.getByRole('heading', { name: 'Счета покупателю' })).toBeVisible();
});

test('телефон: карточка без горизонтальной прокрутки, меню прокручивается само, страница под ним стоит @phone', async ({ page }) => {
  test.skip((page.viewportSize()?.width ?? 0) > 899, 'Кнопка «Меню» есть только на узком экране.');
  await openNewCustomerCard(page, `ООО «Телефон ${stamp}»`);
  expect(await hasNoHorizontalScroll(page)).toBe(true);

  // Страница прокручена вниз, затем открыто меню.
  await page.evaluate(() => window.scrollTo(0, 200));
  const before = await page.evaluate(() => window.scrollY);
  await page.getByRole('button', { name: 'Меню' }).click();
  const nav = page.locator('#nav-body');
  await expect(nav).toBeVisible();

  const state = await page.evaluate(() => {
    const side = document.querySelector('.side') as HTMLElement;
    const body = document.getElementById('nav-body') as HTMLElement;
    return {
      bodyOverflow: getComputedStyle(document.body).overflow,
      sidePosition: getComputedStyle(side).position,
      sideHeight: side.getBoundingClientRect().height,
      viewport: window.innerHeight,
      navOverscroll: getComputedStyle(body).overscrollBehaviorY,
      navScrollable: body.scrollHeight > body.clientHeight,
    };
  });
  expect(state.bodyOverflow).toBe('hidden');
  expect(state.sidePosition).toBe('fixed');
  expect(Math.abs(state.sideHeight - state.viewport)).toBeLessThanOrEqual(1);
  expect(state.navOverscroll).toBe('contain');

  // Прокрутка колесом/пальцем над меню прокручивает меню, а не страницу.
  const box = (await nav.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  for (let i = 0; i < 5; i++) {
    await page.mouse.wheel(0, 600);
  }
  await page.waitForTimeout(200);
  expect(await page.evaluate(() => window.scrollY)).toBe(before);
  if (state.navScrollable) {
    expect(await nav.evaluate(el => el.scrollTop)).toBeGreaterThan(0);
  }

  await page.getByRole('button', { name: 'Закрыть' }).first().click();
  await expect(nav).toBeHidden();
  expect(await page.evaluate(() => getComputedStyle(document.body).overflow)).not.toBe('hidden');
});
