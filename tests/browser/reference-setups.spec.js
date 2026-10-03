import { generateTakes, toolsTab, compositionState, composeProduction, openShotSetup, closeShotSetup } from './workspace-tools.js';
import { test, expect } from './fixtures.js';

async function fixture(page, request) {
  const project = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${project.id}/images`);
  await request.post(`/fixtures/${project.id}/approved`);
  const seeded = await request.post(`/fixtures/${project.id}/reference-setups`);
  expect(seeded.ok()).toBe(true);
  const assets = await seeded.json(), person = assets.assets[0];
  const state = () => compositionState(request, project.id);
  const library = async () => (await (await request.get(`/fixtures/${project.id}`)).json()).assets;
  await page.setViewportSize({ width: 1440, height: 950 });
  return { project, person, state, library };
}

test('project aspect follows, overrides, cancels drafts and keeps captured take dimensions', async ({ page, request }) => {
  test.setTimeout(70000);
  const { project, state } = await fixture(page, request);
  await page.goto(`/projects/${project.id}/settings`);
  const settings = page.locator('.project-video-defaults');
  await expect(settings).toHaveAttribute('data-interactive', 'true');
  await settings.getByLabel('Video aspect').selectOption('9:16');
  await settings.getByRole('button', { name: 'Save video defaults' }).click();
  await expect(settings).toContainText('Video defaults saved.');
  await settings.getByLabel('Video aspect').selectOption('1:1');
  await settings.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(settings.getByLabel('Video aspect')).toHaveValue('9:16');
  await page.getByRole('link', { name: 'Shots', exact: true }).click(); await toolsTab(page, 'Generate');
  const aspect = page.getByLabel('Shot aspect', { exact: true });
  // The shot follows the project's aspect until it overrides it in the generation output.
  await expect(aspect).toHaveValue(''); await expect(aspect.locator('option[value=""]')).toHaveText('Project · 9:16');
  await aspect.selectOption('1:1');
  await expect.poll(async () => (await state()).shots[0].aspectOverride).toBe('1:1');
  await composeProduction(page);
  await generateTakes(page);
  const review = page.locator('.shot-review-dialog');
  await expect(review).toBeVisible({ timeout: 20000 });
  await expect.poll(async () => (await state()).takes.length).toBe(1);
  const take = (await state()).takes[0]; expect(take.snapshot.shot.aspect).toBe('1:1');
  await review.getByRole('button', { name: 'Close', exact: true }).click();
  await toolsTab(page, 'Generate');
  await aspect.selectOption('');
  await closeShotSetup(page);
  await page.getByRole('tab', { name: 'Takes', exact: true }).click();
  await expect(page.locator('.unified-take-card')).toContainText(`${take.width} × ${take.height}`);
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await settings.getByLabel('Video aspect').selectOption('16:9'); await settings.getByRole('button', { name: 'Save video defaults' }).click();
  await expect(settings).toContainText('Video defaults saved.');
  await page.getByRole('link', { name: 'Shots', exact: true }).click(); await toolsTab(page, 'Generate');
  await expect(aspect.locator('option[value=""]')).toHaveText('Project · 16:9'); await expect(aspect).toHaveValue('');
  expect((await state()).takes[0].snapshot.shot.aspect).toBe('1:1');
});
