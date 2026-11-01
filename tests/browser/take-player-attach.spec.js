import { test, expect } from './fixtures.js';
import { toolsTab, closeShotSetup } from './workspace-tools.js';

// The slider renders before the player script attaches; a position chosen in between must be kept.
test('a slider position chosen before the player attaches is the frame it saves', async ({ page, request }) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  expect((await request.post(`/fixtures/${id}/approved`)).ok()).toBe(true);
  expect((await request.post(`/fixtures/${id}/production-shot`)).ok()).toBe(true);
  expect((await request.post(`/fixtures/${id}/take-generation-setup`)).ok()).toBe(true);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await toolsTab(page, 'Generate');
  await closeShotSetup(page);
  // Hold back the player script, as a slow machine does.
  await page.route(/take-player.*\.js/, async route => { await new Promise(r => setTimeout(r, 3000)); await route.continue(); });
  await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
  const player = page.locator('.shot-review-dialog .take-player');
  const position = player.getByRole('slider', { name: 'Video position' });
  await expect(position).toBeVisible({ timeout: 30000 });
  const last = Number(await position.getAttribute('max'));
  await position.focus(); await page.keyboard.press('End'); await page.keyboard.press('ArrowLeft');
  await expect(position).toHaveValue(String(last - 1));
  // Once attached, the player shows the chosen frame rather than the first.
  await expect(player).toHaveAttribute('data-frame-index', String(last - 1), { timeout: 20000 });
});
