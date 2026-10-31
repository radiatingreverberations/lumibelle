import { test, expect } from './fixtures.js';

const library = async (request, id) => (await (await request.get(`/fixtures/${id}`)).json()).assets;

for (const narrow of [false, true]) test(`promote a reel with editable video guidance (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }, testInfo) => {
  test.setTimeout(120000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  await request.post(`/fixtures/${id}/environment-reel-owner`);
  const owner = (await library(request, id)).assets[0];
  expect((await request.post(`/fixtures/${id}/regeneration-source?quick=true`)).ok()).toBe(true);
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(1);
  const source = (await library(request, id)).reels[0];
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/assets?assetId=${owner.id}&view=reels`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  const action = async (reel, label) => {
    // Dialog focus restoration can reopen the selected item's tools drawer after cancel.
    await expect(async () => {
      if (narrow && await page.locator('.workspace-right').isVisible()) {
        await page.getByRole('button', { name: 'Close Asset tools', exact: true }).click({ timeout: 1000 });
        await expect(page.locator('.workspace-right')).not.toBeVisible({ timeout: 1000 });
      }
      if (!await page.getByRole('menuitem', { name: label, exact: true }).isVisible())
        await page.locator(`.asset-media-card[data-media-id="${reel.id}"]`).getByRole('button', { name: `Actions for ${reel.name}`, exact: true }).click({ timeout: 1000 });
      await expect(page.getByRole('menuitem', { name: label, exact: true })).toBeVisible({ timeout: 1000 });
    }).toPass({ timeout: 10000 });
    await page.getByRole('menuitem', { name: label, exact: true }).click();
  };
  await action(source, 'Refine from video…');
  const dialog = page.getByRole('dialog', { name: 'Refine from video', exact: true });
  await expect(dialog.getByLabel('Shared output resolution')).toHaveValue('default');
  await expect(dialog.locator('.refine-facts')).toContainText('→ Detail · 0.7 MP');
  await expect(dialog.getByLabel(/^Output resolution for /).locator('option')).toContainText(['Shared setting', 'Quick', 'Preview', 'Detail', 'Native', '1080p-class', '1440p-class']);
  await expect(dialog.getByLabel('Save lossless frames', { exact: true })).toBeChecked();
  await expect(dialog.getByLabel(/^Base prompt for /)).toHaveValue(source.generation.recipe.prompt.replaceAll('\r\n', '\n'));
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await library(request, id)).reels).toHaveLength(1);
  // Video-guided promotion is a separate action and does not require saved latents.
  await action(source, 'Refine from video…');
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Reel name', { exact: true }).fill('Detailed environment final');
  await dialog.getByText('Reel use guidance', { exact: true }).click();
  await dialog.getByLabel('Use guidance', { exact: true }).fill('Use selected final frames for the room layout and fine set details.');
  await dialog.getByLabel('Motion-reference instructions', { exact: true }).fill('Keep the camera route and held views.');
  await dialog.getByRole('button', { name: 'Manage references', exact: true }).click();
  const references = page.getByRole('dialog', { name: 'Manage references', exact: true });
  if (narrow) await references.getByRole('tab', { name: 'Selected references (2)', exact: true }).click();
  await references.getByRole('button', { name: 'Video 1: Reference settings', exact: true }).click();
  await expect(references.getByLabel('Use audio', { exact: true })).toBeDisabled();
  await expect(references.getByLabel('Use audio', { exact: true })).not.toBeChecked();
  await references.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(references).not.toBeVisible();
  await dialog.getByText('Submitted prompt preview', { exact: true }).click();
  await expect(dialog.locator('.refine-preview')).toContainText('Motion reference <Video 1>: Keep the camera route and held views.');
  await expect(dialog.getByRole('button', { name: 'Queue refinement', exact: true })).toBeEnabled();
  expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  await dialog.screenshot({ path: testInfo.outputPath('reel-motion-reference.png') });
  await dialog.getByRole('button', { name: 'Queue refinement', exact: true }).click();
  // Preparation continues in the background after the dialog closes.
  await expect(page.getByRole('button', { name: 'View batch', exact: true })).toBeVisible();
  await expect(dialog).not.toBeVisible();
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 60000 }).toBe(2);
  const saved = await library(request, id);
  const final = saved.reels.find(r => r.id !== source.id);
  expect(saved.reels.find(r => r.id === source.id)).toEqual(source);
  expect(final.name).toBe('Detailed environment final');
  expect(final.useGuidance).toBe('Use selected final frames for the room layout and fine set details.');
  expect([final.media.width, final.media.height]).toEqual([1120, 640]);
  expect(final.media.id).not.toBe(source.media.id);
  expect(final.generation.seed).not.toBe(source.generation.seed);
  expect(final.generation.snapshot.motionReference.reelId).toBe(source.id);
  expect(final.generation.snapshot.motionReference.media.id).not.toBe(source.media.id);
  expect(final.generation.snapshot.shot.generationPreset).toBe('standard');
  expect(final.generation.snapshot.shot.videos[0].useSoundtrack).toBe(false);
  expect(final.generation.snapshot.outputPolicy.saveLosslessFrames).toBe(true);
  expect(final.refinement).toBeFalsy();
  await page.reload();
  await expect(page.locator(`.asset-media-card[data-media-id="${final.id}"]`)).toBeVisible();
  await action(final, 'Refine from video…');
  await expect(dialog.getByLabel(/^Output resolution for /).locator('option')).toContainText(['Shared setting', 'Detail', 'Native', '1080p-class', '1440p-class']);
  await expect(dialog.getByLabel(/^Base prompt for /)).toHaveValue(source.generation.recipe.prompt.replaceAll('\r\n', '\n'));
  await expect(dialog).toContainText('1 videos');
  await dialog.getByText('Submitted prompt preview', { exact: true }).click();
  expect((await dialog.locator('.refine-preview').innerText()).split('Motion reference <Video 1>:')).toHaveLength(2);
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
});
