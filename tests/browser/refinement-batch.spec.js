import { test, expect } from './fixtures.js';
import { closeShotSetup, toolsTab } from './workspace-tools.js';

// Acceptance: select drafts, review output settings, queue them held, leave, start later,
// then review each result independently against its source.
test('refine selected takes as a held background batch', async ({ page, request, context }) => {
  test.setTimeout(150000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  expect((await request.post(`/fixtures/${id}/motion-reference-setup?takes=2`)).ok()).toBe(true);
  const state = async () => (await request.get(`/fixtures/${id}/shots`)).json();
  await page.setViewportSize({ width: 1173, height: 1000 });
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await toolsTab(page, 'Generate');
  await closeShotSetup(page);
  await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
  await expect.poll(async () => (await state()).takes.length, { timeout: 60000 }).toBe(2);
  const drafts = (await state()).takes;
  const selectedBefore = (await state()).shots[0].selectedTakeId ?? null;

  await page.goto(`/projects/${id}/shots?view=Takes`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await page.getByRole('button', { name: 'Select multiple', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Select take 1', exact: true }).check();
  await page.getByRole('checkbox', { name: 'Select take 2', exact: true }).check();
  await page.getByRole('button', { name: 'Refine selected…', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Refine from video', exact: true });
  await expect(dialog.getByRole('button', { name: 'Queue 2 refinements', exact: true })).toBeEnabled({ timeout: 30000 });
  await expect(dialog.locator('.refine-row')).toHaveCount(2);
  await expect(dialog.locator('.refine-row').first()).toContainText('608 × 352 → Native · 1 MP · 1344 × 768');
  // A shared size and a per-clip override.
  await dialog.getByLabel('Shared output resolution', { exact: true }).selectOption('preview');
  await dialog.locator('.refine-row').nth(1).getByRole('button', { name: /^Edit / }).click();
  await dialog.getByLabel(/^Output resolution for .*Take 2$/).selectOption('detail');
  await expect(dialog.locator('.refine-row').nth(1)).toContainText('Detail · 0.7 MP · 1120 × 640');
  await dialog.getByRole('radio', { name: 'Hold until started', exact: true }).check();
  await dialog.getByRole('button', { name: 'Queue 2 refinements', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  // The batches open over Shots rather than replacing it.
  await page.getByRole('button', { name: 'View batch', exact: true }).click();
  const batches = page.getByRole('dialog', { name: 'Refinement batches', exact: true });
  const batch = batches.locator('.refinement-batch').first();
  await expect(batch).toContainText('Held');
  await expect(batch).toHaveClass(/is-requested/);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-studio', 'Shots');
  // Preparation continues without the page: close it and return in a new tab.
  await page.close();
  const returned = await context.newPage();
  await returned.setViewportSize({ width: 1173, height: 1000 });
  await returned.goto(`/projects/${id}/shots?view=Takes`);
  await expect(returned.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expect(returned.locator('.refinement-activity-batch')).toContainText('Held');
  await expect(returned.locator('.refinement-activity-batch')).toContainText('0 of 2 clips completed');
  await returned.locator('.shots-library-tools').getByRole('button', { name: 'Refinement batches', exact: true }).click();
  const reopened = returned.getByRole('dialog', { name: 'Refinement batches', exact: true });
  const items = reopened.locator('.refinement-batch').first().locator('.refinement-item');
  await expect(items.nth(0)).toHaveAttribute('data-stage', 'Ready', { timeout: 30000 });
  await expect(items.nth(1)).toHaveAttribute('data-stage', 'Ready', { timeout: 30000 });
  expect((await state()).takes).toHaveLength(2);

  await reopened.getByRole('button', { name: 'Start batch', exact: true }).click();
  // A batch that finishes unseen marks Batches as new; showing its clips clears the mark.
  await reopened.getByRole('button', { name: 'Close', exact: true }).click();
  await returned.locator('[data-workspace-group=center]').getByRole('tab', { name: 'Shot', exact: true }).click();
  const batchesButton = returned.locator('.shots-library-tools').getByRole('button', { name: 'Refinement batches', exact: true });
  await expect(batchesButton.locator('.tab-new')).toHaveText('1', { timeout: 60000 });
  await expect(batchesButton).toHaveAttribute('aria-description', '1 new');
  await batchesButton.click();
  await expect(items.nth(0)).toHaveAttribute('data-stage', 'Completed', { timeout: 60000 });
  await expect(batchesButton.locator('.tab-new')).toHaveCount(0);
  await expect(items.nth(1)).toHaveAttribute('data-stage', 'Completed', { timeout: 60000 });
  await expect(returned.locator('.refinement-counts')).toContainText('Completed2');
  const results = (await state()).takes.filter(t => !drafts.some(d => d.id === t.id));
  expect(results.map(t => [t.width, t.height]).sort()).toEqual([[1120, 640], [832, 480]].sort());
  expect(results.every(t => t.snapshot.motionReference && t.snapshot.shot.generationPreset === 'standard')).toBe(true);
  expect((await state()).shots[0].selectedTakeId ?? null).toBe(selectedBefore);

  await items.nth(0).getByRole('button', { name: 'Compare', exact: true }).click();
  await expect(items.nth(0).getByLabel(/^Source clip for /)).toBeVisible();
  await expect(items.nth(0).getByLabel(/^Refined final for /)).toBeVisible();
  // A final sized to another aspect is letterboxed in a box as large as the source's.
  const heights = () => Promise.all([/^Source clip for /, /^Refined final for /].map(async l => Math.round((await items.nth(0).getByLabel(l).boundingBox()).height)));
  await expect.poll(heights).toEqual([expect.any(Number), expect.any(Number)]);
  const [source, final] = await heights(); expect(final).toBe(source);
  await items.nth(0).locator('.refinement-compare').screenshot({ path: 'test-results/refinement-compare.png' });
  await items.nth(0).getByRole('button', { name: 'Use this take', exact: true }).click();
  await expect(items.nth(0).locator('.refinement-compare')).toHaveCount(0);
  const accepted = results.find(t => t.snapshot.motionReference.takeId === drafts[0].id);
  await expect.poll(async () => (await state()).shots[0].selectedTakeId).toBe(accepted.id);
  await items.nth(1).getByRole('button', { name: 'Compare', exact: true }).click();
  await expect(items.nth(1).locator('.refinement-compare')).toBeVisible();
  await items.nth(1).getByRole('button', { name: 'Mark reviewed', exact: true }).click();
  await expect(items.nth(1).locator('.refinement-compare')).toHaveCount(0);
  await expect(returned.locator('.refinement-counts')).toContainText('Reviewed2 / 2');
  expect(await returned.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await returned.screenshot({ path: 'artifacts/refinement-batch.png', fullPage: true });
});

for (const pause of [false, true]) {
  test(`restored batch is visible and can ${pause ? 'pause, resume and retain completed clips' : 'cancel current and waiting clips'} from Shots`, async ({ page, request }) => {
    test.setTimeout(150000);
    const { id } = await (await request.get('/fixtures/new')).json();
    await request.post(`/fixtures/${id}/approved`);
    await request.post(`/fixtures/${id}/production-shot`);
    await request.post(`/fixtures/${id}/motion-reference-setup?takes=2`);
    const state = async () => (await request.get(`/fixtures/${id}/shots`)).json();
    await page.setViewportSize({ width: 1173, height: 1000 });
    await page.goto(`/projects/${id}/shots`);
    await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    await toolsTab(page, 'Generate');
    await closeShotSetup(page);
    await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
    await expect.poll(async () => (await state()).takes.length, { timeout: 60000 }).toBe(2);
    await page.goto(`/projects/${id}/shots?view=Takes`);
    await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    await page.getByRole('button', { name: 'Select multiple', exact: true }).click();
    await page.getByRole('checkbox', { name: 'Select take 1', exact: true }).check();
    await page.getByRole('checkbox', { name: 'Select take 2', exact: true }).check();
    await page.getByRole('button', { name: 'Refine selected…', exact: true }).click();
    const refine = page.getByRole('dialog', { name: 'Refine from video', exact: true });
    await refine.getByRole('radio', { name: 'Hold until started', exact: true }).check();
    await expect(refine.getByRole('button', { name: 'Queue 2 refinements', exact: true })).toBeEnabled({ timeout: 30000 });
    await refine.getByRole('button', { name: 'Queue 2 refinements', exact: true }).click();
    await expect(refine).toBeHidden();
    await page.reload();
    const activity = page.locator('.refinement-activity-batch');
    await expect(activity).toContainText('Held');
    await expect(activity).toContainText('0 of 2 clips completed');
    await activity.getByRole('button', { name: 'Manage batch', exact: true }).click();
    const batches = page.getByRole('dialog', { name: 'Refinement batches', exact: true });
    await expect(batches.locator('[data-stage="Ready"]')).toHaveCount(2, { timeout: 30000 });
    await batches.getByRole('button', { name: 'Close', exact: true }).click();
    await request.post('/fixtures/video-queue?paused=true');
    await activity.getByRole('button', { name: 'Start batch', exact: true }).click();
    await expect(activity).toHaveAttribute('data-state', 'Running');
    await expect(activity).toContainText('Queued');
    if (pause) {
      await activity.getByRole('button', { name: 'Pause after current clip', exact: true }).click();
      await expect(activity).toContainText('Pausing after the current clip finishes.');
      await activity.getByRole('button', { name: 'Resume batch', exact: true }).click();
      await expect(activity.getByText('Pausing after the current clip finishes.')).toBeHidden();
      await activity.getByRole('button', { name: 'Pause after current clip', exact: true }).click();
      await request.post('/fixtures/video-queue?paused=false');
      await expect(activity).toHaveAttribute('data-state', 'Paused', { timeout: 60000 });
      await expect(activity).toContainText('1 of 2 clips completed');
      await page.reload();
      await expect(activity).toHaveAttribute('data-state', 'Paused');
      await expect(activity.getByRole('button', { name: 'Resume batch', exact: true })).toBeEnabled();
      await activity.screenshot({ path: 'artifacts/refinement-batch-activity.png' });
    }
    await activity.getByRole('button', { name: 'Cancel batch', exact: true }).click();
    await expect(activity).toHaveCount(0, { timeout: 30000 });
    await page.locator('.shots-library-tools').getByRole('button', { name: 'Refinement batches', exact: true }).click();
    await expect(batches.locator('[data-stage="Cancelled"]')).toHaveCount(pause ? 1 : 2);
    await expect(batches.locator('[data-stage="Completed"]')).toHaveCount(pause ? 1 : 0);
    expect((await state()).takes).toHaveLength(pause ? 3 : 2);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  });
}

test('refinement batches close from the header, Done and Escape, and reopen', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  const open = page.locator('.shots-library-tools').getByRole('button', { name: 'Refinement batches', exact: true });
  const dialog = page.getByRole('dialog', { name: 'Refinement batches', exact: true });
  for (const close of [() => page.getByRole('button', { name: 'Close refinement batches', exact: true }).click(),
    () => dialog.getByRole('button', { name: 'Close', exact: true }).click(), () => page.keyboard.press('Escape')]) {
    await open.click();
    await expect(dialog).toContainText('No refinement batches yet');
    await close();
    await expect(dialog).toBeHidden();
  }
});
