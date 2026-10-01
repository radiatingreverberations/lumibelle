import { shotAction, generateTakes, composeProduction, closeShotSetup as closeSetup, toolsTab, closeShotSetup, openShotSetup } from './workspace-tools.js';
import { planningComposer, submitPlanning } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

const activity = page => page.getByRole('dialog', { name: 'AI activity', exact: true });
const review = page => page.locator('.shot-review-dialog');
const state = async (request, id) => (await request.get(`/fixtures/${id}/shots`)).json();
async function pause(page, value) {
  const setupOpen = await page.locator('.shot-setup-dialog').isVisible();
  if (setupOpen) await closeShotSetup(page);
  await page.locator('.ai-activity-trigger').click();
  await expect(activity(page)).toBeVisible();
  const action = activity(page).getByRole('button', { name: `${value ? 'Pause' : 'Resume'} queue ComfyUI`, exact: true });
  if (await action.isVisible()) await action.click();
  else await expect(activity(page).getByRole('button', { name: `${value ? 'Resume' : 'Pause'} queue ComfyUI`, exact: true })).toBeVisible();
  await activity(page).getByRole('button', { name: 'Close AI activity' }).click();
  await expect(activity(page)).not.toBeVisible();
  if (setupOpen) await openShotSetup(page, 'Generation settings');
}
async function setup(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`); await request.post(`/fixtures/${id}/approved`);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Draft shots', exact: true }).click();
  const planning = page.locator('.shot-planning-dialog');
  await submitPlanning(page);
  await planning.getByRole('button', { name: 'Add reviewed shots' }).click();
  await expect(page.getByLabel('Action and camera')).toBeVisible();
  await page.getByLabel('Duration (seconds)').fill('1');
  await page.getByLabel('Duration (seconds)').blur();
  await expect.poll(async () => (await state(request, id)).shots[0].duration).toBe(1);
  await composeProduction(page);
  await toolsTab(page, 'Generate');
  return id;
}

test('video review opens on the first saved take in a visible unfocused page and stays closed after dismissal', async ({ page, request }) => {
  await page.addInitScript(() => Object.defineProperty(document, 'hasFocus', { value: () => false }));
  const id = await setup(page, request);
  await page.getByRole('combobox', { name: 'Default takes', exact: true }).selectOption('2');
  await generateTakes(page);
  await expect(page.locator('.shot-generation-footer .generation-progress-label')).toContainText('1/2 ·');
  await expect(page.locator('.shot-generation-footer .generation-progress-label')).not.toContainText('Take 1');
  await page.screenshot({ path: 'test-results/shot-progress-execution-order.png' });

  await expect(review(page)).toBeVisible();
  await review(page).getByText('Performance and timings', { exact: true }).click();
  await expect(review(page).locator('.video-performance-details')).toContainText('Not requested');
  await expect(review(page).locator('.video-performance-details')).toContainText('Unavailable');
  await expect(review(page).getByRole('button', { name: 'Take 1', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(async () => (await state(request, id)).takes.length).toBe(1);
  await review(page).getByRole('button', { name: 'Close', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).takes.length, { timeout: 15000 }).toBe(2);
  await expect(page.locator('.shot-list-row').first()).toContainText('2 takes');
  await expect(review(page)).not.toBeVisible();
  const generate = page.getByRole('button', { name: 'Generate takes', exact: true });
  const latest = page.getByRole('button', { name: 'Review latest batch', exact: true });
  await expect(latest).toHaveText('Review');
  const [generateBounds, reviewBounds] = await Promise.all([generate.boundingBox(), latest.boundingBox()]);
  expect(Math.abs(generateBounds.y - reviewBounds.y)).toBeLessThan(2);
  expect(reviewBounds.x).toBeGreaterThan(generateBounds.x);
  expect(reviewBounds.width).toBeLessThan(generateBounds.width);
  await latest.click();
  await expect(review(page)).toBeVisible();
  await review(page).getByRole('button', { name: 'Close', exact: true }).click();
  await expect(latest).toBeFocused();
});

test('video batches queue per shot and retain captured settings through edits, reload and explicit review', async ({ page, request }) => {
  test.setTimeout(60000);
  const id = await setup(page, request);
  await pause(page, true);
  try {
    await page.getByRole('combobox', { name: 'Default takes', exact: true }).selectOption('2');
    await generateTakes(page);
    await expect(page.getByRole('button', { name: 'View batch', exact: true })).toBeVisible();
    const footer = page.locator('.shot-generation-footer');
    await expect(footer.getByRole('button', { name: 'Review latest batch', exact: true })).toHaveCount(0);
    await expect(footer.getByRole('button', { name: 'Cancel generation', exact: true })).toBeEnabled();
    await expect(footer.locator('.ai-batch-summary')).toHaveText('0 / 2 takes saved');
    await footer.getByRole('button', { name: 'View batch', exact: true }).click();
    await expect(review(page)).toBeVisible();
    await review(page).getByRole('button', { name: 'Close', exact: true }).click();
    await expect(footer.getByRole('button', { name: 'View batch', exact: true })).toBeFocused();
    const [viewBounds, cancelBounds] = await Promise.all([
      footer.getByRole('button', { name: 'View batch', exact: true }).boundingBox(),
      footer.getByRole('button', { name: 'Cancel generation', exact: true }).boundingBox()
    ]);
    expect(Math.abs(viewBounds.y - cancelBounds.y)).toBeLessThan(2);
    await page.screenshot({ path: 'test-results/shot-generation-footer-queued.png' });
    const original = (await state(request, id)).shots[0];
    await page.getByLabel('Action and camera').fill('A newer author draft while the original waits.');
    await page.getByLabel('Action and camera').blur();
    await expect.poll(async () => (await state(request, id)).shots[0].description).toContain('newer author draft');
    await shotAction(page, 'Duplicate');
    await expect(page.locator('.shot-list-row')).toHaveCount(2);
    await composeProduction(page);
    await generateTakes(page);
    await expect.poll(async () => (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id && j.kind === 'Video').length).toBe(2);
    await page.goto(`/projects/${id}/script`);
    await pause(page, false);
    await expect.poll(async () => (await state(request, id)).takes.length, { timeout: 30000 }).toBe(4);
    await expect(page.getByRole('dialog')).toHaveCount(0);
    const saved = await state(request, id);
    expect(saved.takes.filter(t => t.shotId === original.id).every(t => t.snapshot.shot.description === original.description)).toBe(true);
    await page.goto(`/projects/${id}/shots`);
    await page.locator('.shot-list-row').first().click(); await toolsTab(page, 'Generate');
    await expect(review(page)).not.toBeVisible();
    await closeSetup(page);
  await page.getByRole('button', { name: 'Review latest batch', exact: true }).click();
    await expect(review(page).getByRole('button', { name: 'Take 2', exact: true })).toBeVisible();
    await review(page).getByRole('button', { name: 'Take 2', exact: true }).click();
    await expect(review(page).getByRole('button', { name: 'Take 2', exact: true })).toHaveAttribute('aria-pressed', 'true');
    const video = review(page).locator('video');
    const source = `/media/projects/${id}/takes/${saved.takes.find(t => t.shotId === original.id && t.candidate === 2).id}`;
    await expect(video).toHaveAttribute('src', source);
    await video.evaluate(v => v.currentTime = .5);
    await review(page).getByRole('button', { name: 'One more take', exact: true }).click();
    await expect(review(page)).toContainText(/Generating|Preparing|Take 3/);
    await expect.poll(async () => (await state(request, id)).takes.length, { timeout: 15000 }).toBe(5);
    await expect(video).toHaveAttribute('src', source);
    expect(await video.evaluate(v => v.currentTime)).toBeCloseTo(.5, 1);
    await expect.poll(async () => (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id && j.kind === 'Video' && j.target.shotId === original.id).some(j => j.unread)).toBe(false);
    await review(page).getByRole('button', { name: 'Close', exact: true }).click();
  } finally { await page.goto(`/projects/${id}/shots`); await pause(page, false); }
});

test('waiting video cancellation needs no provider and Activity opens the exact shot on mobile', async ({ page, request }) => {
  const id = await setup(page, request); await pause(page, true);
  try {
    await generateTakes(page);
    await expect(page.getByRole('button', { name: 'View batch', exact: true })).toBeVisible();
    const jobs = (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id && j.kind === 'Video');
    expect(jobs).toHaveLength(1); const job = jobs[0];
    await page.setViewportSize({ width: 390, height: 844 });
    await toolsTab(page, 'References');
    const footer = page.locator('.shot-generation-footer');
    await expect(footer.getByRole('button', { name: 'View batch', exact: true })).toBeVisible();
    await expect(footer.getByRole('button', { name: 'Cancel generation', exact: true })).toBeVisible();
    expect(await footer.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    await page.screenshot({ path: 'test-results/shot-generation-footer-mobile.png' });
    await page.goto(`/projects/${id}/assets`);
    await page.locator('.ai-activity-trigger').click();
    await activity(page).getByLabel('Project', { exact: true }).selectOption(id);
    await activity(page).locator(`[data-job-id="${job.id}"]`).getByRole('link', { name: 'View', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/projects/${(await (await request.get(`/fixtures/${id}/project-route`)).json()).slug}/shots\\?jobId=${job.id}`));
    await expect(review(page)).toBeVisible(); await expect(review(page)).toContainText('Queued in Lumibelle');
    await review(page).getByRole('button', { name: 'Cancel remaining takes', exact: true }).click();
    await expect(review(page)).toContainText('Cancelled. Completed takes are retained.');
    expect((await state(request, id)).takes).toHaveLength(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: 'test-results/queue-video-review-mobile.png' });
    await review(page).getByRole('button', { name: 'Close', exact: true }).click();
    await expect(review(page)).not.toBeVisible();
  } finally { await page.goto(`/projects/${id}/shots`); await pause(page, false); }
});
