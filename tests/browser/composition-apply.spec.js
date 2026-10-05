import { openShotSetup } from './workspace-tools.js';
import { test, expect } from './fixtures.js';
test.beforeEach(async ({ request }) => { await request.post('/fixtures/generation-setups/reset'); });

async function review(page, request, conflict = false) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  const response = await request.post(`/fixtures/${id}/composition-review?conflict=${conflict}`);
  expect(response.ok()).toBeTruthy();
  const job = await response.json();
  await page.goto(`/projects/${id}/shots?jobId=${job.id}&view=Prompt`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await openShotSetup(page);
  // Waiting AI changes are the next step: Review changes stands out, and Mark reviewed (which would dismiss them) steps back.
  await expect(page.locator('#shot-setup-prompt-panel .text-request-action').first()).toHaveClass(/request-ready/);
  await expect(page.locator('.shot-setup-dialog').getByRole('button', { name: 'Mark reviewed', exact: true })).toHaveClass(/mud-button-outlined/);
  await page.locator('#shot-setup-prompt-panel').getByRole('button', { name: 'Review changes', exact: true }).click();
  const dialog = page.locator('.prompt-review-dialog:visible');
  await expect(dialog.getByRole('heading', { name: 'Review prompt changes', exact: true })).toBeVisible();
  return { id, job, dialog };
}

for (const narrow of [false, true]) test(`Apply fills an empty prompt after an extra save (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  const { id, job, dialog } = await review(page, request);
  await expect(dialog).toContainText('Empty prompt');
  const apply = dialog.getByRole('button', { name: 'Apply changes', exact: true });
  await apply.focus(); await expect(apply).toBeInViewport(); await apply.press('Enter');
  await expect(dialog).not.toBeVisible();
  const prompt = page.getByLabel('H3 prompt', { exact: true });
  await expect(prompt).toContainText('subject_definitions:');
  await expect(page.getByText('Changes applied. This prompt will be used for new takes.', { exact: true })).toBeVisible();
  const saved = (await (await request.get(`/fixtures/${id}/production`)).json()).compositions[0];
  expect(saved.prompt).toBe(job.prompt); expect(saved.seed).toBe(42); expect(saved.appliedJobId).toBe(job.id);
  expect(saved.acceptedRevisionId).toBeTruthy(); expect(saved.history).toHaveLength(1);
  await expect(page.getByText('Check prompt · The shot or references changed.', { exact: false })).not.toBeVisible();
  await page.reload(); await expect(prompt).toContainText('subject_definitions:');
  const jobs = (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id);
  expect(jobs).toHaveLength(1);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('An Apply conflict stays visible through background refresh', async ({ page, request }) => {
  const { id, job, dialog } = await review(page, request, true);
  await dialog.getByRole('button', { name: 'Apply changes', exact: true }).click();
  const error = dialog.getByRole('alert');
  await expect(error).toContainText('Since this prompt was written, the Direction for AI changed');
  await expect(dialog.locator('.mud-dialog-actions .assisted-apply-warning')).toBeInViewport();
  await expect(dialog.getByRole('button', { name: 'Apply anyway', exact: true })).toBeVisible();
  await request.post(`/fixtures/composition-review-refresh?jobId=${job.id}`);
  // Exercise repeated background refreshes; they must not erase the action error.
  const refreshStarted = Date.now();
  await expect.poll(async () => {
    await request.post(`/fixtures/composition-review-refresh?jobId=${job.id}`);
    await expect(error).toContainText('It may not match the shot as it is now');
    return Date.now() - refreshStarted >= 1000;
  }).toBe(true);
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByLabel('H3 prompt', { exact: true })).toHaveText('');
  const saved = (await (await request.get(`/fixtures/${id}/production`)).json()).compositions[0];
  expect(saved.prompt).toBe(''); expect(saved.directingNotes).toBe('A different camera direction');
  expect(saved.history).toHaveLength(0);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('the shot list flags a composed prompt that is ready for review', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  expect((await request.post(`/fixtures/${id}/composition-review?conflict=false`)).ok()).toBeTruthy();
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  const badge = page.locator('.shot-prompt-review[data-prompt-state=ReviewReady]');
  await expect(badge).toHaveText(/Review ready/);
  await expect(page.locator('.shots-attention-summary')).toHaveText('1 shot needs prompt attention');
  await page.getByLabel('Filter shots by prompt status').selectOption('attention');
  await expect(page.locator('.shot-list-row')).toHaveCount(1);
  // The badge opens the suggestion itself, not the prompt dialog that contains it.
  await badge.click();
  const dialog = page.locator('.prompt-review-dialog:visible');
  await expect(dialog.getByRole('heading', { name: 'Review prompt changes', exact: true })).toBeVisible();
  await expect(page.locator('.shot-setup-dialog')).not.toBeVisible();
  await dialog.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect(badge).toHaveCount(0);
});

test('Apply anyway applies a response whose inputs changed', async ({ page, request }) => {
  const { id, job, dialog } = await review(page, request, true);
  await dialog.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('the Direction for AI changed');
  await dialog.getByRole('button', { name: 'Apply anyway', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect(page.getByLabel('H3 prompt', { exact: true })).toContainText('subject_definitions:');
  const saved = (await (await request.get(`/fixtures/${id}/production`)).json()).compositions[0];
  expect(saved.appliedJobId).toBe(job.id); expect(saved.directingNotes).toBe('A different camera direction');
});
