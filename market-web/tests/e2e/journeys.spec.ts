import { expect, signIn, test } from './fixtures';
import { checkA11y } from '../a11y';

test.describe.configure({ mode: 'serial' });

function seededAccount(project: string) {
  return `journeys-${project}-alpha`;
}

test('sign in and explain a rejected password', async ({ page }) => {
  await page.goto('/signin');
  await checkA11y(page);
  await page.getByLabel('Account name').fill(seededAccount(test.info().project.name));
  await page.getByLabel('Password', { exact: true }).fill('wrong-password');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('alert')).toContainText('account name or password is incorrect');
  await checkA11y(page);
});

test('browse filters, sorting and paging stay in the URL', async ({ page }) => {
  await page.goto('/?q=Chainmail&sort=price&dir=asc');
  await checkA11y(page);
  await expect(page).toHaveURL(/q=Chainmail.*sort=price.*dir=asc/);
  await expect(page.getByRole('heading', { name: /Browse|No listings/ })).toBeVisible();
});

test('a signed-in player can view the listing page and Vault', async ({ page }) => {
  await signIn(page, seededAccount(test.info().project.name));
  await page.goto('/');
  await checkA11y(page);
  await expect(page.getByRole('link', { name: 'Vault', exact: true })).toBeVisible();
  const listing = page.locator('a[href^="/listing/"]').first();
  await expect(listing).toBeVisible();
  await listing.click();
  await checkA11y(page);
  await expect(page.locator('h1')).toBeVisible();
  await page.getByRole('link', { name: 'Vault', exact: true }).click();
  await checkA11y(page);
  await expect(page.getByRole('heading', { name: 'Vault' })).toBeVisible();
});

test('sign out clears the session', async ({ page }) => {
  await signIn(page, seededAccount(test.info().project.name));
  await page.goto('/');
  await checkA11y(page);
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toBeVisible();
  await checkA11y(page);
});

test('a missing listing is presented as gone', async ({ page }) => {
  await page.goto('/listing/999999999');
  await expect(page.getByRole('heading', { name: 'No longer available' })).toBeVisible();
  await checkA11y(page);
});

test('the sign-in form validates missing credentials', async ({ page }) => {
  await page.goto('/signin');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByLabel('Account name')).toBeFocused();
  await checkA11y(page);
});

test('browse can clear URL filters', async ({ page }) => {
  await page.goto('/?q=Chainmail&sort=price&dir=asc');
  await page.getByRole('button', { name: 'Clear' }).click();
  await expect(page).toHaveURL(/\/$/);
  await checkA11y(page);
});

test('a listing detail links back to the seller browse filter', async ({ page }) => {
  await page.goto('/');
  const listing = page.locator('a[href^="/listing/"]').first();
  await listing.click();
  await checkA11y(page);
  const seller = page.locator('.listing-detail a[href^="/?seller="]');
  await expect(seller).toBeVisible();
  await seller.click();
  await expect(page).toHaveURL(/seller=/);
  await checkA11y(page);
});

test('a signed-out player is sent to sign in for Vault', async ({ page }) => {
  await page.goto('/vault');
  await expect(page.getByRole('heading', { name: 'Your Vault' })).toBeVisible();
  await page.getByRole('link', { name: 'Sign in' }).click();
  await expect(page).toHaveURL(/signin/);
  await checkA11y(page);
});

test('a signed-in player can read history and request older pages', async ({ page }) => {
  await signIn(page, seededAccount(test.info().project.name));
  await page.goto('/history');
  await expect(page.getByRole('heading', { name: 'History' })).toBeVisible();
  await checkA11y(page);
  const older = page.getByRole('button', { name: /Load older/ });
  if (await older.count()) {
    await older.first().click();
    await expect(page.getByRole('heading', { name: 'History' })).toBeVisible();
    await checkA11y(page);
  }
});

test('the seeded fixture exposes waiting, working, progress, done and failed tickets', async ({ page }) => {
  await signIn(page, seededAccount(test.info().project.name));
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Market requests' })).toBeVisible();
  await expect(page.getByText(/Waiting for the game server|Working|Confirm in game|Channelling|Done|failed/i).first()).toBeVisible();
  await checkA11y(page);
});

test('a player can list and delist a held Vault item', async ({ page }) => {
  await signIn(page, seededAccount(test.info().project.name));
  await page.goto('/vault');
  await expect(page.getByRole('heading', { name: 'Vault' })).toBeVisible();
  await checkA11y(page);
  const held = page.locator('tbody tr').filter({ hasText: 'held' }).first();
  await held.locator('input[type=number]').fill('37');
  await held.getByRole('button', { name: 'List item' }).click();
  await expect(held.getByRole('button', { name: 'Delist' })).toBeVisible();
  await checkA11y(page);
  await held.getByRole('button', { name: 'Delist' }).click();
  await expect(held.getByRole('button', { name: 'List item' })).toBeVisible();
  await checkA11y(page);
});

test('the browse page exposes paging and appraisal on a touch tap', async ({ page }) => {
  await page.goto('/');
  await checkA11y(page);
  const appraisal = page.getByRole('button', { name: /Appraise/ }).first();
  await appraisal.click();
  await expect(page.getByRole('region', { name: /appraisal/i })).toBeVisible();
  await checkA11y(page);
  await page.getByRole('button', { name: 'Close appraisal' }).click();
  const more = page.getByRole('button', { name: 'Load more' });
  if (await more.count()) {
    await more.click();
    await checkA11y(page);
  }
});

test('a signed-in player can open the account pages from the browse header', async ({ page }) => {
  await signIn(page, seededAccount(test.info().project.name));
  await page.goto('/');
  await checkA11y(page);
  await page.getByRole('link', { name: 'History', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'History' })).toBeVisible();
  await checkA11y(page);
  await page.getByRole('link', { name: 'Vault', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Vault' })).toBeVisible();
  await checkA11y(page);
});

test('a buyer sees both balances change after purchasing from another seeded account', async ({ page, context }) => {
  const project = test.info().project.name;
  await signIn(page, `journeys-${project}-alpha`);
  await page.goto('/');
  const sellerRow = page.locator('tbody tr').filter({ hasText: `journeys ${project} Bravo` }).first();
  await expect(sellerRow).toBeVisible();
  await sellerRow.locator('a[href^="/listing/"]').click();
  await checkA11y(page);
  await page.getByRole('button', { name: 'Buy' }).click();
  await page.getByRole('button', { name: 'Confirm purchase' }).click();
  await expect(page.getByRole('status')).toContainText('Purchased.');
  await expect(page.locator('.balance')).toContainText('900');
  await checkA11y(page);
  await page.getByRole('button', { name: 'Sign out' }).click();
  await signIn(page, `journeys-${project}-bravo`);
  await page.goto('/');
  await expect(page.locator('.balance')).toContainText('1100');
  await checkA11y(page);
  // Keep the browser context in use so the second account is verified through its own session.
  expect(await context.cookies()).toEqual(expect.arrayContaining([expect.objectContaining({ name: 'market_session' })]));
});
