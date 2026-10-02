import { defineConfig } from '@playwright/test';
const port = process.env.MARKET_WEB_TEST_PORT ?? '5173';
export default defineConfig({
  testDir: './tests', use: { baseURL: `http://127.0.0.1:${port}`, hasTouch: true },
  webServer: { command: `npm run dev -- --host 127.0.0.1 --port ${port}`, url: `http://127.0.0.1:${port}`, reuseExistingServer: false },
});
