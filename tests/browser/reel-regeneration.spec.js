import { test, expect } from './fixtures.js';
const library = async (request, id) => (await (await request.get(`/fixtures/${id}`)).json()).assets;
for (const narrow of [false, true]) test(`regenerate reel with captured seed (${narrow ? 'narrow character' : 'desktop environment'})`, async ({ page, request }) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  if (!narrow) await request.post(`/fixtures/${id}/environment-reel-owner`);
  const owner = (await library(request, id)).assets[0];
  expect((await request.post(`/fixtures/${id}/regeneration-source`)).ok()).toBe(true);
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(1);
  const source = (await library(request, id)).reels[0];
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/assets?assetId=${owner.id}&view=reels`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  if (narrow) {
    await expect(page.locator('.workspace-right')).toBeVisible();
    await page.getByRole('button', { name: 'Close Asset tools', exact: true }).click();
    await expect(page.locator('.workspace-right')).toBeHidden();
  }
  const card = page.locator('.asset-media-card[data-media-kind=Reel]').first();
  await expect(card.locator('.media-resolution-badge')).toHaveAttribute('title', `${source.media.width} × ${source.media.height}`);
  await card.getByRole('button', { name: `Actions for ${source.name}`, exact: true }).click();
  await page.getByRole('menuitem', { name: 'Regenerate…', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Regenerate reel', exact: true });
  await expect(dialog.getByLabel('Resolution', { exact: true })).toHaveValue(source.generation.recipe.resolution?.toLowerCase() ?? (source.generation.recipe.nativeResolution ? 'native' : 'preview'));
  await expect(dialog.getByLabel('Resolution', { exact: true }).locator('option')).toHaveCount(4);
  await expect(dialog.getByLabel('Use original seed')).not.toBeChecked();
  await dialog.getByLabel('Take count').selectOption('3');
  await dialog.getByLabel('Use original seed').check();
  await expect(dialog.getByLabel('Take count')).toHaveValue('1');
  await expect(dialog.getByLabel('Take count')).toBeDisabled();
  await dialog.getByLabel('Use original seed').uncheck();
  await expect(dialog.getByLabel('Take count')).toHaveValue('3');
  await dialog.getByLabel('Resolution', { exact: true }).selectOption('preview');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).focus();
  await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeInViewport();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).press('Enter');
  await expect(dialog).not.toBeVisible();
  expect((await library(request, id)).reels).toHaveLength(1);
  // Reopen from the saved reel's details footer, including keyboard submission.
  await card.getByRole('button', { name: `Actions for ${source.name}`, exact: true }).click();
  await page.getByRole('menuitem', { name: 'Edit details', exact: true }).click();
  const details = page.getByRole('dialog', { name: 'Reel details', exact: true });
  await details.getByRole('button', { name: 'Regenerate…', exact: true }).click();
  await expect(dialog.getByLabel('Resolution', { exact: true })).toHaveValue(source.generation.recipe.resolution?.toLowerCase() ?? (source.generation.recipe.nativeResolution ? 'native' : 'preview'));
  await dialog.getByLabel('Use original seed').check();
  await dialog.getByLabel('Resolution', { exact: true }).selectOption(narrow ? 'detail' : 'native');
  // Lossless frames start from the source reel's choice and can be changed for the new reels.
  const sourceFrames = source.generation.snapshot.outputPolicy.saveLosslessFrames;
  await expect(dialog.getByLabel('Keep lossless frames', { exact: true })).toBeChecked({ checked: sourceFrames });
  if (narrow) await dialog.getByLabel('Keep lossless frames', { exact: true }).setChecked(!sourceFrames);
  await page.screenshot({ animations: 'disabled', path: `artifacts/reel-regeneration-${narrow ? 'narrow' : 'desktop'}.png` });
  const generate = dialog.getByRole('button', { name: 'Queue 1 reel', exact: true });
  await generate.focus(); await expect(generate).toBeInViewport(); await generate.press('Enter');
  await expect(dialog).not.toBeVisible();
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(2);
  const result = (await library(request, id)).reels.find(r => r.id !== source.id);
  expect(result.generation.seed).toBe(source.generation.seed);
  expect(result.generation.recipe.nativeResolution).toBe(!narrow);
  expect(result.generation.snapshot.width).toBe(narrow ? 832 : 1344);
  expect(result.generation.snapshot.height).toBe(narrow ? 832 : 768);
  expect(result.generation.snapshot.prompt).toBe(source.generation.snapshot.prompt);
  expect(result.generation.snapshot.reel.regenerationSource.reelId).toBe(source.id);
  expect(result.generation.snapshot.outputPolicy.saveLosslessFrames).toBe(narrow ? !sourceFrames : sourceFrames);
  expect((await (await request.get('/fixtures/reel-compositions')).json()).filter(c => c.context.environment?.assetId === owner.id || c.context.character?.assetId === owner.id)).toHaveLength(0);
  await details.getByRole('button', { name: 'Close', exact: true }).click();
  await page.reload();
  await expect(page.locator('.asset-media-card[data-media-kind=Reel]')).toHaveCount(2);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  if (narrow) {
    // view=reels opens Asset tools after the page is ready, so wait for it rather than checking once.
    await expect(page.locator('.workspace-right')).toBeVisible();
    await page.getByRole('button', { name: 'Close Asset tools', exact: true }).click();
    await expect(page.locator('.workspace-right')).toBeHidden();
  }
  const resultCard = page.locator(`.asset-media-card[data-media-id='${result.id}']`);
  await expect(resultCard.locator('.media-resolution-badge')).toBeVisible();
  await expect(resultCard.locator('.media-resolution-badge')).toHaveAttribute('title', `${result.media.width} × ${result.media.height}`);
  await expect(resultCard.locator('.media-resolution-badge')).toContainText(narrow ? '0.7 MP' : 'Native · 1.0 MP');
  await resultCard.scrollIntoViewIfNeeded();
  await page.screenshot({ animations: 'disabled', path: `artifacts/reel-resolution-badges-${narrow ? 'narrow' : 'desktop'}.png` });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
