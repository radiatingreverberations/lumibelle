import { test, expect } from './fixtures.js';
import { cropReference, openReferenceCrop } from './workspace-tools.js';

test('Assets shares the crop and selection layout while retaining source-only editing and staged changes', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${id}/images?patterned=true`)).json();
  await page.setViewportSize({ width: 1173, height: 1000 });
  await page.goto(`/projects/${id}/assets`);
  await page.locator('.asset-media-card[data-media-kind=Image] .media-select').first().click();
  await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
  const picker = page.locator('.image-input-manager-dialog');
  const donor = library.assets[1];
  await picker.locator(`[data-reference="${donor.id}/${donor.images[0].id}"]`).click();
  await expect(picker.locator('.reference-picker')).toBeVisible();
  await expect(picker.getByRole('button', { name: 'Image 1: Edit area', exact: true })).toBeEnabled();
  await expect(picker.getByRole('button', { name: 'Image 2: Edit area', exact: true })).toBeDisabled();
  await expect(picker.getByRole('button', { name: 'Image 2: Protect areas', exact: true })).toBeEnabled();
  await cropReference(page, picker, 1);
  await expect(picker.locator('.reference-summary').nth(1)).toContainText('Cropped');
  const crop = await openReferenceCrop(page, picker, 1);
  await crop.getByRole('button', { name: 'Full image', exact: true }).click();
  await page.keyboard.press('Escape');
  await expect(crop).toBeHidden();
  await expect(picker.getByRole('button', { name: 'Image 2: Crop image', exact: true })).toBeFocused();
  await expect(picker.locator('.reference-summary').nth(1)).toContainText('Cropped');
  await picker.getByRole('button', { name: 'Move Image 2 up', exact: true }).click();
  await expect(picker.getByRole('button', { name: 'Apply changes', exact: true })).toBeDisabled();
  await picker.getByRole('button', { name: 'Move Image 1 down', exact: true }).click();
  await expect(picker.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
  await page.screenshot({ path: 'artifacts/shared-picker-assets-desktop.png' });
  await page.setViewportSize({ width: 390, height: 844 });
  const browse = picker.getByRole('tab', { name: 'Browse images', exact: true });
  await browse.focus(); await browse.press('End');
  await expect(picker.getByRole('tab', { name: 'Selected references (2)', exact: true })).toBeFocused();
  await expect(picker.getByRole('button', { name: 'Image 2: Crop image', exact: true })).toBeInViewport();
  await expect(picker.getByRole('button', { name: 'Apply changes', exact: true })).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'artifacts/shared-picker-assets-mobile.png' });
  await picker.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.setViewportSize({ width: 1173, height: 1000 });
  await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
  await expect(picker.locator('.reference-selection-row')).toHaveCount(1);
});

for (const [width, height] of [[1010, 650], [390, 700]]) test(`a short window keeps the reference gallery usable by scrolling its column (${width}x${height})`, async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  for (let i = 0; i < 4; i++) await request.post(`/fixtures/${id}/images`);
  await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`);
  await page.setViewportSize({ width, height });
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expect(async () => { if (!await page.locator('.workspace-right').isVisible()) await page.locator('[data-toggle-pane=right]').click({ timeout: 1000 }); }).toPass({ timeout: 10000 });
  await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
  const grid = page.locator('.reference-browser .picker-grid');
  await expect(grid).toBeVisible();
  // The toolbar and filters must not squeeze the gallery to a sliver; the browse column scrolls instead.
  expect(await grid.evaluate(g => g.clientHeight)).toBeGreaterThanOrEqual(150);
  const last = grid.locator(':scope > *').last();
  await last.evaluate(e => e.scrollIntoView({ block: 'end' }));
  const card = await last.boundingBox(); const columns = await page.locator('.manual-reference-columns').boundingBox();
  expect(card.y + card.height).toBeLessThanOrEqual(columns.y + columns.height + 1);
});

test('Shots restores a trashed picture from Manage references and applies it', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`); await request.post(`/fixtures/${id}/prop-reel-owner`); // A prop needs no voice choice.
  const owner = (await (await request.get(`/fixtures/${id}`)).json()).assets.assets[0], picture = owner.images[0];
  await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`);
  await page.setViewportSize({ width: 1173, height: 1000 });
  const open = async () => {
    await page.goto(`/projects/${id}/shots`);
    await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    await expect(async () => { if (!await page.locator('.workspace-right').isVisible()) await page.locator('[data-toggle-pane=right]').click({ timeout: 1000 }); }).toPass({ timeout: 10000 });
    await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
    return page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Manage references', exact: true }) });
  };
  let picker = await open();
  await picker.locator(`[data-reference="${owner.id}/${picture.id}"]`).click();
  await picker.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(picker).not.toBeVisible();
  const images = async () => (await (await request.get(`/fixtures/${id}/production`)).json()).compositions[0].inputs.images;
  await expect.poll(async () => (await images()).map(i => i.mediaId)).toEqual([picture.id]);

  expect((await request.post(`/fixtures/${id}/trash/${picture.id}`)).ok()).toBe(true);
  picker = await open();
  await expect(picker.locator('.reference-trash-restore')).toContainText('In Trash');
  await picker.getByRole('button', { name: 'Restore image', exact: true }).click();
  await expect(picker.locator('.reference-trash-restore')).toHaveCount(0);
  await expect(picker.locator('.reference-issue')).toHaveCount(0);
  await picker.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(picker).not.toBeVisible();
  const assets = (await (await request.get(`/fixtures/${id}`)).json()).assets;
  expect(assets.assets[0].images.map(i => i.id)).toContain(picture.id); expect(assets.trash).toHaveLength(0);
  expect((await images()).map(i => i.mediaId)).toEqual([picture.id]);
});

for (const [width, height] of [[1150, 745], [1400, 1000], [390, 844]]) test(`Manage references keeps its header compact and expanded Assist footer visible (${width}x${height})`, async ({ page, request }, testInfo) => {
  // Measure the final layout, not MudBlazor's opening scale animation.
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`); await request.post(`/fixtures/${id}/prop-reel-owner`); // A prop needs no voice choice.
  const owner = (await (await request.get(`/fixtures/${id}`)).json()).assets.assets[0];
  await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`); await request.post(`/fixtures/${id}/production-shot`);
  // Prepare both shots before narrowing the dialog, without navigating between phone drawers.
  await page.setViewportSize({ width: Math.max(width, 1150), height });
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expect(async () => { if (!await page.locator('.workspace-right').isVisible()) await page.locator('[data-toggle-pane=right]').click({ timeout: 1000 }); }).toPass({ timeout: 10000 });
  const picker = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Manage references', exact: true }) });
  await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
  await picker.locator(`[data-reference="${owner.id}/${owner.images[0].id}"]`).click();
  await picker.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(picker).toBeHidden();
  const nextShot = page.locator('[data-shot-id]').nth(1);
  await nextShot.click();
  await expect(nextShot).toHaveAttribute('aria-pressed', 'true');
  await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
  await expect(picker.getByLabel('Copy references from')).toHaveCount(0);
  await page.setViewportSize({ width, height });
  const box = async selector => picker.locator(selector).first().boundingBox();
  const assist = await box('.asset-pick-assistant'), columns = await box('.manual-reference-columns');
  expect(assist.y).toBeLessThan(columns.y);
  expect((await box('.mud-dialog-title')).height).toBeLessThan(height <= 800 ? 60 : 100);
  expect(columns.height).toBeGreaterThan(width < 650 ? 250 : height <= 800 ? 440 : 600);
  expect((await box('.reference-editor-footer')).y + (await box('.reference-editor-footer')).height).toBeLessThanOrEqual(height);
  await picker.locator('.asset-pick-assistant > summary').click();
  expect((await box('.asset-pick-assistant')).width).toBeGreaterThan(columns.width * .9); // An opened Assist takes the full width.
  const apply = picker.getByRole('button', { name: 'Apply changes', exact: true });
  const cancel = picker.getByRole('button', { name: 'Cancel', exact: true });
  await expect(apply).toBeInViewport({ ratio: .99 });
  await expect(cancel).toBeInViewport({ ratio: .99 });
  const body = picker.locator('.reference-picker-body');
  expect(await body.evaluate(el => el.scrollHeight > el.clientHeight)).toBe(true);
  await picker.screenshot({ path: testInfo.outputPath('expanded-assist-footer.png') });
  await picker.locator('.reference-browser').scrollIntoViewIfNeeded();
  await expect(apply).toBeInViewport({ ratio: .99 });
  await expect(cancel).toBeInViewport({ ratio: .99 });
  await picker.locator(`[data-reference="${owner.id}/${owner.images[0].id}"]`).click();
  await expect(apply).toBeEnabled();
  await apply.click();
  await expect(picker).not.toBeVisible();
});
