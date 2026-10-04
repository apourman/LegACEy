import { expect, test as base, type Page } from '@playwright/test';
import { checkA11y } from '../a11y';

export const password = process.env.MARKET_SEED_PASSWORD ?? 'marketdev';

export async function signIn(page: Page, account: string) {
  const response = await page.request.post('/auth/sign-in', {
    data: { account, password },
    headers: { Origin: process.env.MARKET_E2E_URL ?? 'http://127.0.0.1:5174' },
  });
  expect(response.status(), await response.text()).toBe(200);
}

export const test = base;
test.afterEach(async ({ page }, testInfo) => {
  if (testInfo.status === 'skipped') return;
  await checkA11y(page);
});

export { expect };
