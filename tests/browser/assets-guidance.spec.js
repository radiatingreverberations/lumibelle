import { modelOptions, submitGuidance, closeComposer } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';
import { assetView, imageAction } from './workspace-tools.js';

async function fixture(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${id}/images`)).json();
  const asset = library.assets[0];
  await page.goto(`/projects/${id}/assets?assetId=${asset.id}`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await assetView(page, 'Asset details');
  return { id, asset, state: async () => (await (await request.get(`/fixtures/${id}`)).json()).assets };
}
const button = page => page.locator('.asset-editor > .asset-preservation').getByRole('button', { name: 'Suggest guidance' });
// The section opens by itself only when a link names the asset; after a reload it starts collapsed.
async function openPreservation(page) {
  const section = page.locator('.asset-editor > .asset-preservation');
  if (await section.getAttribute('open') === null) await section.locator(':scope > summary').click();
  await expect(section).toHaveAttribute('open', '');
}
const dialog = page => page.locator('.guidance-dialog');

test('guidance uses the project model, explicit vision, reviewed application and target-only saves', async ({ page, request }) => {
  const { id, asset, state } = await fixture(page, request);
  await page.getByLabel('Character identity notes', { exact: true }).fill('A round face and steady gaze. Hoodie is a temporary outfit.');
  await page.getByLabel('Character identity', { exact: true }).fill('Keep her face recognizable.');
  await button(page).click();
  const review = dialog(page);
  const composer = page.locator('.ai-assist-dialog').last();
  await expect(composer.getByLabel('Inspect an image', { exact: true })).not.toBeChecked();
  await composer.getByLabel('Inspect an image', { exact: true }).check();
  await expect(composer.getByRole('button', { name: 'Suggest', exact: true })).toBeDisabled();
  await composer.locator('.model-chip').click();
  await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await page.getByRole('button', { name: 'Set as project default', exact: true }).click();
  await composer.getByLabel('Image to inspect').selectOption(`${asset.id}/${asset.images[0].id}`);
  await submitGuidance(page, page.locator('.asset-editor > .asset-preservation'));
  await expect(review.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled({ timeout: 15000 });
  await expect(page.getByLabel('Character identity', { exact: true })).toHaveValue('Keep her face recognizable.');
  const calls = await (await request.get('/fixtures/guidance')).json();
  const call = calls.find(c => c.context.context.Target.ProjectId === id);
  expect(call.model).toBe('mock/alternate'); expect(call.images).toEqual([{ width: 64, height: 80 }]);
  expect(call.context.scope).toBe('CharacterIdentity');
  await review.getByLabel('Suggested guidance', { exact: true }).fill('Preserve her round face and gaze.');
  await review.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(review).toBeHidden(); await expect(button(page)).toBeFocused();
  await expect.poll(async () => (await state()).assets[0].preservationGuidance).toBe('Preserve her round face and gaze.');
  expect((await state()).assets[0].description).toBe('A round face and steady gaze. Hoodie is a temporary outfit.');
  await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true'); await assetView(page, 'Asset details');
  await openPreservation(page); await button(page).click();
  await expect(composer.locator('.assist-composer-model')).toContainText('Alternate mock model');
  await expect(composer.getByLabel('Inspect an image', { exact: true })).not.toBeChecked();
  await page.setViewportSize({ width: 390, height: 844 });
  await submitGuidance(page, page.locator('.asset-editor > .asset-preservation'));
  await expect(review.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled({ timeout: 15000 });
  await review.getByRole('button', { name: 'Close', exact: true }).focus(); await page.keyboard.press('Tab');
  expect(await review.evaluate(el => el.contains(document.activeElement))).toBe(true);
  await page.screenshot({ path: 'test-results/guidance-mobile.png' });
  await page.keyboard.press('Escape'); await expect(review).toBeHidden();
});

test('image guidance names targets changed elsewhere before applying anyway and preserves invalid responses for retry', async ({ page, request, context }) => {
  const { id, asset, state } = await fixture(page, request);
  await assetView(page, 'Images'); await imageAction(page, 'Edit details…');
  let editor = page.locator('.image-review-inspector .media-details-disclosure');
  await editor.getByText('Use guidance', { exact: true }).first().click();
  await editor.getByLabel('Use guidance', { exact: true }).fill('SLOW original guidance');
  await page.locator('.image-review-dialog').getByRole('button', { name: 'Save details', exact: true }).click();
  await expect(editor.getByLabel('Use guidance', { exact: true })).toHaveValue('SLOW original guidance');
  await editor.getByRole('button', { name: 'Suggest guidance', exact: true }).click();
  const review = dialog(page);
  const composer = page.locator('.ai-assist-dialog').last();
  await submitGuidance(page, editor);
  const other = await context.newPage();
  await expect.poll(async () => (await state()).assets[0].images[0].preservationGuidance).toBe('SLOW original guidance');
  await other.goto(`/projects/${id}/assets?assetId=${asset.id}`);
  await expect(other.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await assetView(other, 'Images'); await imageAction(other, 'Edit details…');
  const otherEditor = other.locator('.image-review-inspector .media-details-disclosure');
  await otherEditor.getByText('Use guidance', { exact: true }).first().click();
  await otherEditor.getByLabel('Use guidance', { exact: true }).fill('Changed in another tab.');
  await other.locator('.image-review-dialog').getByRole('button', { name: 'Save details', exact: true }).click();
  await expect.poll(async () => (await state()).assets.find(a => a.id === asset.id).images[0].preservationGuidance).toBe('Changed in another tab.');
  expect((await state()).assets.find(a => a.id === asset.id).images[0].name ?? '').toBe(asset.images[0].name ?? '');
  await expect(review.getByLabel('Suggested guidance', { exact: true })).not.toHaveValue('');
  // The saved change is named once Apply checks it, and the suggestion can then be applied anyway.
  const changed = review.getByText('Since this suggestion was written', { exact: false });
  if (!await changed.isVisible()) {
    try { await review.getByRole('button', { name: 'Apply changes', exact: true }).click({ timeout: 3000 }); }
    catch (error) { if (!await changed.isVisible()) throw error; }
  }
  await expect(review).toContainText('Since this suggestion was written, the guidance was edited. It may not match the image as it is now.');
  await expect(review.getByRole('button', { name: 'Apply anyway', exact: true })).toBeEnabled();
  await review.getByRole('button', { name: 'Close', exact: true }).click(); await other.close();
  await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true'); await assetView(page, 'Asset details');
  await page.getByLabel('Character identity notes', { exact: true }).fill('INVALID');
  await openPreservation(page); await button(page).click(); await submitGuidance(page, page.locator('.asset-editor > .asset-preservation'));
  await expect(review.getByRole('button', { name: 'Retry', exact: true })).toBeVisible();
  await expect(review.getByRole('button', { name: 'Apply changes', exact: true })).toBeDisabled();
  await review.getByText('Response details', { exact: false }).click(); await expect(review.locator('pre')).toContainText('unfinished');
  await review.getByRole('button', { name: 'Close', exact: true }).click();
});

test('look suggestions stay in the edit draft until Save, and cancelling preserves the saved look', async ({ page, request }) => {
  const { state } = await fixture(page, request);
  await assetView(page, 'Asset details'); await page.getByRole('button', { name: 'Add look', exact: true }).click();
  const editor = page.locator('.look-editor-dialog');
  await editor.getByLabel('Look name', { exact: true }).fill('Everyday');
  await editor.getByLabel('Look description', { exact: true }).fill('Soft gray hoodie.');
  await editor.getByRole('button', { name: 'Save look', exact: true }).click();
  await expect.poll(async () => (await state()).assets[0].looks.length).toBe(1);
  await page.getByRole('button', { name: 'Look actions', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Edit look…', exact: true }).click();
  await editor.getByText('Keep consistent in shots', { exact: true }).click();
  await editor.getByLabel('Look description', { exact: true }).fill('Soft gray hoodie with blue cuffs.');
  await editor.getByRole('button', { name: 'Suggest guidance', exact: true }).click();
  const review = dialog(page);
  const composer = page.locator('.ai-assist-dialog').last(); await submitGuidance(page, editor);
  await expect(review.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled({ timeout: 15000 });
  await review.getByLabel('Suggested guidance', { exact: true }).fill('Keep the blue cuffs.');
  await review.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(editor.getByLabel('This look', { exact: true })).toHaveValue('Keep the blue cuffs.');
  expect((await state()).assets[0].looks[0].preservationGuidance).toBe('');
  expect((await state()).assets[0].looks[0].description).toBe('Soft gray hoodie.');
  await editor.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await state()).assets[0].looks[0].preservationGuidance).toBe('');
});
