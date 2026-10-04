import { expect, signIn, test } from './fixtures';

// Global-state journeys run after both viewport projects and have one worker. Every mutation belongs in a try/finally restoration block.
test('the global-state account starts unpaused and can end its session cleanly', async ({ page }) => {
  await signIn(page, 'global-state-global-alpha');
  await page.goto('/');
  await expect(page.getByText('Market paused')).toHaveCount(0);
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('link', { name: 'Sign in', exact: true })).toBeVisible();
});
