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
  await returned.locator('.take-selection-toolbar').getByRole('button', { name: 'Refinement batches', exact: true }).click();
  const reopened = returned.getByRole('dialog', { name: 'Refinement batches', exact: true });
  const items = reopened.locator('.refinement-batch').first().locator('.refinement-item');
  await expect(items.nth(0)).toHaveAttribute('data-stage', 'Ready', { timeout: 30000 });
  await expect(items.nth(1)).toHaveAttribute('data-stage', 'Ready', { timeout: 30000 });
  expect((await state()).takes).toHaveLength(2);

  await reopened.getByRole('button', { name: 'Start batch', exact: true }).click();
  await expect(items.nth(0)).toHaveAttribute('data-stage', 'Completed', { timeout: 60000 });
  await expect(items.nth(1)).toHaveAttribute('data-stage', 'Completed', { timeout: 60000 });
  await expect(returned.locator('.refinement-counts')).toContainText('Completed2');
  const results = (await state()).takes.filter(t => !drafts.some(d => d.id === t.id));
  expect(results.map(t => [t.width, t.height]).sort()).toEqual([[1120, 640], [832, 480]].sort());
  expect(results.every(t => t.snapshot.motionReference && t.snapshot.shot.generationPreset === 'standard')).toBe(true);
  expect((await state()).shots[0].selectedTakeId ?? null).toBe(selectedBefore);

  await items.nth(0).getByRole('button', { name: 'Compare', exact: true }).click();
  await expect(items.nth(0).getByLabel(/^Source clip for /)).toBeVisible();
  await expect(items.nth(0).getByLabel(/^Refined final for /)).toBeVisible();
  await items.nth(0).getByRole('button', { name: 'Use this take', exact: true }).click();
  const accepted = results.find(t => t.snapshot.motionReference.takeId === drafts[0].id);
  await expect.poll(async () => (await state()).shots[0].selectedTakeId).toBe(accepted.id);
  await items.nth(1).getByRole('button', { name: 'Mark reviewed', exact: true }).click();
  await expect(returned.locator('.refinement-counts')).toContainText('Reviewed2 / 2');
  expect(await returned.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await returned.screenshot({ path: 'artifacts/refinement-batch.png', fullPage: true });
});
