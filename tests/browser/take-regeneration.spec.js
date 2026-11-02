import { test, expect } from './fixtures.js';
import { toolsTab, closeShotSetup } from './workspace-tools.js';

for (const narrow of [false, true]) test(`low resolution takes and captured regeneration (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(100000);
  page.setDefaultTimeout(15000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  expect((await request.post(`/fixtures/${id}/take-generation-setup`)).ok()).toBe(true);
  const state = async () => (await request.get(`/fixtures/${id}/shots`)).json();
  const setup = async () => (await (await request.get(`/fixtures/${id}/production`)).json()).compositions[0];
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/shots`);
  await toolsTab(page, 'Generate');
  const resolution = page.getByLabel('Default resolution', { exact: true });
  for (const value of ['detail', 'native', 'preview', 'quick']) {
    await resolution.selectOption(value);
    await expect.poll(async () => { const s = (await setup()).inputs; return s.resolution ?? (s.nativeResolution ? 3 : 1); }).toBe(['quick','preview','detail','native'].indexOf(value));
  }
  await expect(page.getByRole('combobox', { name: 'Default takes', exact: true })).toHaveValue('2');
  await closeShotSetup(page);
  await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
  await expect.poll(async () => (await state()).takes.length, { timeout: 45000 }).toBe(2);
  const original = (await state()).takes.sort((a,b) => a.candidate - b.candidate);
  expect(original.map(t => t.width)).toEqual([608, 608]);
  expect(original.map(t => t.seed)).toEqual([52, 53]);
  const current = await setup();
  await page.goto(`/projects/${id}/shots?view=Takes`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  const closeTools = page.getByRole('button', { name: 'Close Shot tools', exact: true });
  if (await closeTools.isVisible()) await closeTools.click();
  const card = page.locator(`[data-take-id='${original[1].id}']`);
  await expect(card.locator('.take-resolution-badge')).toContainText('0.2 MP');
  await expect(card).toContainText('Seed 53');
  await card.getByRole('button', { name: 'Regenerate…', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Regenerate take', exact: true });
  await expect(dialog.getByLabel('Resolution', { exact: true })).toHaveValue('native');
  await dialog.getByLabel('Seed mode').selectOption('custom');
  await dialog.getByLabel('Custom seed').fill('-1');
  await expect(dialog.getByRole('button', { name: 'Generate take', exact: true })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  expect((await state()).takes).toHaveLength(2);
  await card.getByRole('button', { name: 'Regenerate…', exact: true }).click();
  if (narrow) {
    await dialog.getByLabel('Seed mode').selectOption('custom');
    await dialog.getByLabel('Custom seed').fill('4242');
    await dialog.getByLabel('Resolution', { exact: true }).selectOption('detail');
  }
  // Lossless frames start from the source take's choice and can be changed for the new take.
  const sourceFrames = original[1].snapshot.outputPolicy.saveLosslessFrames;
  await expect(dialog.getByLabel('Save lossless frames', { exact: true })).toBeChecked({ checked: sourceFrames });
  if (narrow) await dialog.getByLabel('Save lossless frames', { exact: true }).setChecked(!sourceFrames);
  const generate = dialog.getByRole('button', { name: 'Generate take', exact: true });
  await generate.focus(); await expect(generate).toBeInViewport();
  await page.screenshot({ path: `artifacts/take-regeneration-${narrow ? 'narrow' : 'desktop'}.png` });
  await generate.press('Enter'); await expect(dialog).not.toBeVisible();
  await expect.poll(async () => (await state()).takes.length, { timeout: 45000 }).toBe(3);
  const result = (await state()).takes.find(t => !original.some(o => o.id === t.id));
  expect(result.seed).toBe(narrow ? 4242 : 53);
  expect([result.width, result.height]).toEqual(narrow ? [1120,640] : [1344,768]);
  expect(result.snapshot.prompt).toBe(original[1].snapshot.prompt);
  expect(result.snapshot.regenerationSource.takeId).toBe(original[1].id);
  expect(result.snapshot.outputPolicy.saveLosslessFrames).toBe(narrow ? !sourceFrames : sourceFrames);
  expect((await setup()).inputs).toEqual(current.inputs);
  await page.reload();
  // Take cards keep generation details, including the seed, folded under Details.
  const resultCard = page.locator(`[data-take-id='${result.id}']`);
  await resultCard.locator('details > summary').filter({ hasText: 'Details' }).click();
  await expect(resultCard).toContainText(narrow ? 'Seed 4242' : 'Seed 53');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
