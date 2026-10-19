import { test, expect } from './fixtures.js';
import { toolsTab } from './workspace-tools.js';

const saved = async (request, id) => (await (await request.get(`/fixtures/${id}/environment-setup`)).json());
async function fixture(page, request, narrow, options = '') {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  const take = await (await request.post(`/fixtures/${id}/reference-video-take`)).json();
  await request.post(`/fixtures/${id}/reference-reels?takeId=${take.id}`);
  const library = await (await request.post(`/fixtures/${id}/environment-library?${options}`)).json();
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/shots`);
  await toolsTab(page, 'References');
  return { id, library, owner: library.assets[0] };
}

for (const narrow of [false, true]) test(`Manual environment choice preserves references and supports None (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  const { id, owner } = await fixture(page, request, narrow, 'selected=false');
  const before = await saved(request, id);
  await expect(page.locator('.shot-references')).toHaveAttribute('data-next-reference-action', 'edit');
  await expect(page.getByRole('button', { name: 'Choose environment views', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Edit references', exact: true }).click();
  await page.getByRole('group', { name: 'Reference sources', exact: true }).getByRole('button', { name: 'Environment', exact: true }).click();
  const editor = page.getByRole('dialog', { name: 'Manage references', exact: true });
  await editor.getByRole('combobox', { name: 'Reference environment' }).selectOption(owner.id);
  await editor.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await saved(request, id)).shot).toEqual(before.shot);
  await page.getByRole('button', { name: 'Edit references', exact: true }).click();
  await page.getByRole('group', { name: 'Reference sources', exact: true }).getByRole('button', { name: 'Environment', exact: true }).click();
  await editor.getByRole('combobox', { name: 'Reference environment' }).selectOption(owner.id);
  await editor.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(editor).toBeHidden();
  await expect.poll(async () => (await saved(request, id)).shot.environmentAssetId).toBe(owner.id);
  const after = await saved(request, id);
  expect(after.shot.images).toEqual(before.shot.images);
  expect(after.prompt).toBe(before.prompt);
  await page.reload(); await toolsTab(page, 'References');
  await page.getByRole('button', { name: 'Edit references', exact: true }).click();
  await page.getByRole('group', { name: 'Reference sources', exact: true }).getByRole('button', { name: 'Environment', exact: true }).click();
  await expect(editor.getByRole('combobox', { name: 'Reference environment' })).toHaveValue(owner.id);
  await editor.getByRole('combobox', { name: 'Reference environment' }).selectOption('');
  await editor.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(editor).toBeHidden();
  expect((await saved(request, id)).shot.environmentInspectionDisabled).toBe(true);
  expect((await saved(request, id)).shot.images).toEqual(before.shot.images);
  expect((await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id)).toHaveLength(0);
});

test('Copy carries the shot environment across presets and makes an omitted library explicit', async ({ page, request }) => {
  test.setTimeout(120000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  const library = await (await request.post(`/fixtures/${id}/environment-library`)).json();
  const environment = library.assets[0];
  expect((await request.post(`/fixtures/${id}/environment-copy-source`)).ok()).toBe(true);
  await request.post(`/fixtures/${id}/production-shot`);
  await page.setViewportSize({ width: 1152, height: 1000 });
  await page.goto(`/projects/${id}/shots`);
  await toolsTab(page, 'References');
  await page.locator('[data-shot-id]').nth(1).click();
  await expect(page.locator('.shot-references')).toHaveAttribute('data-next-reference-action', 'copy');
  await expect(page.locator('.shot-references .reference-next-step')).toHaveAttribute('aria-label', 'Copy from shot…');
  await page.screenshot({ path: 'artifacts/reference-next-copy-desktop.png' });
  await page.getByRole('button', { name: 'Copy from shot…', exact: true }).click();
  const copying = page.getByRole('dialog', { name: 'Copy references from shot', exact: true });
  await expect(copying.getByLabel('Environment library', { exact: true })).toHaveValue(environment.id);
  await expect(copying).toContainText('choose different views for this shot');
  await page.screenshot({ path: 'artifacts/copy-environment-library-desktop.png' });
  await copying.getByRole('button', { name: 'Copy and edit references', exact: true }).click();
  await expect(copying).toBeHidden();
  const copiedEditor = page.getByRole('dialog', { name: 'Manage references', exact: true });
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await copiedEditor.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(copiedEditor).toBeHidden();
  await expect(page.locator('.shot-references')).toHaveAttribute('data-next-reference-action', 'edit');
  let document = await (await request.get(`/fixtures/${id}/production`)).json();
  expect(document.shotContent).toHaveLength(2);
  expect(document.shotContent.every(c => c.environmentAssetId === environment.id)).toBe(true);
  await page.reload(); await toolsTab(page, 'References');
  await page.locator('[data-shot-id]').nth(1).click();
  const preset = page.getByRole('combobox', { name: 'Generation setup', exact: true });
  const currentPreset = await preset.inputValue();
  const otherPreset = (await preset.locator('option').evaluateAll(options => options.map(o => o.value))).find(value => value !== currentPreset);
  expect(otherPreset).toBeTruthy();
  // Hold the new setup's save so opening references cannot race its captured baseline.
  expect((await request.post(`/fixtures/${id}/production-save-paused?paused=true`)).ok()).toBe(true);
  try {
    await preset.selectOption(otherPreset);
    await expect(preset).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Copy from shot…', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Edit references', exact: true })).toBeDisabled();
  } finally {
    expect((await request.post(`/fixtures/${id}/production-save-paused?paused=false`)).ok()).toBe(true);
  }
  await expect(preset).toBeEnabled();
  await expect(preset).toHaveValue(otherPreset);
  // An explicit None is visible before confirmation, remains visible after saving, and is undoable.
  await page.getByRole('button', { name: 'Copy from shot…', exact: true }).click();
  await copying.getByLabel('Environment library', { exact: true }).selectOption('');
  await expect(copying).toContainText('No environment library selected. Copied pictures still work');
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await copying.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  await expect(copying.getByRole('button', { name: 'Copy and edit references', exact: true })).toBeInViewport();
  await page.screenshot({ path: 'artifacts/copy-no-environment-narrow.png' });
  await copying.getByRole('button', { name: 'Copy and edit references', exact: true }).click();
  await expect(copying).toBeHidden();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await copiedEditor.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(copiedEditor).toBeHidden();
  document = await (await request.get(`/fixtures/${id}/production`)).json();
  expect(document.shotContent[1].environmentAssetId ?? null).toBe(null);
  expect(document.shotContent[1].images).toHaveLength(1);
  await expect(page.locator('.shot-references')).toHaveAttribute('data-next-reference-action', 'edit');
  await page.setViewportSize({ width: 1152, height: 1000 });
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
});

test('Picture 10 is displayed as over budget and cannot be applied', async ({ page, request }) => {
  const { id } = await fixture(page, request, false);
  expect((await request.post(`/fixtures/${id}/picture-ten`)).ok()).toBe(true);
  await page.reload(); await toolsTab(page, 'References');
  await page.getByRole('button', { name: 'Edit references', exact: true }).click();
  await page.getByRole('group', { name: 'Reference sources', exact: true }).getByRole('button', { name: 'Environment', exact: true }).click();
  const editor = page.locator('.manual-reference-dialog');
  await expect(editor).toContainText('10/9 pictures');
  await expect(editor.getByRole('alert')).toContainText('Over picture budget');
  await expect(editor.getByRole('button', { name: 'Apply changes', exact: true })).toBeDisabled();
});

for (const narrow of [false, true]) test(`Historical shot environment selections remain read-only (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  const { id } = await fixture(page, request, narrow);
  const created = await request.post(`/fixtures/${id}/historical-environment-selection`);
  expect(created.ok(), await created.text()).toBe(true);
  const job = await created.json();
  await expect.poll(async () => (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.id === job.id)?.state).toBe('Completed');
  const before = await saved(request, id);
  await page.goto(`/projects/${id}/shots?jobId=${job.id}`);
  const review = page.getByRole('dialog', { name: 'Choose environment views', exact: true });
  await expect(review).toBeVisible();
  await expect(review.getByRole('button', { name: /Apply views|Choose views/ })).toHaveCount(0);
  await review.getByText('Request and response', { exact: true }).click();
  await expect(review).toContainText('Request and response');
  await review.getByRole('button', { name: 'Close', exact: true }).click();
  expect((await saved(request, id)).shot).toEqual(before.shot);
  const jobs = (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id);
  expect(jobs).toHaveLength(1);
});
