import { test, expect } from './fixtures.js';

async function openDeletion(page) {
  await page.getByRole('button', { name: 'Asset library options', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Delete all assets…', exact: true }).click();
}

async function setup(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${id}/images`)).json();
  await page.goto(`/projects/${id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('h1')).toBeFocused();
  await expect(page.locator('.asset-list-row.selected')).toHaveAttribute('data-asset-id', library.assets[0].id);
  return { id, library, state: async () => (await (await request.get(`/fixtures/${id}`)).json()).assets };
}

test('library extraction and confirmed deletion include filtered assets, survive conflicts and restore through Trash', async ({ page, request }) => {
  await page.setViewportSize({ width: 1173, height: 1272 });
  const { id, library, state } = await setup(page, request);
  const rail = page.locator('.asset-library');
  const extract = rail.getByRole('button', { name: 'Extract assets from script', exact: true });
  await expect(extract).toBeVisible();
  await expect(page.locator('.assets-header').getByRole('button', { name: 'Extract assets from script', exact: true })).toHaveCount(0);
  await page.getByLabel('Search assets', { exact: true }).fill(library.assets[0].name);
  await expect(rail.locator('.asset-choice')).toHaveCount(1);
  await openDeletion(page);
  const dialog = page.locator('.asset-delete-all-dialog');
  await expect(dialog.locator('.asset-delete-list li')).toHaveCount(library.assets.length);
  await expect(dialog).toContainText('30 days');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await state()).assets).toHaveLength(library.assets.length);
  await openDeletion(page);
  await request.post(`/fixtures/${id}/touch-assets`);
  await dialog.getByRole('button', { name: 'Delete all assets', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Nothing was deleted');
  await expect(dialog.getByRole('button', { name: 'Delete all assets', exact: true })).toBeDisabled();
  expect((await state()).assets).toHaveLength(library.assets.length);
  await dialog.getByRole('button', { name: 'Review latest assets', exact: true }).click();
  await page.screenshot({ path: 'test-results/assets-delete-desktop.png' });
  await dialog.getByRole('button', { name: 'Delete all assets', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(rail.getByRole('button', { name: 'Create asset', exact: true })).toBeFocused();
  await expect.poll(async () => (await state()).assets.length).toBe(0);
  expect((await state()).trash).toHaveLength(2);
  const { slug } = await (await request.get(`/fixtures/${id}/project-route`)).json();
  await page.goto('/trash');
  const cards = page.locator('.trash-card').filter({ has: page.locator(`a[href="/projects/${slug}/assets"]`) });
  await expect(cards).toHaveCount(2);
  await cards.first().getByRole('button', { name: 'Restore', exact: true }).click();
  await expect.poll(async () => (await state()).assets.length).toBe(1);
  await page.goto(`/projects/${id}/assets`);
  await expect(rail.locator('.asset-choice')).toHaveCount(1);
  await page.screenshot({ path: 'test-results/assets-library-desktop.png' });
});

test('mobile library actions use one modal surface and restore keyboard focus', async ({ page, request }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const { library, state } = await setup(page, request);
  const workspace = page.locator('.studio-workspace');
  const rail = page.locator('.asset-library');
  const open = page.locator('[data-toggle-pane="left"]');
  await open.click();
  await expect(workspace).toHaveAttribute('data-drawer', 'left');
  const extract = rail.getByRole('button', { name: 'Extract assets from script', exact: true });
  await extract.click();
  const extraction = page.locator('.extraction-dialog');
  await expect(extraction).toBeVisible();
  await expect(workspace).toHaveAttribute('data-suspended', 'true');
  await expect.poll(() => extraction.evaluate(el => el.contains(document.activeElement))).toBe(true);
  await page.keyboard.press('Escape');
  await expect(extraction).toBeHidden();
  await expect(extract).toBeFocused();
  const options = rail.getByRole('button', { name: 'Asset library options', exact: true });
  await openDeletion(page);
  const dialog = page.locator('.asset-delete-all-dialog');
  await expect(dialog).toBeVisible();
  await expect(workspace).toHaveAttribute('data-suspended', 'true');
  await dialog.getByRole('button', { name: 'Delete all assets', exact: true }).focus();
  await page.keyboard.press('Tab');
  expect(await dialog.evaluate(el => el.contains(document.activeElement))).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'test-results/assets-delete-mobile.png' });
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(options).toBeFocused();
  expect((await state()).assets).toHaveLength(library.assets.length);
  await openDeletion(page);
  await dialog.getByRole('button', { name: 'Delete all assets', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(open).toBeFocused();
  expect((await state()).assets).toHaveLength(0);
});
