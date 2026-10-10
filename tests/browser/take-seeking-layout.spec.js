import { test, expect } from './fixtures.js';
import { toolsTab, closeShotSetup } from './workspace-tools.js';

test('loading a sought frame leaves the player and its controls in place', async ({ page, request }) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  expect((await request.post(`/fixtures/${id}/approved`)).ok()).toBe(true);
  expect((await request.post(`/fixtures/${id}/production-shot`)).ok()).toBe(true);
  expect((await request.post(`/fixtures/${id}/take-generation-setup`)).ok()).toBe(true);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await toolsTab(page, 'Generate');
  await closeShotSetup(page);
  // Generation is served by the isolated BrowserHost's mock video generator.
  await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
  const player = page.locator('.shot-review-dialog .take-player');
  const position = player.getByRole('slider', { name: 'Video position' });
  const save = player.getByRole('button', { name: 'Save frame', exact: true });
  const geometry = () => player.evaluate(el => ['.take-player-stage', '.take-transport', '.take-frame-actions', '.take-frame-buttons'].map(selector => {
    const { x, y, width, height } = el.querySelector(selector).getBoundingClientRect();
    return { x, y, width, height };
  }));
  for (const width of [1440, 1100, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    await position.press('Home');
    await expect(player).toHaveAttribute('data-frame-index', '0');
    await expect(save).toBeEnabled();
    let release;
    const pending = new Promise(resolve => { release = resolve; });
    await page.route('**/takes/*/frames/20', async route => {
      await pending;
      await route.continue().catch(() => {});
    });
    try {
      // Focus first, so any normal focus scrolling is excluded from the measurement.
      await position.focus();
      const before = await geometry();
      await position.evaluate(el => { el.value = '20'; el.dispatchEvent(new Event('input', { bubbles: true })); });
      await expect(position).toHaveAttribute('aria-valuetext', /^Frame 21 of \d+, /);
      await expect(player.locator('.take-frame-position')).toBeVisible();
      await expect(player.locator('.take-frame-position')).toContainText('Frame 21 ·');
      await expect(player.locator('.take-frame-loading')).toBeVisible();
      for (const button of await player.locator('.take-frame-buttons button').all()) {
        await expect(button).toBeEnabled();
      }
      expect(await geometry()).toEqual(before);
      // A click during loading waits for the chosen frame instead of using the previous one.
      if (width === 390) {
        await save.click();
        await expect(save).toBeDisabled();
        await expect(page.getByRole('region', { name: 'Save image to Assets', exact: true })).toHaveCount(0);
      }
      release();
      await expect(player).toHaveAttribute('data-frame-index', '20');
      await expect(save).toBeEnabled();
      await expect(player.locator('.take-frame-loading')).toHaveCount(0);
      if (width === 390) {
        const panel = page.getByRole('region', { name: 'Save image to Assets', exact: true });
        await expect(panel).toContainText('Frame 21 · saved from the take');
        await panel.getByRole('button', { name: 'Cancel', exact: true }).click();
      } else {
        expect(await geometry()).toEqual(before);
      }
    } finally {
      release();
      await page.unroute('**/takes/*/frames/20');
    }
  }
  await save.focus();
  await page.mouse.move(0, 0);
  await expect(player.locator('.take-frame-position')).toBeHidden();
});
