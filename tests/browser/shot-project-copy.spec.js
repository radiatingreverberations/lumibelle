import { test, expect } from './fixtures.js';

for (const narrow of [false, true]) test(`copy a shot range with references and script (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }, testInfo) => {
  const source = await (await request.get('/fixtures/new')).json();
  const target = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${target.id}/rename-project?name=Latent%20Motion`);
  for (const fixture of ['approved', 'images', 'shot-copy'])
    expect((await request.post(`/fixtures/${source.id}/${fixture}`)).ok()).toBe(true);
  const original = await (await request.get(`/fixtures/${source.id}/shots`)).json();
  await page.setViewportSize({ width: narrow ? 390 : 1280, height: narrow ? 844 : 960 });
  await page.goto(`/projects/${source.id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  if (narrow) await page.getByRole('button', { name: 'Show Shots', exact: true }).click();
  await page.getByRole('button', { name: 'Bulk operations' }).click();
  await page.getByRole('menuitem', { name: 'Copy to project…', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Copy shots to project', exact: true });
  await dialog.getByLabel('From shot', { exact: true }).fill('2');
  await dialog.getByLabel('Through shot', { exact: true }).fill('3');
  await dialog.getByRole('button', { name: 'Select range', exact: true }).click();
  await expect(dialog.getByRole('checkbox', { name: /^Select shot 1:/ })).not.toBeChecked();
  await expect(dialog.getByRole('checkbox', { name: /^Select shot 2:/ })).toBeChecked();
  await expect(dialog.getByRole('checkbox', { name: /^Select shot 3:/ })).toBeChecked();
  await dialog.getByRole('combobox', { name: 'Destination project', exact: true }).selectOption(target.id);
  const copy = dialog.getByRole('button', { name: 'Copy 2 shots', exact: true });
  await expect(copy).toBeEnabled();
  await page.screenshot({ path: testInfo.outputPath('copy-preview.png') });
  await copy.click();
  await expect(dialog.getByRole('heading', { name: '2 shots copied' })).toBeVisible();
  await expect(dialog.getByRole('alert')).toHaveCount(0);
  await expect(dialog.getByText('Some references or script scenes may already be saved.', {exact:false})).toHaveCount(0);
  const destination = await (await request.get(`/fixtures/${target.id}/shots`)).json();
  expect(destination.shots.map(s => s.title)).toEqual(['Motion study 2', 'Motion study 3']);
  const context = await (await request.get(`/fixtures/${target.id}`)).json();
  expect(context.assets.assets).toHaveLength(1);
  expect(context.script.blocks.some(b => b.kind === 'Scene')).toBe(true);
  const production = await (await request.get(`/fixtures/${target.id}/production`)).json();
  expect(production.shotContent).toHaveLength(2);
  for (const shot of production.shotContent) {
    expect(shot.images).toHaveLength(1);
    expect(shot.images[0].assetId).toBe(context.assets.assets[0].id);
    expect(shot.acceptedRevisionId).toBeNull();
  }
  expect((await (await request.get(`/fixtures/${source.id}/shots`)).json()).shots).toEqual(original.shots);
  expect(await (await request.get('/fixtures/ai-jobs')).json()).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).toBeHidden();
  await page.getByRole('button', { name: 'Bulk operations' }).click();
  await page.getByRole('menuitem', { name: 'Copy to project…', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Copy 0 shots', exact: true })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog).toBeHidden();
  await page.goto(`/projects/${target.id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  if (narrow) await page.getByRole('button', { name: 'Show Shots', exact: true }).click();
  await expect(page.getByRole('button', { name: /^1\. Motion study 2 / })).toBeVisible();
  await expect(page.getByRole('button', { name: /^2\. Motion study 3 / })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath('copied-shots.png') });
});
