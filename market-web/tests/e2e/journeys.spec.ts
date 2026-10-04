import { expect, signIn, test } from './fixtures';

function seededAccount(project: string) {
  return `journeys-${project}-alpha`;
}

test('sign in and explain a rejected password', async ({ page }) => {
  await page.goto('/signin');
  await page.getByLabel('Account name').fill(seededAccount(test.info().project.name));
  await page.getByLabel('Password', { exact: true }).fill('wrong-password');
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('alert')).toContainText('account name or password is incorrect');
});

test('browse filters, sorting and paging stay in the URL', async ({ page }) => {
  await page.goto('/?q=Chainmail&sort=price&dir=asc');
  await expect(page).toHaveURL(/q=Chainmail.*sort=price.*dir=asc/);
  await expect(page.getByRole('heading', { name: /Browse|No listings/ })).toBeVisible();
});

test('a signed-in player can view the listing page and Vault', async ({ page }) => {
  await signIn(page, seededAccount(test.info().project.name));
  await page.goto('/');
  await expect(page.getByRole('link', { name: 'Vault', exact: true })).toBeVisible();
  const listing = page.locator('a[href^="/listing/"]').first();
  await expect(listing).toBeVisible();
  await listing.click();
  await expect(page.locator('h1')).toBeVisible();
  await page.getByRole('link', { name: 'Vault', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Vault' })).toBeVisible();
});

test('sign out clears the session', async ({ page }) => {
  await signIn(page, seededAccount(test.info().project.name));
  await page.goto('/');
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toBeVisible();
});
