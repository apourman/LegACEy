import type { Locator } from "@playwright/test";
import { expect, signIn, test } from "./fixtures";
import { checkA11y } from "../a11y";
import {
    mutateGlobalState,
    readGlobalState,
    withRestoredGlobalState,
} from "./live-global-state";

// The global-state project runs these one at a time, after the parallel projects finish (playwright.e2e.config.ts).
const accounts = ["global-state-global-alpha", "global-state-global-bravo"];
const [player] = accounts;

async function delistAll(delist: Locator) {
    for (let listed = await delist.count(); listed > 0; listed--) {
        await delist.first().click();
        await expect(delist).toHaveCount(listed - 1);
    }
}

test("shows the paused banner while the market is paused", async ({ page }) => {
    const before = readGlobalState(accounts);
    await signIn(page, player);
    await page.goto("/");
    await withRestoredGlobalState(accounts, async () => {
        mutateGlobalState(accounts, ["pause"]);
        await page.reload();
        await expect(
            page
                .getByRole("status")
                .filter({ hasText: "The market is paused" }),
        ).toBeVisible();
        await checkA11y(page);
    });
    expect(readGlobalState(accounts)).toEqual(before);
});

test("ends a browser session when its account is banned", async ({ page }) => {
    const before = readGlobalState(accounts);
    await signIn(page, player);
    await page.goto("/");
    await checkA11y(page);
    await withRestoredGlobalState(accounts, async () => {
        mutateGlobalState([player], ["bans"]);
        await page.reload();
        await expect(
            page.getByRole("link", { name: "Sign in", exact: true }),
        ).toBeVisible();
        await checkA11y(page);
    });
    expect(readGlobalState(accounts)).toEqual(before);
});

test("refuses a new listing over the active listing setting", async ({
    page,
}) => {
    const before = readGlobalState(accounts);
    await signIn(page, player);
    await page.goto("/vault");
    await expect(page.getByRole("heading", { name: "Vault" })).toBeVisible();
    await checkA11y(page);
    const row = (item: string) =>
        page.locator("tbody tr").filter({ hasText: item }).first();
    const delist = page.getByRole("button", { name: "Delist" });
    // start from no listings, whatever earlier tests left (a ban also returns them)
    await delistAll(delist);
    await withRestoredGlobalState(accounts, async () => {
        mutateGlobalState(accounts, ["settings"]); // one active listing
        await row("Kite Shield").locator("input[type=number]").fill("37");
        await row("Kite Shield")
            .getByRole("button", { name: "List item" })
            .click();
        await expect(delist).toHaveCount(1);
        await row("Chainmail Basinet").locator("input[type=number]").fill("38");
        await row("Chainmail Basinet")
            .getByRole("button", { name: "List item" })
            .click();
        await expect(page.getByRole("alert")).toContainText(
            "You have reached your active listing limit.",
        );
        await expect(delist).toHaveCount(1);
    });
    expect(readGlobalState(accounts)).toEqual(before);
    await delistAll(delist);
});

test("puts pause, bans and settings back after a step fails midway", async () => {
    const before = readGlobalState(accounts);
    await expect(
        withRestoredGlobalState(accounts, async () => {
            mutateGlobalState(accounts, ["pause", "settings", "bans"]);
            expect(readGlobalState(accounts)).not.toEqual(before);
            throw new Error("forced midway failure");
        }),
    ).rejects.toThrow("forced midway failure");
    expect(readGlobalState(accounts)).toEqual(before);
});
