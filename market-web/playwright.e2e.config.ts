import { defineConfig, devices } from "@playwright/test";

const baseURL = process.env.MARKET_E2E_URL ?? "http://127.0.0.1:5174";

export default defineConfig({
    testDir: "./tests/e2e",
    globalSetup: "./tests/e2e/global-setup.ts",
    fullyParallel: true,
    forbidOnly: !!process.env.CI,
    retries: process.env.CI ? 2 : 0,
    workers: process.env.CI ? 2 : undefined,
    reporter: "list",
    use: {
        baseURL,
        trace: "retain-on-failure",
        screenshot: "only-on-failure",
        video: "retain-on-failure",
    },
    projects: [
        {
            name: "desktop",
            testMatch: /journeys\.spec\.ts/,
            use: {
                ...devices["Desktop Chrome"],
                viewport: { width: 1280, height: 900 },
            },
        },
        {
            name: "phone",
            testMatch: /journeys\.spec\.ts/,
            use: { ...devices["Pixel 5"] },
        },
        {
            name: "global-state",
            testMatch: /global-state\.spec\.ts/,
            dependencies: ["desktop", "phone"],
            fullyParallel: false,
            workers: 1,
            use: { ...devices["Desktop Chrome"] },
        },
    ],
});
