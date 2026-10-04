import AxeBuilder from "@axe-core/playwright";
import { expect, type Page } from "@playwright/test";

/** Run the same accessibility contract after every user-visible page transition. */
export async function checkA11y(page: Page) {
    const results = await new AxeBuilder({ page }).analyze();
    expect(
        results.violations,
        results.violations.map((v) => `${v.id}: ${v.help}`).join("\n"),
    ).toEqual([]);
}
