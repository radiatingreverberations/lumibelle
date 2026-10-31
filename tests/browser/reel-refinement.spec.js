import { test, expect } from './fixtures.js';

const library = async (request, id) => (await (await request.get(`/fixtures/${id}`)).json()).assets;

for (const narrow of [false, true]) test(`prepare a quick reel for refinement and save a separate ${narrow ? 'mobile character rework' : 'desktop environment refinement'}`, async ({ page, request }, testInfo) => {
  test.setTimeout(150000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  if (!narrow) await request.post(`/fixtures/${id}/environment-reel-owner`);
  const owner = (await library(request, id)).assets[0];
  expect((await request.post(`/fixtures/${id}/regeneration-source?quick=true`)).ok()).toBe(true);
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(1);
  const original = (await library(request, id)).reels[0];
  expect(original.refinementPackage).toBeFalsy();
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/assets?assetId=${owner.id}&view=reels`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  if (narrow) {
    await page.getByRole('button', { name: 'Close Asset tools', exact: true }).click();
    await expect(page.locator('.workspace-right')).toBeHidden();
  }
  const refine = async reel => {
    const card = page.locator(`.asset-media-card[data-media-id="${reel.id}"]`);
    await card.getByRole('button', { name: `Actions for ${reel.name}`, exact: true }).click();
    await page.getByRole('menuitem', { name: 'Upscale latents…', exact: true }).click();
    await expect(page.locator('.reel-refinement-dialog')).toBeVisible();
    return page.locator('.reel-refinement-dialog');
  };
  const missing = await refine(original);
  await expect(missing).toContainText('This reel has no saved latents');
  await missing.getByRole('button', { name: 'Regenerate with latents…', exact: true }).click();
  await expect(missing).not.toBeVisible();
  const regenerate = page.getByRole('dialog', { name: 'Regenerate reel', exact: true });
  await expect(regenerate.getByLabel('Use original seed')).toBeChecked();
  await expect(regenerate.getByLabel('Save latents', { exact: true })).toBeChecked();
  await expect(regenerate.getByLabel('Resolution', { exact: true })).toHaveValue('quick');
  await expect(regenerate.getByLabel('Take count', { exact: true })).toHaveValue('1');
  await regenerate.screenshot({ path: testInfo.outputPath('prepare-latents.png') });
  await regenerate.getByRole('button', { name: 'Queue 1 reel', exact: true }).click();
  await expect(regenerate).not.toBeVisible();
  try {
    await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(2);
  } catch (error) {
    const jobs = await (await request.get('/fixtures/ai-jobs')).json();
    throw new Error(`${error.message}\n${JSON.stringify(jobs.filter(j => j.state === 'NeedsAttention'))}`);
  }
  const prepared = (await library(request, id)).reels.find(r => r.id !== original.id);
  expect(prepared.refinementPackage).toBeTruthy();
  expect(prepared.generation.seed).toBe(original.generation.seed);
  expect([prepared.media.width, prepared.media.height]).toEqual([original.media.width, original.media.height]);
  await expect(page.locator(`.asset-media-card[data-media-id="${prepared.id}"]`)).toContainText('Ready to refine');
  const dialog = await refine(prepared);
  const mode = narrow ? 'Rework' : 'Refine';
  await expect(dialog.getByRole('button', { name: 'Queue Refine', exact: true })).toBeEnabled();
  await dialog.getByLabel('Mode', { exact: true }).selectOption(mode);
  await expect(dialog.getByLabel('Output size', { exact: true }).locator('option:checked')).toContainText('Native');
  expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  await dialog.screenshot({ path: testInfo.outputPath('refine-reel.png') });
  await dialog.getByRole('button', { name: `Queue ${mode}`, exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 60000 }).toBe(3);
  const saved = await library(request, id);
  const version = saved.reels.find(r => r.refinement?.parentTakeId === prepared.id);
  expect(version).toBeTruthy();
  expect(version.refinementPackage.width).toBeGreaterThan(prepared.refinementPackage.width);
  expect(version.media.id).not.toBe(prepared.media.id);
  expect(version.keyframes).toBeFalsy();
  expect(saved.reels.find(r => r.id === original.id)).toEqual(original);
  expect(saved.reels.find(r => r.id === prepared.id)).toEqual(prepared);
});
