import { test, expect } from './fixtures.js';
import { toolsTab, closeShotSetup } from './workspace-tools.js';

test('take tools toggle, switch, and close around one shared player', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  for (const fixture of ['approved', 'production-shot', 'take-generation-setup']) {
    expect((await request.post(`/fixtures/${id}/${fixture}`)).ok()).toBe(true);
  }
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await toolsTab(page, 'Generate');
  await closeShotSetup(page);
  // Only the isolated BrowserHost's mock generator is used.
  await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
  const review = page.locator('.shot-review-dialog');
  const player = review.locator('.take-player');
  const panel = review.locator('#take-review-tool-panel');
  const tools = player.getByRole('group', { name: 'Take tools' });
  const position = player.getByRole('slider', { name: 'Video position' });
  await expect(player).toHaveAttribute('data-frame-index', '0');
  const end = await position.getAttribute('max');

  for (const name of ['Continue', 'Lead into', 'Trim', 'Save frame']) {
    const button = tools.getByRole('button', { name, exact: true });
    await expect(button).toHaveAttribute('aria-expanded', 'false');
    await button.click();
    await expect(button).toHaveAttribute('aria-expanded', 'true');
    await expect(panel).toBeVisible();
    await expect(player).toHaveCount(1);
    await button.click();
    await expect(panel).toBeHidden();
    await expect(button).toHaveAttribute('aria-expanded', 'false');
    await button.click();
    await panel.getByRole('button', { name: 'Close panel', exact: true }).click();
    await expect(panel).toBeHidden();
    await expect(button).toBeFocused();
  }

  await tools.getByRole('button', { name: 'Continue', exact: true }).click();
  const snap = panel.getByLabel(/Use nearby saved motion boundary/);
  if (await snap.count()) await snap.uncheck();
  await panel.getByLabel('Next action', { exact: true }).fill('Keep walking through the doorway.');
  await panel.getByLabel('New dialogue', { exact: true }).fill('Riley: This way.');
  await panel.getByLabel('Added duration', { exact: true }).fill('3');
  await panel.getByLabel('Extension prompt', { exact: true }).fill('Keep my edited prompt.');
  await position.fill('12');
  // Seeking must update the frame without recreating the form and losing edits.
  if (await snap.count()) await snap.uncheck();
  await expect(panel.getByRole('heading')).toHaveText('Extend from frame 13');
  await expect(panel.getByLabel('Next action', { exact: true })).toHaveValue('Keep walking through the doorway.');
  await expect(panel.getByLabel('New dialogue', { exact: true })).toHaveValue('Riley: This way.');
  await expect(panel.getByLabel('Added duration', { exact: true })).toHaveValue('3');
  await expect(panel.getByLabel('Extension prompt', { exact: true })).toHaveValue('Keep my edited prompt.');
  await tools.getByRole('button', { name: 'Lead into', exact: true }).click();
  await expect(panel.getByRole('heading')).toHaveText('Lead into frame 13');
  await panel.getByLabel('Lead-in action', { exact: true }).fill('She approaches the doorway.');
  await position.fill('6');
  await expect(panel.getByRole('heading')).toHaveText('Lead into frame 7');
  await expect(panel.getByLabel('Lead-in action', { exact: true })).toHaveValue('She approaches the doorway.');
  await expect(tools.locator('[aria-expanded=true]')).toHaveCount(1);
  await tools.getByRole('button', { name: 'Trim', exact: true }).click();
  await expect(panel.getByRole('heading')).toHaveText('Trim take');
  await panel.getByRole('slider', { name: 'Trim start frame' }).fill('5');
  await expect(position).toHaveAttribute('min', '5');
  await position.press('Home');
  await expect(player).toHaveAttribute('data-frame-index', '5');
  await tools.getByRole('button', { name: 'Save frame', exact: true }).click();
  await expect(panel).toContainText('Frame 6 · saved from the take');
  await expect(position).toHaveAttribute('min', '0');
  await expect(position).toHaveAttribute('max', end);
  await expect(tools.locator('[aria-expanded=true]')).toHaveCount(1);
  await position.fill('8');
  await expect(panel).toContainText('Frame 9 · saved from the take');
  await expect(panel.getByLabel('Image name', { exact: true })).toHaveValue(/frame 9$/);
  await expect(panel.getByLabel('New asset name', { exact: true })).toHaveValue(/frame 9$/);
  await panel.getByLabel('Image name', { exact: true }).fill('Chosen expression');
  await panel.getByLabel('New asset name', { exact: true }).fill('Riley study');
  await panel.getByLabel('What to keep consistent (optional)', { exact: true }).fill('Keep the glasses.');
  await position.fill('10');
  await expect(panel).toContainText('Frame 11 · saved from the take');
  await expect(panel.getByLabel('Image name', { exact: true })).toHaveValue('Chosen expression');
  await expect(panel.getByLabel('New asset name', { exact: true })).toHaveValue('Riley study');
  await expect(panel.getByLabel('What to keep consistent (optional)', { exact: true })).toHaveValue('Keep the glasses.');

  await page.setViewportSize({ width: 390, height: 844 });
  expect(await review.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  await panel.getByRole('button', { name: 'Close panel', exact: true }).click();
  await expect(panel).toBeHidden();
  await expect(tools.getByRole('button', { name: 'Save frame', exact: true })).toBeFocused();
});
