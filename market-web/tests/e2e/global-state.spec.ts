import { expect, signIn, test } from "./fixtures";
import { checkA11y } from "../a11y";
import { withRestoredGlobalState } from "../../src/global-state";
import {
    mutateGlobalState,
    readGlobalState,
    restoreGlobalState,
} from "./live-global-state";

const accounts = ["global-state-global-alpha", "global-state-global-bravo"];

test("the global-state account starts unpaused and can end its session cleanly", async ({
    page,
}) => {
    await signIn(page, accounts[0]);
    await page.goto("/");
    await checkA11y(page);
    await expect(page.getByText("Market paused")).toHaveCount(0);
    await page.getByRole("button", { name: "Sign out" }).click();
    await expect(
        page.getByRole("link", { name: "Sign in", exact: true }),
    ).toBeVisible();
    await checkA11y(page);
});

test("shows the paused banner while restoring the live setting", async ({
    page,
}) => {
    const before = await readGlobalState(accounts);
    await signIn(page, accounts[0]);
    await page.goto("/");
    await withRestoredGlobalState(
        before,
        async () => {
            await mutateGlobalState(accounts.slice(0, 1), ["pause"]);
            await page.reload();
            await checkA11y(page);
            await expect(
                page
                    .getByRole("status")
                    .filter({ hasText: "The market is paused" }),
            ).toBeVisible();
        },
        (state) => restoreGlobalState(state, accounts),
    );
    expect(await readGlobalState(accounts)).toEqual(before);
});

test("ends a browser session when its account is banned, then restores the ban", async ({
    page,
}) => {
    const before = await readGlobalState(accounts);
    await signIn(page, accounts[0]);
    await page.goto("/");
    await checkA11y(page);
    await withRestoredGlobalState(
        before,
        async () => {
            await mutateGlobalState(accounts.slice(0, 1), ["bans"]);
            await page.reload();
            await expect(
                page.getByRole("link", { name: "Sign in", exact: true }),
            ).toBeVisible();
            await checkA11y(page);
        },
        (state) => restoreGlobalState(state, accounts),
    );
    expect(await readGlobalState(accounts)).toEqual(before);
});

test("restores pause, bans and settings after a forced midway failure", async ({
    page,
}) => {
    const before = await readGlobalState(accounts);
    await expect(
        withRestoredGlobalState(
            before,
            async () => {
                await mutateGlobalState(accounts);
                throw new Error("forced midway failure");
            },
            (state) => restoreGlobalState(state, accounts),
        ),
    ).rejects.toThrow("forced midway failure");
    expect(await readGlobalState(accounts)).toEqual(before);
    await page.goto("/");
});
