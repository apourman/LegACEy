import { test as base, expect } from '@playwright/test';
import { startFakeApi, type FakeApi } from '../bff-tests/fake-api';

// The browser checks run the real BFF (server.js, started by playwright.config.ts) against a fake Market API that each check scripts.
// The BFF renders pages and signs in on the server, where the browser's page.route can't reach, so the API's answers are scripted here instead.

/** What the API's session sign-in answers; the BFF keeps the token in its HttpOnly cookie */
export const session = {
  token: 'ws.YnJvd3Nlci1jaGVja3MtdG9rZW4tMDEyMzQ1Njc4OWFiY2RlZjAxMjM',
  accountId: 1, accountName: 'Alpha',
  idleExpiresTime: new Date(Date.now() + 14 * 86_400_000).toISOString(),
  absoluteExpiresTime: new Date(Date.now() + 30 * 86_400_000).toISOString(),
};

export const test = base.extend<{ api: FakeApi; signIn: () => Promise<void> }, { fakeApi: FakeApi }>({
  fakeApi: [async ({}, use) => {
    const api = await startFakeApi(Number(process.env.MARKET_WEB_TEST_API_PORT));
    await use(api);
    await api.close();
  }, { scope: 'worker' }],
  // the server's HTML shows controls before the page's script has hydrated them: every goto waits until the page is interactive
  page: async ({ page }, use) => {
    const goto = page.goto.bind(page);
    page.goto = async (url, options) => {
      const response = await goto(url, options);
      await page.waitForSelector('html[data-hydrated]', { state: 'attached' });
      return response;
    };
    await use(page);
  },
  api: async ({ fakeApi }, use) => {
    fakeApi.reset();
    await use(fakeApi);
  },
  /** Signs the browser in through the BFF, as an earlier visit would have: the session cookie is in the browser before the page loads */
  signIn: async ({ page, baseURL }, use) => {
    await use(async () => {
      const response = await page.request.post('/auth/sign-in', { data: { account: 'Alpha', password: 'pass' }, headers: { Origin: baseURL! } });
      expect(response.status(), await response.text()).toBe(200);
      expect((await page.context().cookies()).some(c => c.name === 'market_session' && c.httpOnly)).toBe(true);
    });
  },
});

export { expect };
