import { expect, test as base, type Page } from "@playwright/test";
import { checkA11y } from "../a11y";

export const password = process.env.MARKET_SEED_PASSWORD ?? "marketdev";

export async function signIn(page: Page, account: string) {
    const response = await page.request.post("/auth/sign-in", {
        data: { account, password },
        headers: {
            Origin: process.env.MARKET_E2E_URL ?? "http://127.0.0.1:5174",
        },
    });
    expect(response.status(), await response.text()).toBe(200);
}

/** Playwright's test, plus an axe check of the page each passing test ends on (tests check the pages before it themselves). */
export const test = base.extend<{ finalPageA11y: void }>({
    finalPageA11y: [
        async ({ page }, use, testInfo) => {
            await use();
            if (
                testInfo.status === testInfo.expectedStatus &&
                page.url() !== "about:blank"
            )
                await checkA11y(page);
        },
        { auto: true },
    ],
});

export { expect };
