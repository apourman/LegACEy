import { test, expect } from '@playwright/test';

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
test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/me') return route.fulfill({ status: 401, json: { error: 'unauthorized' } });
    if (path === '/api/tickets') return route.fulfill({ json: [] });
    if (path === '/api/facets') return route.fulfill({ json: {
      itemTypes: [{ value: 'MeleeWeapon', label: 'MeleeWeapon', count: 1 }],
      sorts: [{ value: 'newest', label: 'Newest', defaultDir: 'desc' }, { value: 'price', label: 'Price', defaultDir: 'asc' }],
    } });
    if (path === '/api/listings') return route.fulfill({ json: { listings: [listing], nextCursor: null } });
    if (path === '/api/listings/suggest') return route.fulfill({ json: { suggestions: ['Bone Slicer'] } });
    if (path === '/api/listings/1') return route.fulfill({ json: { ...listing, lines: ['Value: 500', 'Damage: 34 - 52'], spells: [{ name: 'Blood Drinker', cantrip: false }] } });
    return route.fulfill({ status: 404, json: { error: 'not_found' } });
  });
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

test('purchase retries keep character price and key, then refresh account and link Vault', async ({ page }) => {
  await page.route('**/api/me', route => route.fulfill({ json: me }));
  let attempts: object[] = [];
  await page.route('**/api/listings/1/purchase', async route => {
    attempts.push(route.request().postDataJSON());
    if (attempts.length === 1) return route.abort('failed');
    await route.fulfill({ json: { status: 'ok', balance: 380, price: 120, fee: 0, listingId: 1, itemGuid: 10 } });
  });
  await page.goto('/listing/1');
  await page.getByLabel('Acting character').selectOption('12');
  await page.getByRole('button', { name: 'Buy', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Alpha Second');
  await expect(page.getByRole('dialog')).toContainText('380 MMD');
  await page.getByRole('button', { name: 'Confirm purchase' }).click();
  await expect(page.getByText('The response was lost.', { exact: false })).toBeVisible();
  await page.evaluate(() => localStorage.setItem('market-character-1', '11'));
  await page.getByRole('button', { name: 'Retry this attempt' }).click();
  await expect(page.getByRole('link', { name: 'Go to Vault' })).toBeVisible();
  expect(attempts).toHaveLength(2);
  expect(attempts[0]).toEqual(attempts[1]);
  expect(attempts[0]).toMatchObject({ characterId: 12, expectedPrice: 120, count: 3 });
});

test('price changed requires a fresh confirmation and fresh key', async ({ page }) => {
  await page.route('**/api/me', route => route.fulfill({ json: me }));
  let changed = false;
  await page.route('**/api/listings/1', route => route.fulfill({ json: { ...listing, price: changed ? 150 : 120, lines: [], spells: [] } }));
  const attempts: { idempotencyKey: string; expectedPrice: number }[] = [];
  await page.route('**/api/listings/1/purchase', async route => {
    attempts.push(route.request().postDataJSON());
    if (!changed) { changed = true; return route.fulfill({ status: 409, json: { error: 'price_changed', price: 150 } }); }
    return route.fulfill({ json: { status: 'ok', listingId: 1, itemGuid: 10, price: 150, fee: 0, balance: 350 } });
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

test('banned session returns to sign-in and signing in explains the ban', async ({ page }) => {
  let banned = false;
  await page.route('**/api/me', route => route.fulfill(banned ? { status: 401, json: { error: 'unauthorized' } } : { json: me }));
  await page.route('**/api/auth/login', route => route.fulfill({ status: 403, json: { error: 'banned' } }));
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

test('phone browse fits viewport, appraisal opens on tap and dialog traps keyboard focus', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.route('**/api/me', route => route.fulfill({ json: me }));
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
  test(`sign-in explains ${code} without showing session-ended`, async ({ page }) => {
    await page.route('**/api/auth/login', route => route.fulfill({ status, json: { error: code } }));
    await page.goto('/signin');
    await page.getByLabel('Account name').fill('Alpha');
    await page.getByLabel('Password', { exact: true }).fill('pass');
    await page.getByRole('button', { name: 'Sign in' }).click();
    await expect(page.getByRole('alert')).toContainText(message);
    await expect(page.getByText('Your session has ended. Please sign in again.')).toHaveCount(0);
  });
}
test('account without characters cannot buy and has a clear next step', async ({ page }) => {
  await page.route('**/api/me', route => route.fulfill({ json: { ...me, characters: [] } }));
  await page.goto('/listing/1');
  await expect(page.getByText('Create a character in game first.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Buy', exact: true })).toBeDisabled();
});

test('load more uses cursor and filter changes discard previous rows', async ({ page }) => {
  await page.route('**/api/listings**', route => {
    const url = new URL(route.request().url());
    if (url.pathname !== '/api/listings') return route.fallback();
    if (url.searchParams.has('q')) return route.fulfill({ json: { listings: [], nextCursor: null } });
    return route.fulfill({ json: url.searchParams.has('cursor') ? { listings: [{ ...listing, id: 2, name: 'Chainmail Basinet' }], nextCursor: null } : { listings: [listing], nextCursor: 'next-page' } });
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

test('a delayed account refresh cannot restore the account after sign-out', async ({ page }) => {
  let calls = 0;
  let release!: () => void;
  const pending = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/me', async route => {
    if (++calls > 1) await pending;
    await route.fulfill({ json: me });
  });
  await page.route('**/api/auth/logout', route => route.fulfill({ json: { ok: true } }));
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
test('sign-in stays on the form when loading the account fails', async ({ page }) => {
  let authenticated = false;
  await page.route('**/api/auth/login', route => { authenticated = true; return route.fulfill({ json: { ok: true } }); });
  await page.route('**/api/me', route => route.fulfill(authenticated ? { status: 503, json: { error: 'server' } } : { status: 401, json: { error: 'unauthorized' } }));
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
test('an open purchase stays fixed but is disabled after pause is refreshed', async ({ page }) => {
  let paused = false;
  await page.route('**/api/me', route => route.fulfill({ json: { ...me, paused } }));
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
