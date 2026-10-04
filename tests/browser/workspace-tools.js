import { scriptAssist } from './text-assistance-tools.js';
import { expect } from '@playwright/test';

// Use the same visible tabs and drawer controls as an author; never force hidden inputs.
async function showTools(page, studio) {
  const root = page.locator(`.studio-workspace[data-studio="${studio}"]`);
  // After in-app navigation the markup can arrive before its handlers; a click then does nothing.
  if (studio === 'Shots') await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await expect(root).toHaveAttribute('data-ready', 'true');
  await expect(root).not.toHaveAttribute('data-suspended', 'true');
  const toggle = page.locator(`[data-toggle-pane=right][aria-controls="workspace-${studio}-right"]`);
  if (!await root.locator('.workspace-right').isVisible()) {
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await toggle.click();
  }
  return root;
}
export async function imageOutput(page) {
  const root = await showTools(page, 'Assets');
  await expect(root.getByRole('group', { name: 'Image output options', exact: true })).toBeVisible();
}
export async function toolsTab(page, name) {
  if (name === 'Generate') { await openShotSetup(page, 'Generation settings'); return; }
  if (name === 'References') { await closeShotSetup(page); await showTools(page, 'Shots'); return; }
  if (name === 'Assistant') { await scriptAssist(page); return; }
  if (name === 'Requests') { await scriptAssist(page); await page.getByRole('button', { name: 'Requests', exact: true }).click(); return; }
  const studio = ['References', 'Generate'].includes(name) ? 'Shots' : ['Assistant', 'Requests'].includes(name) ? 'Script' : 'Assets';
  const root = await showTools(page, studio);
  if (studio === 'Assets') return;
  const label = ({ Generate: 'Generation' })[name] ?? name;
  const tab = root.locator('[data-workspace-group=tools]').getByRole('tab', { name: label, exact: true });
  if (await tab.getAttribute('aria-selected') !== 'true') await tab.click();
}

export async function openShotSetup(page, tab = 'Prompt') {
  if (tab === 'Generation settings') tab = 'Preset';
  const dialog = page.locator('.shot-setup-dialog');
  // The prompt and the preset are separate dialogs, each opened from its own button.
  const close = dialog.getByRole('button', { name: tab === 'Preset' ? 'Close generation preset' : 'Close prompt', exact: true });
  if (new URL(page.url()).searchParams.get('view') === 'Prompt') await expect(dialog).toBeVisible();
  if (await dialog.isVisible() && !await close.isVisible()) await closeShotSetup(page);
  if (!await dialog.isVisible()) {
    const root = await showTools(page, 'Shots');
    await root.getByRole('button', { name: tab === 'Preset' ? 'Edit generation preset' : tab, exact: true }).click();
  }
  await expect(close).toBeVisible();
  if (tab === 'Prompt') await expect(dialog.locator('[data-prompt-ready]')).toHaveAttribute('data-prompt-ready', 'true');
}

export async function closeShotSetup(page) {
  const dialog = page.locator('.shot-setup-dialog');
  if (await dialog.isVisible()) {
    await dialog.getByRole('button', { name: 'Done', exact: true }).click();
    await expect(dialog).not.toBeVisible();
  }
}

export async function generateTakes(page) {
  await closeShotSetup(page);
  await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
}

export async function shotAction(page, name) {
  await closeShotSetup(page);
  await page.locator('.shot-outline-item.selected').getByRole('button', { name: /^Actions for shot / }).click();
  await page.getByRole('menuitem', { name, exact: true }).click();
}

export async function addImageReference(page, key) {
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  const picker = page.locator('.image-input-manager-dialog');
  await picker.locator(`[data-reference="${key}"]`).click();
  await picker.getByRole('button', { name: 'Apply changes', exact: true }).click();
}
export async function editShotReference(page, row = page.locator('.compact-reference').first()) {
  const id = await row.getAttribute('data-reference-id');
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  const editor = page.locator('.manual-reference-dialog');
  const expand = editor.locator(`[data-reference-id="${id}"] .reference-settings-button`);
  await expect(expand).toBeAttached();
  const selected = editor.getByRole('tab', { name: /^Selected references/ });
  if (await selected.isVisible()) await selected.click();
  await expand.click();
}
export async function assetView(page, name) {
  const details = page.locator('.asset-details-dialog');
  if (await details.isVisible()) {
    if (name === 'Asset details') return;
    await details.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(details).not.toBeVisible();
  }
  const root = page.locator('.studio-workspace[data-studio=Assets]');
  await expect(root).toHaveAttribute('data-ready', 'true');
  const close = root.locator('.workspace-right .workspace-drawer-heading [data-close-pane]');
  if (await close.isVisible()) { await close.click(); await expect(root.locator('.workspace-right')).not.toBeVisible(); }
  if (name === 'Asset details') { await root.getByRole('button', { name: 'Edit asset details', exact: true }).click(); await expect(details).toBeVisible(); return; }
  await root.getByLabel('Filter media', { exact: true }).selectOption(name === 'Reference reels' ? 'Reels' : name);
  if (name === 'Reference reels') { await showTools(page, 'Assets'); const clear = root.getByRole('button', { name: 'Clear selection', exact: true }); if (await clear.isVisible()) await clear.click(); await root.getByLabel('Create media type').selectOption('Reel'); }
}
export async function imageAction(page, action, index = 0) {
  const card = page.locator('.reference-card').nth(index);
  await card.getByRole('button', { name: 'Image actions', exact: true }).click();
  await page.getByRole('menuitem', { name: action, exact: true }).click();
}

// Production inputs are independent; these helpers keep legacy media scenarios focused
// on the composition while reading selected takes from the source shot document.
export async function compositionState(request, id) {
  const [source, document, presets] = await Promise.all([
    request.get(`/fixtures/${id}/shots`), request.get(`/fixtures/${id}/production`), request.get('/fixtures/generation-setups')
  ].map(async response => (await response).json()));
  return { ...source, shots: source.shots.map(shot => {
    const setups = document.compositions.filter(c => c.shotId === shot.id && !c.archived);
    const setup = setups.find(c => c.generationSetupId === presets.selectedId) ?? setups[0];
    // The aspect override belongs to the shot's shared content, not to a setup.
    const aspectOverride = document.shotContent?.find(c => c.shotId === shot.id)?.aspectOverride;
    return setup ? { ...shot, ...setup.inputs, aspectOverride, selectedTakeId: shot.selectedTakeId } : shot;
  }) };
}
export async function composeProduction(page) {
  await openShotSetup(page);
  const prompt = page.getByLabel('H3 prompt', { exact: true });
  const initiallyEmpty = !(await prompt.innerText()).trim();
  await page.getByRole('button', { name: 'Prompt assistance', exact: true }).click();
  const composer = page.locator('.ai-assist-dialog').filter({ has: page.getByLabel('Direction for AI', { exact: true }) });
  await composer.locator('.assist-composer-model .model-chip').click();
  const models = page.locator('.ai-assist-dialog').filter({ has: page.getByRole('heading', { name: 'Text model', exact: true }) });
  await models.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await expect(models).toContainText('For this request only.');
  await models.getByRole('button', { name: 'Done', exact: true }).click();
  await expect(models).toBeHidden();
  await composer.getByRole('button', { name: /^(Compose prompt|Revise with AI)$/ }).click();
  await expect(composer).toBeHidden();
  if (initiallyEmpty) {
    await expect(prompt).not.toBeEmpty();
    return;
  }
  await page.locator('#shot-setup-prompt-panel').getByRole('button', { name: 'Review changes', exact: true }).click();
  const review = page.locator('.prompt-review-dialog');
  await review.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(review).toBeHidden();
  await expect(page.getByLabel('H3 prompt', { exact: true })).not.toBeEmpty();
}

export async function openReferenceCrop(page, manager, index = 0) {
  await manager.locator('.reference-crop-button').nth(index).click();
  const crop = page.locator('.image-input-crop-dialog');
  await expect(crop).toBeVisible();
  return crop;
}

export async function cropReference(page, manager, index = 0, zoom = 2, x = 50, y = 50) {
  const crop = await openReferenceCrop(page, manager, index);
  await crop.getByText('Precise crop controls', { exact: true }).click();
  for (const [label, value] of [['Crop zoom', zoom], ['Crop horizontal position', x], ['Crop vertical position', y]])
    await crop.getByRole('slider', { name: label, exact: true }).evaluate((el, v) => { el.value = String(v); el.dispatchEvent(new Event('change', { bubbles: true })); }, value);
  await crop.getByRole('button', { name: 'Apply crop', exact: true }).click();
  await expect(crop).toBeHidden();
}

export async function cropImageInput(page, index, zoom, x, y) {
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  const manager = page.locator('.image-input-manager-dialog');
  const selected = manager.getByRole('tab', { name: /^Selected references/ });
  if (await selected.isVisible()) await selected.click();
  await cropReference(page, manager, index, zoom, x, y);
  await manager.getByRole('button', { name: 'Apply changes', exact: true }).click();
}
