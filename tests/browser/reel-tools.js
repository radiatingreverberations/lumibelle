import { expect } from './fixtures.js';
// The reel tools pane holds references, output (Requested seconds, Aspect) and Generate. Its Prompt dialog holds the name,
// voice, framing and direction, Instructions, Assist and the prompt pair; the preset has its own dialog. See reference-reels.spec.js.
export const reelTools = page => page.locator('.reel-tools');
export const reelSetup = page => page.locator('.reel-setup-dialog');
export async function showToolsPane(page) {
  const toggle = page.locator('[data-toggle-pane=right]');
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expect(async () => {
    if (!await page.locator('.workspace-right').isVisible()) await toggle.click({ timeout: 1000 });
    await expect(page.locator('.workspace-right')).toBeVisible({ timeout: 1000 });
  }).toPass({ timeout: 10000 });
}
export async function openReelSetup(page) {
  const dialog = reelSetup(page), close = dialog.getByRole('button', { name: 'Close reel prompt', exact: true });
  if (await dialog.isVisible() && !await close.isVisible()) await closeReelSetup(page);
  if (!await dialog.isVisible()) { await showToolsPane(page); await reelTools(page).getByRole('button', { name: 'Prompt', exact: true }).click(); }
  await expect(close).toBeVisible();
  await expect(dialog.locator('[data-prompt-ready]')).toHaveAttribute('data-prompt-ready', 'true');
  return dialog;
}
export async function closeReelSetup(page) {
  const dialog = reelSetup(page);
  if (await dialog.isVisible()) { await dialog.getByRole('button', { name: 'Done', exact: true }).click(); await expect(dialog).not.toBeVisible(); }
}
export async function expand(details) {
  if (!await details.evaluate(d => d.open)) await details.locator(':scope > summary').click();
  await expect(details).toHaveAttribute('open');
}
// Opens the Prompt dialog with its framing section (preset, direction, timing and Instructions) expanded.
export async function reelFraming(page) {
  const dialog = await openReelSetup(page);
  await expand(dialog.locator('.reel-direction-options'));
  return dialog;
}
export async function requestSeconds(page, value) {
  await closeReelSetup(page); await showToolsPane(page);
  const seconds = reelTools(page).getByLabel('Requested seconds', { exact: true });
  await seconds.fill(value); await seconds.blur();
}
// On a slow runner the first click can land before the button is ready; retry until the assist panel is open.
export async function openReelAssist(page) {
  const dialog = await openReelSetup(page);
  await expect(async () => {
    if (!await page.locator('.ai-assist-dialog').isVisible()) await dialog.getByRole('button', { name: 'Reel assistance', exact: true }).click({ timeout: 2000 });
    await expect(page.locator('.ai-assist-dialog')).toBeVisible({ timeout: 2000 });
  }).toPass({ timeout: 20000 });
  return page.getByRole('dialog', { name: 'Reel assistance', exact: true });
}
export async function chooseReelReferences(page, references) {
  await showToolsPane(page);
  await reelTools(page).getByRole('button', { name: 'Manage references', exact: true }).click();
  const pictures = page.locator('.reel-pictures-dialog');
  for (const reference of references) await pictures.locator(`[data-reference="${reference}"]`).click();
  return pictures;
}
export async function useVisionModel(page, assist) {
  await assist.locator('.model-chip').click();
  await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await page.getByRole('button', { name: 'Set as project default', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Text model', exact: true })).not.toBeVisible();
}
