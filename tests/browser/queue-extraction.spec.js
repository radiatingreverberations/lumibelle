import { extractionComposer, submitExtraction } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

const modal = page => page.locator('.extraction-dialog');
const activity = page => page.getByRole('dialog', { name: 'AI activity', exact: true });
async function setup(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/coverage-script`);
  await page.goto(`/projects/${id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  return id;
}
async function pause(page, paused) {
  await page.locator('.ai-activity-trigger').click();
  await activity(page).getByRole('button', { name: `${paused ? 'Pause' : 'Resume'} queue OpenRouter`, exact: true }).click();
  await activity(page).getByRole('button', { name: 'Close AI activity' }).click();
}
async function jobFor(request, project) { return (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.target.projectId === project); }
async function state(request, id) { return (await (await request.get(`/fixtures/${id}`)).json()); }
async function savedName(request, job) { return (await (await request.get(`/fixtures/ai-jobs/${job}/review`)).json()).value?.proposals?.[0]?.name; }
async function start(page) {
  await page.getByRole('button', { name: 'Extract assets from script', exact: true }).click();
  await submitExtraction(page);
}

test('queued extraction survives closing, reload and navigation, preserving its approval and scene choices', async ({ page, request }) => {
  const id = await setup(page, request); const original = (await (await request.get(`/fixtures/${id}/script-source`)).json()).id;
  await pause(page, true);
  try {
    await page.getByRole('button', { name: 'Extract assets from script', exact: true }).click();
    await extractionComposer(page).locator('.coverage-scene').filter({ hasText: 'EMPTY FIELD' }).getByRole('checkbox').uncheck();
    await submitExtraction(page);
    await expect(modal(page)).toContainText('Queued in Lumibelle');
    await page.screenshot({ path: 'test-results/extraction-request-actions-desktop.png' });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: 'test-results/extraction-request-actions-mobile.png' });
    await page.setViewportSize({ width: 1280, height: 720 });
    await modal(page).locator('.dialog-actions').getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page.getByRole('button', { name: /^Queued.*View request$/ })).toBeVisible();
    await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    await page.getByRole('button', { name: /^Queued.*View request$/ }).click();
    const captured = modal(page).locator('.extraction-request-details');
    await captured.locator('summary').first().click();
    await expect(captured.locator('.extraction-captured-scenes li')).toHaveCount(1);
    await expect(captured.locator('.extraction-captured-scenes')).not.toContainText('EMPTY FIELD');
    await expect(modal(page).locator('.coverage-scene input')).toHaveCount(0);
    await modal(page).locator('.dialog-actions').getByRole('button', { name: 'Close', exact: true }).click();
    await page.locator('.project-tabs').getByRole('link', { name: 'Script', exact: true }).click();
    await request.post(`/fixtures/${id}/coverage-script?mode=change`);
  } finally { await pause(page, false); }
  await expect.poll(async () => (await jobFor(request, id))?.state).toBe('Completed');
  const job = await jobFor(request, id); expect(job.cancelRequested).toBe(false); expect(job.unread).toBe(true);
  await expect(modal(page)).not.toBeVisible(); expect((await state(request, id)).assets.extractionReviews).toHaveLength(0);
  await page.locator('.ai-activity-trigger').click();
  await activity(page).getByRole('tab', { name: 'History', exact: true }).click();
  await activity(page).getByLabel('Project', { exact: true }).selectOption(id);
  await activity(page).locator(`[data-job-id="${job.id}"]`).getByRole('link', { name: 'Review', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`assets\\?jobId=${job.id}`));
  await expect(modal(page)).toContainText('A newer saved script is available');
  await expect.poll(async () => (await jobFor(request, id)).unread).toBe(false);
  await modal(page).getByLabel('Asset name', { exact: true }).fill('Reviewed after navigation');
  await expect.poll(() => savedName(request, job.id)).toBe('Reviewed after navigation');
  await modal(page).locator('.apply-extraction').click();
  await expect(modal(page)).not.toBeVisible();
  const result = (await state(request, id)).assets;
  expect(result.assets[0].name).toBe('Reviewed after navigation'); expect(result.extractionReviews).toHaveLength(1);
  expect(result.extractionReviews[0].id).toBe(job.id); expect(result.extractionReviews[0].approvedScriptId).toBe(original);
  expect(result.extractionReviews[0].scenes).toHaveLength(1);
  await page.reload(); await expect(modal(page)).toContainText('already reviewed');
  await expect(modal(page).locator('.apply-extraction')).toBeDisabled();
  expect((await state(request, id)).assets.assets).toHaveLength(1);
});

test('review drafts reopen with author edits and concurrent tabs require an explicit conflict resolution', async ({ page, context, request }) => {
  const id = await setup(page, request); await start(page);
  await modal(page).getByLabel('Asset name', { exact: true }).fill('First reviewed name');
  const job = await jobFor(request, id);
  await expect.poll(() => savedName(request, job.id)).toBe('First reviewed name');
  const other = await context.newPage(); await other.goto(`/projects/${id}/assets?jobId=${job.id}`);
  await expect(modal(other).getByLabel('Asset name', { exact: true })).toHaveValue('First reviewed name');
  await page.bringToFront(); await modal(page).getByLabel('Asset name', { exact: true }).fill('New saved decision');
  await expect.poll(() => savedName(request, job.id)).toBe('New saved decision');
  await other.bringToFront(); await modal(other).getByLabel('Asset name', { exact: true }).fill('Conflicting local decision');
  await expect(modal(other).getByRole('alert')).toContainText('Another tab saved different review decisions');
  await expect(modal(other).locator('.apply-extraction')).toBeDisabled();
  await expect(modal(other).getByLabel('Asset name', { exact: true })).toHaveValue('Conflicting local decision');
  await expect(modal(other).getByRole('link', { name: 'Download my decisions' })).toHaveAttribute('href', /^data:application\/json;base64,/);
  await modal(other).getByRole('button', { name: 'Load saved review', exact: true }).click();
  await expect(modal(other).getByLabel('Asset name', { exact: true })).toHaveValue('New saved decision');
  await other.setViewportSize({ width: 390, height: 844 });
  await modal(other).locator('.dialog-actions').getByRole('button', { name: 'Close', exact: true }).focus();
  await other.keyboard.press('Tab'); await expect.poll(() => modal(other).evaluate(el => el.contains(document.activeElement))).toBe(true);
  expect(await other.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await other.screenshot({ path: 'artifacts/validation/plan005-extraction-review-mobile.png' });
  await modal(other).locator('.apply-extraction').click(); await expect(modal(other)).not.toBeVisible();
  await page.bringToFront(); await modal(page).locator('.apply-extraction').click();
  await expect(modal(page)).not.toBeVisible(); expect((await state(request, id)).assets.extractionReviews).toHaveLength(1);
});
