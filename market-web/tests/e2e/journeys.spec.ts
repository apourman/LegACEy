import type { Page } from "@playwright/test";
import { expect, signIn, test } from "./fixtures";
import { checkA11y } from "../a11y";

// The seed gives this file an Alpha (buyer) and Bravo (seller) account per project. Bravo lists an Atlatl, bought for real by the
// purchase journey, and an Amuli Shadow Coat, which the other purchase journeys open but never buy.
function seededPair() {
    const project = test.info().project.name;
    return {
        buyer: `journeys-${project}-alpha`,
        seller: `journeys-${project}-bravo`,
        buyerCharacter: `journeys ${project} Alpha`,
        sellerCharacter: `journeys ${project} Bravo`,
    };
}

const mmd = (text: string) => Number(text.replace(/[^0-9]/g, ""));

async function balance(page: Page) {
    return mmd(await page.locator(".balance").innerText());
}

/** Opens the seeded seller's listing of one item, signed in as the seeded buyer. */
async function openSellerListing(page: Page, item: string) {
    const { buyer, sellerCharacter } = seededPair();
    await signIn(page, buyer);
    await page.goto(
        `/?${new URLSearchParams({ seller: sellerCharacter, q: item })}`,
    );
    await page.getByRole("link", { name: item }).first().click();
    await expect(page.getByRole("heading", { name: item })).toBeVisible();
    await checkA11y(page);
}

/** The site refreshes the account when the tab becomes visible again. */
async function refreshAccount(page: Page) {
    await page.evaluate(() =>
        document.dispatchEvent(new Event("visibilitychange")),
    );
}

/** Lets GET /api/me reach the real API, then changes its answer. */
async function editAccountAnswer(
    page: Page,
    edit: (me: Record<string, unknown>) => void,
) {
    await page.route("**/api/me", async (route) => {
        const response = await route.fetch();
        const me = await response.json();
        edit(me);
        await route.fulfill({ response, body: JSON.stringify(me) });
    });
}

test("sign in explains each failure, then signs in", async ({ page }) => {
    const { buyer } = seededPair();
    await page.goto("/signin");
    await checkA11y(page);
    const account = page.getByLabel("Account name");
    const password = page.getByLabel("Password", { exact: true });
    const submit = page.getByRole("button", { name: "Sign in" });

    await submit.click();
    await expect(account).toBeFocused();

    await account.fill(buyer);
    await password.fill("wrong-password");
    await submit.click();
    await expect(page.getByRole("alert")).toContainText(
        "account name or password is incorrect",
    );
    await checkA11y(page);

    await page.route("**/api/me", (route) =>
        route.fulfill({
            status: 503,
            contentType: "application/json",
            body: JSON.stringify({ error: "server" }),
        }),
    );
    await password.fill("marketdev");
    await submit.click();
    await expect(page.locator(".signin [role=alert]")).toContainText(
        "The server is unavailable.",
    );
    await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();

    await page.unroute("**/api/me");
    await submit.click();
    await expect(page.locator("header")).toContainText(buyer);
});

test("browse filters, sorting and paging stay in the URL", async ({ page }) => {
    const prices = async () =>
        (await page.locator("tbody td.price").allInnerTexts()).map(mmd);
    await page.goto("/?sort=price&dir=asc&limit=2");
    await checkA11y(page);
    await expect(page.locator("tbody tr")).toHaveCount(2);
    await page.getByRole("button", { name: "Load more" }).click();
    await expect(page.locator("tbody tr")).toHaveCount(4);
    const ascending = await prices();
    expect(ascending).toEqual([...ascending].sort((a, b) => a - b));
    await expect(page).toHaveURL(/sort=price.*dir=asc.*limit=2/);

    await page.getByLabel("Item name").fill("Chainmail");
    await page.getByRole("button", { name: "Apply filters" }).click();
    await expect(page).toHaveURL(/q=Chainmail/);
    await expect(page.locator("tbody tr").first()).toBeVisible();
    for (const name of await page
        .locator('tbody a[href^="/listing/"]')
        .allInnerTexts())
        expect(name).toContain("Chainmail");
    await checkA11y(page);

    await page.getByRole("button", { name: "Clear" }).click();
    await expect(page).toHaveURL(/\/$/);
});

test("a listing page links back to its seller's listings", async ({ page }) => {
    await page.goto("/");
    await page.locator('a[href^="/listing/"]').first().click();
    await expect(page.locator("h1")).toBeVisible();
    await checkA11y(page);
    await page.locator('.listing-detail a[href^="/?seller="]').click();
    await expect(page).toHaveURL(/seller=/);
});

test("a signed-in player moves between Browse, History and Vault", async ({
    page,
}) => {
    await page.goto("/vault");
    await expect(
        page.getByRole("heading", { name: "Your Vault" }),
    ).toBeVisible();
    await page.getByRole("link", { name: "Sign in" }).first().click();
    await expect(page).toHaveURL(/signin/);
    await checkA11y(page);

    await signIn(page, seededPair().buyer);
    await page.goto("/");
    await page.getByRole("link", { name: "History", exact: true }).click();
    await expect(page.getByRole("heading", { name: "History" })).toBeVisible();
    await checkA11y(page);
    await page.getByRole("link", { name: "Vault", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Vault" })).toBeVisible();
});

test("sign out ends the session, and a delayed account refresh cannot restore it", async ({
    page,
}) => {
    const { buyer } = seededPair();
    let calls = 0;
    let release!: () => void;
    const held = new Promise<void>((resolve) => (release = resolve));
    await signIn(page, buyer);
    await page.goto("/");
    await expect(page.locator("header")).toContainText(buyer);
    await checkA11y(page);
    await page.route("**/api/me", async (route) => {
        calls++;
        await held;
        await route.continue();
    });
    await refreshAccount(page);
    await expect.poll(() => calls).toBe(1);
    await page.getByRole("button", { name: "Sign out" }).click();
    await expect(
        page.getByRole("link", { name: "Sign in", exact: true }),
    ).toBeVisible();
    release();
    await page.waitForTimeout(100);
    await expect(page.locator("header")).not.toContainText(buyer);
});

test("history loads older item events page by page", async ({ page }) => {
    // the seed's history fits on one 50-row page, so ask the real API for pages of two
    await page.route("**/api/history?*", (route) => {
        const url = new URL(route.request().url());
        for (const limit of ["itemsLimit", "transfersLimit"])
            if (url.searchParams.has(limit)) url.searchParams.set(limit, "2");
        return route.continue({ url: url.toString() });
    });
    await signIn(page, seededPair().buyer);
    await page.goto("/history");
    const events = page
        .getByRole("region", { name: "Item events" })
        .getByRole("listitem");
    await expect(events).toHaveCount(2);
    await checkA11y(page);
    await page.getByRole("button", { name: "Load older item events" }).click();
    await expect(events).toHaveCount(4);
    const texts = await events.allInnerTexts();
    expect(new Set(texts).size).toBe(4);
});

test("the ticket fixture shows every ticket state until finished ones are cleared", async ({
    page,
}) => {
    await page.clock.install();
    await signIn(page, seededPair().buyer);
    await page.goto("/");
    const panel = page.getByRole("region", { name: "Market requests" });
    for (const label of [
        "Waiting for the game server",
        "Working",
        "Confirm in game",
        "Channelling",
    ])
        await expect(panel.getByText(label).first()).toBeVisible();
    await expect(panel.locator(".ticket-status--done").first()).toBeVisible();
    const failed = panel.locator(".ticket-status--failed");
    // each failure shows the game's message with its result code
    const statuses = (await failed.allInnerTexts()).join("\n");
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

    // the confirmation and channelling countdowns run out
    await page.clock.fastForward("10:00");
    await expect(panel.getByText(/Confirm in game · Finishing…/)).toBeVisible();
    await expect(panel.getByText(/Channelling · Finishing…/)).toBeVisible();

    await panel.getByRole("button", { name: "Clear finished" }).click();
    await expect(
        panel.locator(".ticket-status--done, .ticket-status--failed"),
    ).toHaveCount(0);
    await expect(
        panel.getByRole("button", { name: "Clear finished" }),
    ).toHaveCount(0);
    await expect(
        panel.getByText("Waiting for the game server").first(),
    ).toBeVisible();
});

test("a price_changed answer needs a fresh price and idempotency key", async ({
    page,
}) => {
    await openSellerListing(page, "Amuli Shadow Coat");
    const listedPrice = mmd(await page.locator(".big-price").innerText());
    const sent: Array<{ expectedPrice: number; idempotencyKey: string }> = [];
    await page.route("**/api/listings/*/purchase", async (route) => {
        const attempt = route.request().postDataJSON();
        sent.push(attempt);
        if (sent.length === 1)
            // a price the seller has since changed: the real API refuses it with the current one
            return route.continue({
                postData: JSON.stringify({
                    ...attempt,
                    expectedPrice: listedPrice - 1,
                }),
            });
        // the second attempt is only inspected; this listing stays for the other journeys
        return route.abort("failed");
    });
    await page.getByRole("button", { name: "Buy" }).click();
    await page.getByRole("button", { name: "Confirm purchase" }).click();
    await expect(page.getByRole("alert")).toContainText(
        `New price: ${listedPrice} MMD`,
    );
    await page.getByRole("button", { name: "Review new price" }).click();
    await page.getByRole("button", { name: "Confirm purchase" }).click();
    await expect.poll(() => sent.length).toBe(2);
    expect(sent[1].expectedPrice).toBe(listedPrice);
    expect(sent[1].idempotencyKey).not.toBe(sent[0].idempotencyKey);
});

test("the purchase dialog keeps focus, follows a pause and reports an unreadable answer", async ({
    page,
}) => {
    let paused = false;
    await editAccountAnswer(page, (me) => (me.paused = paused));
    await openSellerListing(page, "Amuli Shadow Coat");
    await page.getByRole("button", { name: "Buy" }).click();
    const dialog = page.getByRole("dialog");
    for (let i = 0; i < 5; i++) {
        await page.keyboard.press("Tab");
        expect(
            await dialog.evaluate((element) =>
                element.contains(document.activeElement),
            ),
        ).toBe(true);
    }

    paused = true;
    await refreshAccount(page);
    const confirm = page.getByRole("button", { name: "Confirm purchase" });
    await expect(confirm).toBeDisabled();
    await checkA11y(page);
    paused = false;
    await refreshAccount(page);
    await expect(confirm).toBeEnabled();

    await page.route("**/api/listings/*/purchase", (route) =>
        route.fulfill({
            status: 200,
            contentType: "application/json",
            body: '{"status":',
        }),
    );
    await confirm.click();
    await expect(dialog.getByRole("alert")).toContainText(
        "The server is unavailable.",
    );
    await checkA11y(page);
    await page.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);
});

test("an account without characters cannot buy and is told what to do", async ({
    page,
}) => {
    await editAccountAnswer(page, (me) => (me.characters = []));
    await openSellerListing(page, "Amuli Shadow Coat");
    await refreshAccount(page);
    await expect(
        page.getByText("Create a character in game first."),
    ).toBeVisible();
    await expect(page.getByRole("button", { name: "Buy" })).toBeDisabled();
});

test("appraisal opens on tap and focus and closes with Escape", async ({
    page,
}) => {
    await page.goto("/");
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
        ),
    ).toBe(true);
    const appraise = page.getByRole("button", { name: /Appraise/ }).first();
    const appraisal = page.getByRole("region", { name: /appraisal/i });
    await appraise.click();
    await expect(appraisal).toBeVisible();
    await checkA11y(page);
    await page.getByRole("button", { name: "Close appraisal" }).click();
    await expect(appraisal).toHaveCount(0);

    await appraise.focus();
    await expect(appraisal).toBeVisible();
    await page.keyboard.press("Tab");
    await expect(
        page.getByRole("button", { name: "Close appraisal" }),
    ).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(appraisal).toHaveCount(0);
});

test("a character chosen before the BFF moves from local storage to its cookie", async ({
    page,
}) => {
    await signIn(page, seededPair().buyer);
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
});

// These change the seeded accounts for good (a listing, a purchase, both balances), so they run in order and are never retried:
// a retry would find the Atlatl already bought.
test.describe("changes to the seeded accounts", () => {
    test.describe.configure({ mode: "serial", retries: 0 });

    test("a player lists and delists a held Vault item, and the delisted listing is gone", async ({
        page,
    }) => {
        const { buyer, buyerCharacter } = seededPair();
        await signIn(page, buyer);
        await page.goto("/vault");
        await expect(
            page.getByRole("heading", { name: "Vault" }),
        ).toBeVisible();
        await checkA11y(page);
        const shield = page
            .locator("tbody tr")
            .filter({ hasText: "Kite Shield" })
            .first();
        await shield.locator("input[type=number]").fill("37");
        await shield.getByRole("button", { name: "List item" }).click();
        await expect(
            shield.getByRole("button", { name: "Delist" }),
        ).toBeVisible();
        await checkA11y(page);

        await page.goto(
            `/?${new URLSearchParams({ seller: buyerCharacter, q: "Kite Shield" })}`,
        );
        const listingUrl = await page
            .getByRole("link", { name: "Kite Shield" })
            .first()
            .getAttribute("href");
        expect(listingUrl).toMatch(/^\/listing\/\d+$/);

        await page.goto("/vault");
        await shield.getByRole("button", { name: "Delist" }).click();
        await expect(
            shield.getByRole("button", { name: "List item" }),
        ).toBeVisible();
        await page.goto(listingUrl!);
        await expect(
            page.getByRole("heading", { name: "No longer available" }),
        ).toBeVisible();
    });

    test("a purchase survives a lost answer and moves both seeded balances", async ({
        page,
    }) => {
        const { seller } = seededPair();
        await signIn(page, seller);
        await page.goto("/");
        const sellerBefore = await balance(page);
        await page.getByRole("button", { name: "Sign out" }).click();

        await openSellerListing(page, "Atlatl");
        const buyerBefore = await balance(page);
        const price = mmd(await page.locator(".big-price").innerText());
        const listingUrl = page.url();
        const sent: unknown[] = [];
        await page.route("**/api/listings/*/purchase", async (route) => {
            sent.push(route.request().postDataJSON());
            if (sent.length === 1) await route.abort("failed");
            else await route.continue();
        });
        await page.getByRole("button", { name: "Buy" }).click();
        await page.getByRole("button", { name: "Confirm purchase" }).click();
        await expect(page.getByRole("alert")).toContainText(
            "The response was lost.",
        );
        await page.getByRole("button", { name: "Retry this attempt" }).click();
        await expect(page.getByRole("status")).toContainText("Purchased.");
        expect(sent).toHaveLength(2);
        expect(sent[1]).toEqual(sent[0]);
        await expect.poll(() => balance(page)).toBe(buyerBefore - price);
        await checkA11y(page);
        await page
            .getByRole("dialog")
            .getByRole("button", { name: "Close", exact: true })
            .click();

        await page.goto(listingUrl);
        await expect(
            page.getByRole("heading", { name: "No longer available" }),
        ).toBeVisible();
        await checkA11y(page);

        await page.getByRole("button", { name: "Sign out" }).click();
        await signIn(page, seller);
        await page.goto("/");
        expect(await balance(page)).toBe(sellerBefore + price);
    });
});
