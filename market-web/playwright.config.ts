import { defineConfig } from '@playwright/test';

// MARKET_WEB_TEST_PORT: run the checks beside a dev server that already holds 5173.
// The BFF's server renders pages itself, so the checks script the Market API, not the browser's requests: the BFF under test points at a fake
// API (bff-tests/fake-api.ts) that each check scripts, on MARKET_WEB_TEST_API_PORT (default: the test port + 1). One worker, as there is one fake.
const port = process.env.MARKET_WEB_TEST_PORT ?? '5173';
process.env.MARKET_WEB_TEST_API_PORT ??= String(Number(port) + 1);

export default defineConfig({
    // tests/ also holds Vitest's *.test.ts(x) and the end-to-end suite (tests/e2e, playwright.e2e.config.ts): only *.spec files outside e2e are ours.
    testDir: './tests',
    testMatch: '**/*.spec.ts',
    testIgnore: '**/e2e/**',
    workers: 1,
    use: { baseURL: `http://127.0.0.1:${port}`, hasTouch: true },
    webServer: {
        command: `node server.js --port ${port}`,
        url: `http://127.0.0.1:${port}/health`,
        reuseExistingServer: false,
        env: {
            HOST: '127.0.0.1',
            MARKET_API_URL: `http://127.0.0.1:${process.env.MARKET_WEB_TEST_API_PORT}`,
            // test-only values for a fake API; never the real ones
            MARKET_SERVICE_KEY: 'browser-checks-service-key-0123456789abcdef0123',
            MARKET_COOKIE_SECRET: 'browser-checks-cookie-secret-0123456789abcdef01',
        },
    },
});
