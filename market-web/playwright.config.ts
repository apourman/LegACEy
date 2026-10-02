import { defineConfig } from '@playwright/test';
// MARKET_WEB_TEST_PORT: run the checks beside a dev server that already holds 5173
const port = process.env.MARKET_WEB_TEST_PORT ?? '5173';
export default defineConfig({
  testDir: './tests', use: { baseURL: `http://127.0.0.1:${port}`, hasTouch: true },
  webServer: { command: `npm run dev -- --port ${port}`, url: `http://127.0.0.1:${port}`, reuseExistingServer: false },
});
