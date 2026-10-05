import { test, expect, session } from './fixtures';

// The website's checks, through the BFF: the Market API's answers are scripted on the fake API (api.on), which the BFF's pages, sign-in routes
// and /api/* proxy all reach. A check that watches what the browser itself sends still uses page.route, and lets the request go on to the BFF.

const listing = {
  id: 1, itemGuid: 10, name: 'Bone Slicer', itemType: 'MeleeWeapon', material: 'Steel',
  workmanship: 7, level: 100, arcaneLore: 200, summary: '34-52 (Slashing)',
  quantity: 3, price: 120, seller: 'Bravo Main', listedTime: '2026-10-01T12:00:00Z',
  wield: 'Level 100', icon: { layers: [], glow: null },
};
const me = {
  accountId: 1, accountName: 'Alpha', balance: 500, frozen: false, paused: false,
  characters: [{ id: 11, name: 'Alpha Main' }, { id: 12, name: 'Alpha Second' }],
  vaultCount: 2, vaultCap: 1000, listingCount: 1, listingCap: 200,
};
const unauthorized = { status: 401, json: { error: 'unauthorized' } };
test.beforeEach(async ({ api }) => {
  api.on('GET', '/api/me', unauthorized);
  // a signed-in page's request panel asks for tickets; the 404 below would put its own alert beside the one a check reads
  api.on('GET', '/api/tickets', { json: [] });
  api.on('GET', '/api/facets', { json: {
    itemTypes: [{ value: 'MeleeWeapon', label: 'MeleeWeapon', count: 1 }],
    sorts: [{ value: 'newest', label: 'Newest', defaultDir: 'desc' }, { value: 'price', label: 'Price', defaultDir: 'asc' }],
  } });
  api.on('GET', '/api/listings', { json: { listings: [listing], nextCursor: null } });
  api.on('GET', '/api/listings/suggest', { json: { suggestions: ['Bone Slicer'] } });
  api.on('GET', '/api/listings/1', { json: { ...listing, lines: ['Value: 500', 'Damage: 34 - 52'], spells: [{ name: 'Blood Drinker', cantrip: false }] } });
  api.on('POST', '/api/auth/session', { json: session });
  // anything else: 404 { error: not_found } (the fake API's default)
});
test('browse preserves filters in URL and back navigation, with focus appraisal', async ({ page }) => {
  await page.goto('/?q=Bone&sort=price&dir=asc');
  await expect(page.getByLabel('Item name')).toHaveValue('Bone');
  await expect(page.getByRole('link', { name: 'Bone Slicer' })).toBeVisible();
  await page.getByRole('button', { name: 'Bravo Main', exact: true }).click();
  await expect(page).toHaveURL(/seller=Bravo/);
  await page.goBack();
  await expect(page).not.toHaveURL(/seller=/);
  await page.getByRole('button', { name: 'Appraise Bone Slicer' }).focus();
  await expect(page.getByText('Damage: 34 - 52')).toBeVisible();
  await page.getByRole('link', { name: 'Bone Slicer' }).click();
  await expect(page.getByRole('heading', { name: 'Bone Slicer', exact: true })).toBeVisible();
  await expect(page.getByText('Blood Drinker')).toBeVisible();
  await page.getByRole('button', { name: 'Buy', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});

test('sign in, browse listings and buy one', async ({ page, api }) => {
  let signedIn = false;
  const logins: unknown[] = [];
  const purchases: { body: unknown; csrf: string }[] = [];
  api.on('POST', '/api/auth/session', request => {
    logins.push(JSON.parse(request.body));
    signedIn = true;
    return { json: session };
  });
  api.on('GET', '/api/me', () => signedIn ? { json: me } : unauthorized);
  await page.route('**/api/listings/1/purchase', route => {
    purchases.push({ body: route.request().postDataJSON(), csrf: route.request().headers()['x-market-request'] });
    return route.continue();
  });
  api.on('POST', '/api/listings/1/purchase', { json: { status: 'ok', listingId: 1, itemGuid: 10, price: 120, fee: 0, balance: 380 } });

  await page.goto('/signin');
  await page.getByLabel('Account name').fill('Alpha');
  await page.getByLabel('Password', { exact: true }).fill('pass');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByText('Alpha', { exact: true })).toBeVisible();
  expect(logins).toEqual([{ account: 'Alpha', password: 'pass' }]);

  await expect(page).toHaveURL(/\/$/);
  await page.getByRole('link', { name: 'Bone Slicer' }).click();
  await page.getByRole('button', { name: 'Buy', exact: true }).click();
  await page.getByRole('button', { name: 'Confirm purchase' }).click();
  await expect(page.getByRole('link', { name: 'Go to Vault' })).toBeVisible();
  expect(purchases).toHaveLength(1);
  expect(purchases[0].body).toMatchObject({ count: 3, expectedPrice: 120, characterId: 11 });
  expect(purchases[0].csrf).toBe('1');
});

test('purchase retries keep character price and key, then refresh account and link Vault', async ({ page, api, signIn }) => {
  api.on('GET', '/api/me', { json: me });
  await signIn();
  let attempts: object[] = [];
  const csrfHeaders: string[] = [];
  await page.route('**/api/listings/1/purchase', async route => {
    attempts.push(route.request().postDataJSON());
    csrfHeaders.push(route.request().headers()['x-market-request']);
    if (attempts.length === 1) return route.abort('failed');
    await route.continue();
  });
  api.on('POST', '/api/listings/1/purchase', { json: { status: 'ok', balance: 380, price: 120, fee: 0, listingId: 1, itemGuid: 10 } });
  await page.goto('/listing/1');
  await page.getByLabel('Acting character').selectOption('12');
  await page.getByRole('button', { name: 'Buy', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Alpha Second');
  await expect(page.getByRole('dialog')).toContainText('380 MMD');
  await page.getByRole('button', { name: 'Confirm purchase' }).click();
  await expect(page.getByText('The response was lost.', { exact: false })).toBeVisible();
  // the saved choice changes under the open dialog: the cookie the server reads, and the old key
  await page.evaluate(() => { localStorage.setItem('market-character-1', '11'); document.cookie = 'market-character-1=11; Path=/'; });
  await page.getByRole('button', { name: 'Retry this attempt' }).click();
  await expect(page.getByRole('link', { name: 'Go to Vault' })).toBeVisible();
  expect(attempts).toHaveLength(2);
  expect(attempts[0]).toEqual(attempts[1]);
  expect(attempts[0]).toMatchObject({ characterId: 12, expectedPrice: 120, count: 3 });
  expect(csrfHeaders, 'every attempt carries the header the BFF checks for CSRF').toEqual(['1', '1']);
  expect(api.requests.filter(r => r.path === '/api/listings/1/purchase').map(r => JSON.parse(r.body)), 'the retry reached the API as it was').toEqual([attempts[1]]);
});

test('an unreadable answer is reported as a server problem, not a lost response', async ({ page, api, signIn }) => {
  api.on('GET', '/api/me', { json: me });
  await signIn();
  api.on('POST', '/api/listings/1/purchase', { body: '{"status":', headers: { 'Content-Type': 'application/json' } });
  await page.goto('/listing/1');
  await page.getByRole('button', { name: 'Buy', exact: true }).click();
  await page.getByRole('button', { name: 'Confirm purchase' }).click();
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('The server is unavailable.');
});

test('price changed requires a fresh confirmation and fresh key', async ({ page, api, signIn }) => {
  api.on('GET', '/api/me', { json: me });
  await signIn();
  let changed = false;
  api.on('GET', '/api/listings/1', () => ({ json: { ...listing, price: changed ? 150 : 120, lines: [], spells: [] } }));
  const attempts: { idempotencyKey: string; expectedPrice: number }[] = [];
  api.on('POST', '/api/listings/1/purchase', request => {
    attempts.push(JSON.parse(request.body));
    if (!changed) { changed = true; return { status: 409, json: { error: 'price_changed', price: 150 } }; }
    return { json: { status: 'ok', listingId: 1, itemGuid: 10, price: 150, fee: 0, balance: 350 } };
  });
  await page.goto('/listing/1');
  await page.getByRole('button', { name: 'Buy', exact: true }).click();
  await page.getByRole('button', { name: 'Confirm purchase' }).click();
  await expect(page.getByRole('alert')).toContainText('New price: 150 MMD');
  await page.getByRole('button', { name: 'Review new price' }).click();
  await expect(page.getByRole('dialog')).toContainText('350 MMD');
  expect(attempts).toHaveLength(1);
  await page.getByRole('button', { name: 'Confirm purchase' }).click();
  await expect(page.getByRole('link', { name: 'Go to Vault' })).toBeVisible();
  expect(attempts[1].expectedPrice).toBe(150);
  expect(attempts[1].idempotencyKey).not.toBe(attempts[0].idempotencyKey);
});

test('banned session returns to sign-in and signing in explains the ban', async ({ page, api, signIn }) => {
  let banned = false;
  api.on('GET', '/api/me', () => banned ? unauthorized : { json: me });
  await signIn();
  api.on('POST', '/api/auth/session', { status: 403, json: { error: 'banned' } });
  await page.goto('/listing/1');
  await expect(page.getByText('Alpha', { exact: true })).toBeVisible();
  banned = true;
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await expect(page.getByText('Your session has ended. Please sign in again.')).toBeVisible();
  await page.getByLabel('Account name').fill('Alpha');
  await page.getByLabel('Password', { exact: true }).fill('pass');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('alert')).toContainText('Your account is banned.');
});

test('phone browse fits viewport, appraisal opens on tap and dialog traps keyboard focus', async ({ page, api, signIn }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  api.on('GET', '/api/me', { json: me });
  await signIn();
  await page.goto('/');
  await page.getByRole('button', { name: 'Appraise Bone Slicer' }).tap();
  await expect(page.getByText('Damage: 34 - 52')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByRole('button', { name: 'Close appraisal' }).click();
  await page.getByRole('link', { name: 'Bone Slicer' }).click();
  await page.getByRole('button', { name: 'Buy', exact: true }).click();
  for (let i = 0; i < 5; i++) {
    await page.keyboard.press('Tab');
    expect(await page.evaluate(() => document.querySelector('dialog')?.contains(document.activeElement))).toBe(true);
  }
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).not.toBeVisible();
});

for (const [code, status, message] of [
  ['invalid_credentials', 401, 'account name or password is incorrect'],
  ['account_locked', 429, 'account is temporarily locked'],
  ['ip_blocked', 429, 'failed sign-ins from this connection'],
] as const) {
  test(`sign-in explains ${code} without showing session-ended`, async ({ page, api }) => {
    api.on('POST', '/api/auth/session', { status, json: { error: code } });
    await page.goto('/signin');
    await page.getByLabel('Account name').fill('Alpha');
    await page.getByLabel('Password', { exact: true }).fill('pass');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toContainText(message);
    await expect(page.getByText('Your session has ended. Please sign in again.')).toHaveCount(0);
  });
}
test('account without characters cannot buy and has a clear next step', async ({ page, api, signIn }) => {
  api.on('GET', '/api/me', { json: { ...me, characters: [] } });
  await signIn();
  await page.goto('/listing/1');
  await expect(page.getByText('Create a character in game first.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Buy', exact: true })).toBeDisabled();
});

test('load more uses cursor and filter changes discard previous rows', async ({ page, api }) => {
  api.on('GET', '/api/listings', request => {
    const query = new URLSearchParams(request.search);
    if (query.has('q')) return { json: { listings: [], nextCursor: null } };
    return { json: query.has('cursor') ? { listings: [{ ...listing, id: 2, name: 'Chainmail Basinet' }], nextCursor: null } : { listings: [listing], nextCursor: 'next-page' } };
  });
  await page.goto('/');
  await page.getByRole('button', { name: 'Load more' }).click();
  await expect(page.getByRole('link', { name: 'Chainmail Basinet' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Bone Slicer' })).toBeVisible();
  await page.getByLabel('Item name').fill('Nothing');
  await page.getByRole('button', { name: 'Apply filters' }).click();
  await expect(page.getByRole('heading', { name: 'No listings match' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Bone Slicer' })).toHaveCount(0);
});

test('a delayed account refresh cannot restore the account after sign-out', async ({ page, api, signIn }) => {
  let calls = 0;
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  api.on('GET', '/api/me', async () => {
    if (++calls > 1) await pending;
    return { json: me };
  });
  api.on('DELETE', '/api/auth/session', { json: { ok: true } });
  await signIn();
  await page.goto('/');
  await expect(page.getByText('Alpha', { exact: true })).toBeVisible();
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await expect.poll(() => calls).toBe(2);
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toBeVisible();
  const response = page.waitForResponse('**/api/me'); release(); await response;
  await page.waitForTimeout(100);
  await expect(page.getByText('Alpha', { exact: true })).toHaveCount(0);
});
test('sign-in stays on the form when loading the account fails', async ({ page, api }) => {
  let authenticated = false;
  api.on('POST', '/api/auth/session', () => { authenticated = true; return { json: session }; });
  api.on('GET', '/api/me', () => authenticated ? { status: 503, json: { error: 'server' } } : unauthorized);
  await page.goto('/signin');
  await page.getByLabel('Account name').fill('Alpha'); await page.getByLabel('Password', { exact: true }).fill('pass');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await expect(page.locator('.signin [role="alert"]')).toContainText('server is unavailable');
});
test('Escape dismisses appraisal from its close button', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Appraise Bone Slicer' }).focus();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Close appraisal' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('region', { name: 'Bone Slicer appraisal' })).toHaveCount(0);
});
test('a character chosen before the BFF (localStorage) is kept after one load, moves to the cookie, and the old key is gone', async ({ page, api, signIn, browser, baseURL }) => {
  api.on('GET', '/api/me', { json: me });
  await signIn();
  await page.goto('/');
  await expect(page.getByLabel('Acting character')).toHaveValue('11');
  await page.evaluate(() => localStorage.setItem('market-character-1', '12'));
  await page.reload();
  await page.waitForSelector('html[data-hydrated]', { state: 'attached' });
  await expect(page.getByLabel('Acting character')).toHaveValue('12');
  expect(await page.evaluate(() => localStorage.getItem('market-character-1'))).toBeNull();
  expect((await page.context().cookies()).find(c => c.name === 'market-character-1')?.value).toBe('12');
  // from now on the server renders it: with no script running at all, the page that arrives already has it selected
  const serverOnly = await browser.newContext({ javaScriptEnabled: false, storageState: await page.context().storageState(), baseURL });
  const plain = await serverOnly.newPage();
  await plain.goto('/');
  await expect(plain.getByLabel('Acting character')).toHaveValue('12');
  await serverOnly.close();
});
test('an open purchase stays fixed but is disabled after pause is refreshed', async ({ page, api, signIn }) => {
  let paused = false;
  api.on('GET', '/api/me', () => ({ json: { ...me, paused } }));
  await signIn();
  await page.goto('/listing/1');
  await page.getByRole('button', { name: 'Buy', exact: true }).click();
  paused = true;
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await expect(page.getByRole('button', { name: 'Confirm purchase' })).toBeDisabled();
  paused = false;
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await expect(page.getByRole('button', { name: 'Confirm purchase' })).toBeEnabled();
  await expect(page.getByRole('dialog')).toContainText('380 MMD');
});
