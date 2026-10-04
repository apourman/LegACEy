import { expect, signIn, test } from "./fixtures";
import { checkA11y } from "../a11y";

test.describe.configure({ mode: "serial" });

function seededAccount(project: string) {
    return `journeys-${project}-alpha`;
}

async function openListing(
    page: import("@playwright/test").Page,
    project: string,
    seller: string,
) {
    await signIn(page, seededAccount(project));
    await page.goto("/");
    const sellerRow = page
        .locator("tbody tr")
        .filter({
            has: page.getByRole("button", { name: seller, exact: true }),
        })
        .first();
    await expect(sellerRow).toBeVisible();
    await sellerRow.locator('a[href^="/listing/"]').click();
    await checkA11y(page);
}

test("sign in and explain a rejected password", async ({ page }) => {
    await page.goto("/signin");
    await checkA11y(page);
    await page
        .getByLabel("Account name")
        .fill(seededAccount(test.info().project.name));
    await page.getByLabel("Password", { exact: true }).fill("wrong-password");
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByRole("alert")).toContainText(
        "account name or password is incorrect",
    );
    await checkA11y(page);
});

test("browse filters, sorting and paging stay in the URL", async ({ page }) => {
    await page.goto("/?q=Chainmail&sort=price&dir=asc");
    await checkA11y(page);
    await expect(page).toHaveURL(/q=Chainmail.*sort=price.*dir=asc/);
    await expect(
        page.getByRole("heading", { name: /Browse|No listings/ }),
    ).toBeVisible();
});

test("a signed-in player can view the listing page and Vault", async ({
    page,
}) => {
    await signIn(page, seededAccount(test.info().project.name));
    await page.goto("/");
    await checkA11y(page);
    await expect(
        page.getByRole("link", { name: "Vault", exact: true }),
    ).toBeVisible();
    const listing = page.locator('a[href^="/listing/"]').first();
    await expect(listing).toBeVisible();
    await listing.click();
    await checkA11y(page);
    await expect(page.locator("h1")).toBeVisible();
    await page.getByRole("link", { name: "Vault", exact: true }).click();
    await checkA11y(page);
    await expect(page.getByRole("heading", { name: "Vault" })).toBeVisible();
});

test("sign out clears the session", async ({ page }) => {
    await signIn(page, seededAccount(test.info().project.name));
    await page.goto("/");
    await checkA11y(page);
    await page.getByRole("button", { name: "Sign out" }).click();
    await expect(
        page.getByRole("link", { name: "Sign in", exact: true }),
    ).toBeVisible();
    await checkA11y(page);
});

test("a missing listing is presented as gone", async ({ page }) => {
    await page.goto("/listing/999999999");
    await expect(
        page.getByRole("heading", { name: "No longer available" }),
    ).toBeVisible();
    await checkA11y(page);
});

test("the sign-in form validates missing credentials", async ({ page }) => {
    await page.goto("/signin");
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByLabel("Account name")).toBeFocused();
    await checkA11y(page);
});

test("browse can clear URL filters", async ({ page }) => {
    await page.goto("/?q=Chainmail&sort=price&dir=asc");
    await page.getByRole("button", { name: "Clear" }).click();
    await expect(page).toHaveURL(/\/$/);
    await checkA11y(page);
});

test("a listing detail links back to the seller browse filter", async ({
    page,
}) => {
    await page.goto("/");
    const listing = page.locator('a[href^="/listing/"]').first();
    await listing.click();
    await checkA11y(page);
    const seller = page.locator('.listing-detail a[href^="/?seller="]');
    await expect(seller).toBeVisible();
    await seller.click();
    await expect(page).toHaveURL(/seller=/);
    await checkA11y(page);
});

test("a signed-out player is sent to sign in for Vault", async ({ page }) => {
    await page.goto("/vault");
    await expect(
        page.getByRole("heading", { name: "Your Vault" }),
    ).toBeVisible();
    await page.getByRole("link", { name: "Sign in" }).first().click();
    await expect(page).toHaveURL(/signin/);
    await checkA11y(page);
});

test("a signed-in player can read history and request older pages", async ({
    page,
}) => {
    await signIn(page, seededAccount(test.info().project.name));
    await page.goto("/history");
    await expect(page.getByRole("heading", { name: "History" })).toBeVisible();
    await checkA11y(page);
    const older = page.getByRole("button", { name: /Load older/ });
    if (await older.count()) {
        await older.first().click();
        await expect(
            page.getByRole("heading", { name: "History" }),
        ).toBeVisible();
        await checkA11y(page);
    }
});

test("the seeded fixture exposes waiting, working, progress, done and failed tickets", async ({
    page,
}) => {
    await signIn(page, seededAccount(test.info().project.name));
    await page.goto("/");
    await expect(
        page.getByRole("heading", { name: "Market requests" }),
    ).toBeVisible();
    const panel = page.getByRole("region", { name: "Market requests" });
    for (const label of [
        "Waiting for the game server",
        "Working",
        "Confirm in game",
        "Channelling",
        "Done",
    ])
        await expect(
            panel.getByText(label, { exact: false }).first(),
        ).toBeVisible();
    const statuses = await panel.innerText();
    for (const code of [
        "offline",
        "invalid_character",
        "unsupported_kind",
        "invalid_ticket",
        "server_restart",
        "abandoned",
        "not_available",
        "busy",
        "not_in_vault",
        "listed",
        "withdrawing",
        "no_pack_space",
        "unique_limit",
        "recent_player_fight",
        "trading",
        "channelling",
        "interrupted",
        "save_failed",
        "unconfirmed",
        "banned",
        "invalid_amount",
        "insufficient_funds",
        "paused",
    ])
        expect(statuses).toContain(`(${code})`);
    await checkA11y(page);
});

test("a player can list and delist a held Vault item", async ({ page }) => {
    await signIn(page, seededAccount(test.info().project.name));
    await page.goto("/vault");
    await expect(page.getByRole("heading", { name: "Vault" })).toBeVisible();
    await checkA11y(page);
    const item = () =>
        page.locator("tbody tr").filter({ hasText: "Kite Shield" }).first();
    await item().locator("input[type=number]").fill("37");
    await item().getByRole("button", { name: "List item" }).click();
    await expect(item().getByRole("button", { name: "Delist" })).toBeVisible();
    await checkA11y(page);
    await item().getByRole("button", { name: "Delist" }).click();
    await expect(
        item().getByRole("button", { name: "List item" }),
    ).toBeVisible();
    await checkA11y(page);
});

test("the browse page exposes paging and appraisal on a touch tap", async ({
    page,
}) => {
    await page.goto("/");
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
        ),
    ).toBe(true);
    await checkA11y(page);
    const appraisal = page.getByRole("button", { name: /Appraise/ }).first();
    await appraisal.click();
    await expect(
        page.getByRole("region", { name: /appraisal/i }),
    ).toBeVisible();
    await checkA11y(page);
    await page.getByRole("button", { name: "Close appraisal" }).click();
    const more = page.getByRole("button", { name: "Load more" });
    if (await more.count()) {
        await more.click();
        await checkA11y(page);
    }
});

test("a signed-in player can open the account pages from the browse header", async ({
    page,
}) => {
    await signIn(page, seededAccount(test.info().project.name));
    await page.goto("/");
    await checkA11y(page);
    await page.getByRole("link", { name: "History", exact: true }).click();
    await expect(page.getByRole("heading", { name: "History" })).toBeVisible();
    await checkA11y(page);
    await page.getByRole("link", { name: "Vault", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Vault" })).toBeVisible();
    await checkA11y(page);
});

test("a purchase retry preserves the frozen attempt after an unreadable network response", async ({
    page,
}) => {
    const attempts: unknown[] = [];
    await openListing(
        page,
        test.info().project.name,
        `journeys ${test.info().project.name} Bravo`,
    );
    await page.route("**/api/listings/*/purchase", async (route) => {
        attempts.push(route.request().postDataJSON());
        if (attempts.length === 1) await route.abort("failed");
        else await route.continue();
    });
    await page.getByRole("button", { name: "Buy" }).click();
    await page.getByRole("button", { name: "Confirm purchase" }).click();
    await expect(page.getByRole("alert")).toContainText(
        "The response was lost.",
    );
    await page.getByRole("button", { name: "Retry this attempt" }).click();
    await expect(page.getByRole("link", { name: "Go to Vault" })).toBeVisible();
    expect(attempts).toHaveLength(2);
    expect(attempts[0]).toEqual(attempts[1]);
    await checkA11y(page);
});

test("a price_changed answer requires fresh price and idempotency key", async ({
    page,
}) => {
    const attempts: Array<{ expectedPrice: number; idempotencyKey: string }> =
        [];
    let changed = false;
    await openListing(
        page,
        test.info().project.name,
        `journeys ${test.info().project.name} Bravo`,
    );
    await page.route("**/api/listings/*", async (route) => {
        if (changed) {
            const response = await route.fetch();
            const body = await response.json();
            body.price = 150;
            return route.fulfill({ response, body: JSON.stringify(body) });
        }
        await route.continue();
    });
    await page.route("**/api/listings/*/purchase", async (route) => {
        attempts.push(route.request().postDataJSON());
        if (attempts.length === 1)
            return route.fulfill({
                status: 409,
                contentType: "application/json",
                body: JSON.stringify({ error: "price_changed", price: 150 }),
            });
        return route.fulfill({
            status: 200,
            contentType: "application/json",
            body: JSON.stringify({
                status: "ok",
                listingId: 1,
                itemGuid: 1,
                price: 150,
                fee: 0,
                balance: 850,
            }),
        });
    });
    await page.getByRole("button", { name: "Buy" }).click();
    await page.getByRole("button", { name: "Confirm purchase" }).click();
    await expect(page.getByRole("alert")).toContainText("New price: 150 MMD");
    changed = true;
    await page.getByRole("button", { name: "Review new price" }).click();
    await page.getByRole("button", { name: "Confirm purchase" }).click();
    await expect(page.getByRole("link", { name: "Go to Vault" })).toBeVisible();
    expect(attempts[1].expectedPrice).toBe(150);
    expect(attempts[1].idempotencyKey).not.toBe(attempts[0].idempotencyKey);
    await checkA11y(page);
});

test("an unreadable purchase answer is reported as a server problem", async ({
    page,
}) => {
    await openListing(
        page,
        test.info().project.name,
        `journeys ${test.info().project.name} Bravo`,
    );
    await page.route("**/api/listings/*/purchase", (route) =>
        route.fulfill({
            status: 200,
            contentType: "application/json",
            body: '{"status":',
        }),
    );
    await page.getByRole("button", { name: "Buy" }).click();
    await page.getByRole("button", { name: "Confirm purchase" }).click();
    await expect(page.getByRole("dialog").getByRole("alert")).toContainText(
        "The server is unavailable.",
    );
    await checkA11y(page);
});

test("an account without characters cannot buy and gets the game next step", async ({
    page,
}) => {
    await page.route("**/api/me", async (route) => {
        const response = await route.fetch();
        const body = await response.json();
        body.characters = [];
        await route.fulfill({ response, body: JSON.stringify(body) });
    });
    await openListing(
        page,
        test.info().project.name,
        `journeys ${test.info().project.name} Bravo`,
    );
    await page.evaluate(() =>
        document.dispatchEvent(new Event("visibilitychange")),
    );
    await expect(
        page.getByText("Create a character in game first."),
    ).toBeVisible();
    await expect(page.getByRole("button", { name: "Buy" })).toBeDisabled();
    await checkA11y(page);
});

test("a character selected before the BFF migrates from local storage to its cookie", async ({
    page,
}) => {
    await signIn(page, seededAccount(test.info().project.name));
    await page.goto("/");
    const me = await page.evaluate(async () => {
        const response = await fetch("/api/me", {
            credentials: "same-origin",
            headers: { "X-Market-Request": "1" },
        });
        if (!response.ok)
            throw new Error(`GET /api/me failed: ${response.status}`);
        return response.json();
    });
    const characterKey = `market-character-${me.accountId}`;
    const secondCharacter = await page
        .getByLabel("Acting character")
        .locator("option")
        .nth(1)
        .getAttribute("value");
    expect(secondCharacter).toBeTruthy();
    await page.context().clearCookies({ name: characterKey });
    await page.evaluate(
        ([key, value]) => {
            document.cookie = `${key}=; Path=/; Max-Age=0; SameSite=Lax`;
            localStorage.setItem(key, value!);
        },
        [characterKey, secondCharacter!],
    );
    expect(
        await page.evaluate(
            (key) =>
                document.cookie
                    .split("; ")
                    .some((cookie) => cookie.startsWith(`${key}=`)),
            characterKey,
        ),
    ).toBe(false);
    await page.reload();
    await page.waitForSelector("html[data-hydrated]");
    await expect(page.getByLabel("Acting character")).toHaveValue(
        secondCharacter!,
    );
    expect(
        await page.evaluate((key) => localStorage.getItem(key), characterKey),
    ).toBeNull();
    expect(
        (await page.context().cookies()).find(
            (cookie) => cookie.name === characterKey,
        )?.value,
    ).toBe(secondCharacter);
    await checkA11y(page);
});

test("a buyer sees both balances change after purchasing from another seeded account", async ({
    page,
    context,
}) => {
    const amount = (text: string) => Number(text.replace(/[^0-9]/g, ""));
    const project = test.info().project.name;
    await signIn(page, `journeys-${project}-alpha`);
    await page.goto("/");
    const buyerBefore = amount(await page.locator(".balance").innerText());
    const sellerRow = page
        .locator("tbody tr")
        .filter({ hasText: `journeys ${project} Bravo` })
        .first();
    await expect(sellerRow).toBeVisible();
    await sellerRow.locator('a[href^="/listing/"]').click();
    await checkA11y(page);
    const listedPrice = amount(await page.locator(".big-price").innerText());
    const listingUrl = page.url();
    await page.getByRole("button", { name: "Sign out" }).click();
    await signIn(page, `journeys-${project}-bravo`);
    await page.goto("/");
    const sellerBefore = amount(await page.locator(".balance").innerText());
    await page.getByRole("button", { name: "Sign out" }).click();
    await signIn(page, `journeys-${project}-alpha`);
    await page.goto(listingUrl);
    await page.getByRole("button", { name: "Buy" }).click();
    await page.getByRole("button", { name: "Confirm purchase" }).click();
    await expect(page.getByRole("status")).toContainText("Purchased.");
    const buyerAfter = amount(await page.locator(".balance").innerText());
    expect(buyerAfter).toBe(buyerBefore - listedPrice);
    await checkA11y(page);
    await page
        .getByRole("dialog")
        .getByRole("button", { name: "Close", exact: true })
        .click();
    await page.getByRole("button", { name: "Sign out" }).click();
    await signIn(page, `journeys-${project}-bravo`);
    await page.goto("/");
    const sellerAfter = amount(await page.locator(".balance").innerText());
    expect(sellerAfter).toBe(sellerBefore + listedPrice);
    await checkA11y(page);
    // Keep the browser context in use so the second account is verified through its own session.
    expect(await context.cookies()).toEqual(
        expect.arrayContaining([
            expect.objectContaining({ name: "market_session" }),
        ]),
    );
});

test("a delayed account refresh cannot restore the account after sign-out", async ({
    page,
}) => {
    let calls = 0;
    let release!: () => void;
    const pending = new Promise<void>((resolve) => {
        release = resolve;
    });
    await page.route("**/api/me", async (route) => {
        if (++calls >= 1) await pending;
        await route.continue();
    });
    await signIn(page, seededAccount(test.info().project.name));
    await page.goto("/");
    await expect(page.locator("header")).toContainText(
        seededAccount(test.info().project.name),
    );
    await page.evaluate(() =>
        document.dispatchEvent(new Event("visibilitychange")),
    );
    await expect.poll(() => calls).toBe(1);
    await page.getByRole("button", { name: "Sign out" }).click();
    await expect(
        page.getByRole("link", { name: "Sign in", exact: true }),
    ).toBeVisible();
    release();
    await page.waitForTimeout(100);
    await expect(page.locator("header")).not.toContainText(
        seededAccount(test.info().project.name),
    );
    await checkA11y(page);
});

test("sign-in stays on the form when loading the account fails", async ({
    page,
}) => {
    await page.route("**/api/me", (route) =>
        route.fulfill({
            status: 503,
            contentType: "application/json",
            body: JSON.stringify({ error: "server" }),
        }),
    );
    await page.goto("/signin");
    await page
        .getByLabel("Account name")
        .fill(seededAccount(test.info().project.name));
    await page.getByLabel("Password", { exact: true }).fill("marketdev");
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
    await expect(page.locator(".signin [role=alert]")).toContainText(
        "The server is unavailable.",
    );
    await checkA11y(page);
});

test("an open purchase is disabled when a pause refresh arrives", async ({
    page,
}) => {
    let paused = false;
    await page.route("**/api/me", async (route) => {
        const response = await route.fetch();
        const body = await response.json();
        body.paused = paused;
        await route.fulfill({ response, body: JSON.stringify(body) });
    });
    await openListing(
        page,
        test.info().project.name,
        `website ${test.info().project.name} Bravo`,
    );
    await page.getByRole("button", { name: "Buy" }).click();
    paused = true;
    await page.evaluate(() =>
        document.dispatchEvent(new Event("visibilitychange")),
    );
    await expect(
        page.getByRole("button", { name: "Confirm purchase" }),
    ).toBeDisabled();
    await checkA11y(page);
    paused = false;
    await page.evaluate(() =>
        document.dispatchEvent(new Event("visibilitychange")),
    );
    await expect(
        page.getByRole("button", { name: "Confirm purchase" }),
    ).toBeEnabled();
    await checkA11y(page);
});

test("a purchase dialog keeps keyboard focus inside", async ({ page }) => {
    await openListing(
        page,
        test.info().project.name,
        `website ${test.info().project.name} Bravo`,
    );
    await page.getByRole("button", { name: "Buy" }).click();
    for (let i = 0; i < 5; i++) {
        await page.keyboard.press("Tab");
        expect(
            await page.evaluate(() =>
                document
                    .querySelector("dialog")
                    ?.contains(document.activeElement),
            ),
        ).toBe(true);
    }
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await checkA11y(page);
});

test("Escape dismisses an appraisal after focus moves to its close button", async ({
    page,
}) => {
    await page.goto("/");
    const appraisal = page.getByRole("button", { name: /Appraise/ }).first();
    await appraisal.focus();
    await expect(
        page.getByRole("region", { name: /appraisal/i }),
    ).toBeVisible();
    await page.keyboard.press("Tab");
    await expect(
        page.getByRole("button", { name: "Close appraisal" }),
    ).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("region", { name: /appraisal/i })).toHaveCount(
        0,
    );
    await checkA11y(page);
});
