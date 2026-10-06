import { test, expect } from './fixtures.js';
import { openShotSetup, closeShotSetup } from './workspace-tools.js';

test('bulk generation queues captured overrides once and retains saved setup settings', async ({ page, request }) => {
  await request.post('/fixtures/generation-setups/reset');
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  const response = await request.post(`/fixtures/${id}/composition-review?conflict=false`);
  expect(response.ok()).toBeTruthy();
  const job = await response.json();
  await page.goto(`/projects/${id}/shots?jobId=${job.id}&view=Prompt`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await openShotSetup(page);
  await page.locator('#shot-setup-prompt-panel').getByRole('button', { name: 'Review changes', exact: true }).click();
  const review = page.locator('.prompt-review-dialog:visible');
  await review.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(review).not.toBeVisible();
  await closeShotSetup(page);
  const setup = async () => (await (await request.get(`/fixtures/${id}/production`)).json()).compositions[0];
  const original = await setup();
  await page.getByRole('button', { name: 'Bulk operations', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Generate takes…', exact: true }).click();
  const dialog = page.locator('.bulk-generate-dialog');
  await dialog.getByLabel('Bulk generate preset', { exact: true }).selectOption('turbo4');
  await dialog.getByLabel('Bulk generate take count', { exact: true }).selectOption('2');
  await dialog.getByRole('button', { name: 'Queue selected (1)', exact: true }).click();
  await expect(dialog.getByRole('status')).toContainText('Queued 1 shot');
  await expect(dialog.getByRole('button', { name: 'Queue selected (0)', exact: true })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  const takes = async () => (await (await request.get(`/fixtures/${id}/shots`)).json()).takes;
  await expect.poll(async () => (await takes()).length, { timeout: 20000 }).toBe(2);
  for (const take of await takes()) {
    expect(take.snapshot.preset.key).toBe('turbo4');
    expect(take.snapshot.prompt).toBe(original.prompt);
  }
  expect(await setup()).toEqual(original);
  const jobs = (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id && j.kind === 'Video');
  expect(jobs).toHaveLength(1);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});
