import { test, expect } from './fixtures.js';
import { imageOutput, toolsTab } from './workspace-tools.js';

async function fixture(request) {
  const project = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${project.id}/images`)).json();
  return { project, library };
}
async function openAssets(page, project) {
  await page.goto(`/projects/${project.id}/assets`); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
}
const state = async (request, project) => (await (await request.get(`/fixtures/${project.id}`)).json()).assets;

for (const width of [1440, 390]) test(`Trash bulk selection covers every page and keeps the chosen project scope at ${width}px`, async ({ page, request }, testInfo) => {
  test.setTimeout(120000);
  await page.setViewportSize({ width, height: 1000 });
  const first = await fixture(request); const second = await fixture(request);
  await request.post(`/fixtures/${first.project.id}/trash-many`);
  await request.post(`/fixtures/${second.project.id}/trash/${second.library.assets[0].images[0].id}`);
  await page.goto('/trash');
  await expect(page.locator('.trash-toolbar')).toHaveAttribute('data-interactive', 'true');
  const scope = page.locator('.trash-toolbar select');
  const selection = page.locator('.trash-selection-count');
  await scope.selectOption(first.project.id);
  await expect(page.locator('.trash-card')).toHaveCount(24);
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await page.getByRole('button', { name: 'Select all in this project', exact: true }).click();
  await expect(selection).toContainText('55 selected');
  await expect(page.getByRole('button', { name: 'Restore selected (55)', exact: true })).toBeEnabled();
  await expect(page.locator('.trash-card input:checked')).toHaveCount(24);
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.locator('.trash-card input:checked')).toHaveCount(7);
  await scope.selectOption(second.project.id);
  await expect(selection).toContainText('0 selected');
  await page.getByRole('button', { name: 'Select all in this project', exact: true }).click();
  await expect(selection).toContainText('1 selected');
  await scope.selectOption('');
  await page.getByRole('button', { name: 'Select all projects', exact: true }).click();
  await expect(selection).toContainText('56 selected');
  await page.getByRole('button', { name: 'Clear selection', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Restore selected (0)', exact: true })).toBeDisabled();
  await scope.selectOption(first.project.id);
  await page.getByRole('button', { name: 'Select all in this project', exact: true }).click();
  // New discards are excluded until the user explicitly selects them.
  await request.post(`/fixtures/${first.project.id}/trash/${first.library.assets[0].images[0].id}`);
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(selection).toContainText('55 selected');
  await expect(page.getByRole('button', { name: 'Select all in this project', exact: true })).toBeEnabled();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.locator('.trash-toolbar').screenshot({ path: testInfo.outputPath(`trash-bulk-${width}.png`) });
  await page.getByRole('button', { name: 'Restore selected (55)', exact: true }).click();
  await expect(page.locator('.trash-notice')).toContainText('55 media item(s) restored.');
  await expect(page.locator('.trash-card')).toHaveCount(1);
  expect((await state(request, first.project)).assets[0].images).toHaveLength(first.library.assets[0].images.length + 54);
  expect((await state(request, first.project)).trash).toHaveLength(1);
  expect((await state(request, second.project)).trash).toHaveLength(1);
});

test('discard and Undo work during generation; a trashed source remains comparable and restores in place', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  const { project, library } = await fixture(request); const sourceId = library.assets[0].images[0].id;
  await openAssets(page, project);
  await toolsTab(page, 'Prompt');
  await page.getByLabel('Image workflow', { exact: true }).selectOption('Flux2Klein9bKv');
  await page.locator('.asset-media-card[data-media-kind=Image] .media-select').click();
  await toolsTab(page, 'Prompt');
  await page.getByLabel('Edit instruction').fill('SLOW change the coat');
  await imageOutput(page);
  await page.locator('#candidate-count').selectOption('2');
  await page.getByRole('button', { name: 'Generate edited images', exact: true }).click();
  const modal = page.locator('.image-review-dialog');
  await modal.getByRole('button', { name: 'Discard Take 1', exact: true }).click();
  await expect(modal.locator('.review-pending-row')).toHaveCount(1);
  await expect(modal.getByRole('button', { name: 'View Take 1', exact: true })).toHaveCount(0);
  await expect(modal.getByRole('button', { name: 'View Source', exact: true })).toBeFocused();
  expect((await state(request, project)).trash).toHaveLength(1);
  await modal.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(modal.getByRole('button', { name: 'View Take 1', exact: true })).toBeVisible();
  await modal.getByRole('button', { name: 'Close image review' }).click();
  const sourceCard = page.locator('.reference-card').filter({ has: page.locator(`img[src$="/images/${sourceId}"]`) });
  await sourceCard.getByRole('button', { name: 'Image actions', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Move to Trash', exact: true }).click();
  await expect(sourceCard).toHaveCount(0);
  await page.getByRole('button', { name: 'Review latest edit', exact: true }).click();
  await modal.getByRole('button', { name: 'Compare with Source', exact: true }).click();
  await expect(modal.locator('.review-wipe-overlay img')).toHaveAttribute('src', /media\/trash\//);
  await expect(modal.locator('.review-trash-status')).toContainText('In Trash');
  const slider = modal.getByRole('slider', { name: 'Comparison slider' });
  await slider.focus(); await page.keyboard.press('ArrowRight');
  await expect(slider).toHaveValue('51');
  await page.screenshot({ path: 'test-results/trashed-source-desktop.png' });
  await modal.getByRole('button', { name: 'Restore source', exact: true }).click();
  await expect(modal.locator('.review-wipe-overlay img')).toHaveAttribute('src', new RegExp(`/images/${sourceId}$`));
  await expect(slider).toHaveValue('51');
  await expect(modal.getByRole('button', { name: 'Compare with Source', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(modal.getByRole('button', { name: 'View Take 2', exact: true })).toBeVisible({ timeout: 20000 });
  await expect(modal.locator('.review-status')).toHaveCount(0);
  await modal.getByRole('button', { name: 'Discard Take 1', exact: true }).click();
  await modal.getByRole('button', { name: 'Close image review' }).click();
  await page.getByRole('link', { name: 'Trash', exact: true }).click();
  await page.getByRole('heading', { name: 'Trash', exact: true }).waitFor();
  await page.locator('.trash-toolbar select').selectOption(project.id);
  await expect(page.locator('.trash-card')).toHaveCount(1);
  await page.locator('.trash-card').getByRole('button', { name: 'Restore', exact: true }).click();
  await expect(page.locator('.trash-card')).toHaveCount(0);
  expect((await state(request, project)).assets[0].images).toHaveLength(3);
});

test('global Trash previews, filters, restores and permanently deletes exact confirmed selections', async ({ page, request }) => {
  const first = await fixture(request); const second = await fixture(request);
  for (const item of [first, second])
    await request.post(`/fixtures/${item.project.id}/trash/${item.library.assets[0].images[0].id}`);
  await page.goto('/trash');
  await expect(page.getByRole('heading', { name: 'Trash', exact: true })).toBeFocused();
  await page.locator('.trash-toolbar select').selectOption(first.project.id);
  await expect(page.locator('.trash-card')).toHaveCount(1);
  await page.locator('.trash-thumbnail').click();
  await expect(page.locator('.trash-preview')).toBeVisible();
  expect(await page.locator('.trash-preview img').evaluate(i => i.complete && i.naturalWidth > 0)).toBeTruthy();
  await page.keyboard.press('Escape');
  await expect(page.locator('.trash-preview')).toHaveCount(0);
  await expect(page.locator('.trash-thumbnail')).toBeFocused();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: 'test-results/trash-mobile.png' });
  await page.locator('.trash-card').getByRole('checkbox').check();
  await page.getByRole('button', { name: 'Restore selected (1)', exact: true }).click();
  await expect(page.locator('.trash-card')).toHaveCount(0);
  expect((await state(request, first.project)).assets[0].images).toHaveLength(1);
  await page.locator('.trash-toolbar select').selectOption(second.project.id);
  await page.locator('.trash-card').getByRole('button', { name: 'Delete permanently', exact: true }).click();
  const confirm = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Delete media permanently?' }) });
  await expect(confirm).toBeVisible();
  expect((await state(request, second.project)).trash).toHaveLength(1);
  await confirm.getByRole('button', { name: 'Cancel', exact: true }).click();
  // A different project's existing Trash is outside the selected scope.
  await request.post(`/fixtures/${first.project.id}/trash/${first.library.assets[0].images[0].id}`);
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await page.getByRole('button', { name: 'Empty Trash', exact: true }).click();
  const empty = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Empty Trash for this project?' }) });
  await expect(empty).toBeVisible();
  await empty.getByRole('button', { name: 'Delete permanently', exact: true }).click();
  await expect(page.locator('.trash-card')).toHaveCount(0);
  await page.locator('.trash-toolbar select').selectOption('');
  await expect(page.locator('.trash-card')).toHaveCount(1);
  expect((await state(request, first.project)).trash).toHaveLength(1);
  expect((await state(request, second.project)).trash).toHaveLength(0);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({ path: 'test-results/trash-desktop.png' });
});


test('large Trash collections paginate and support keyboard selection with partial project failures', async ({ page, request }) => {
  const first = await fixture(request); const second = await fixture(request);
  await request.post(`/fixtures/${first.project.id}/trash-many`);
  await request.post(`/fixtures/${second.project.id}/trash/${second.library.assets[0].images[0].id}`);
  await page.goto('/trash');
  await expect(page.getByRole('heading', { name: 'Trash', exact: true })).toBeFocused();
  await page.locator('.trash-toolbar select').selectOption(first.project.id);
  await expect(page.locator('.trash-card')).toHaveCount(24);
  await expect(page.locator('.trash-pagination')).toContainText('Page 1 of 3');
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.locator('.trash-pagination')).toContainText('Page 2 of 3');
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.locator('.trash-card')).toHaveCount(7);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({ path: 'test-results/trash-large-desktop.png' });
  await page.locator('.trash-toolbar select').selectOption('');
  const routes = new Map(await Promise.all([first.project.id, second.project.id].map(async id => [id, (await (await request.get(`/fixtures/${id}/project-route`)).json()).slug])));
  const cardFor = id => page.locator('.trash-card').filter({ has: page.locator(`a[href="/projects/${routes.get(id)}/assets"]`) }).first();
  await cardFor(first.project.id).getByRole('checkbox').focus();
  await page.keyboard.press('Space');
  await expect(cardFor(first.project.id).getByRole('checkbox')).toBeChecked();
  await cardFor(second.project.id).getByRole('checkbox').check();
  // Simulate another tab saving to the first project after this page loaded.
  await request.post(`/fixtures/${first.project.id}/touch-assets`);
  await page.getByRole('button', { name: 'Restore selected (2)', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Restore selected (1)', exact: true })).toBeVisible();
  await expect(page.locator('.mud-alert')).toBeVisible();
  expect((await state(request, second.project)).trash).toHaveLength(0);
  expect((await state(request, first.project)).trash).toHaveLength(55);
  await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeFocused();
  await page.getByRole('button', { name: 'Restore selected (1)', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Restore selected (0)', exact: true })).toBeVisible();
  expect((await state(request, first.project)).trash).toHaveLength(54);
});
