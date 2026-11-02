import { test, expect } from './fixtures.js';
import { toolsTab, assetView } from './workspace-tools.js';
import { modelOptions } from './text-assistance-tools.js';

async function visibleHelp(page) {
  const popup = page.locator('.help-hint-content:popover-open');
  await expect(popup).toBeVisible();
  const box = await popup.boundingBox(), viewport = page.viewportSize();
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.y).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(viewport.width);
  expect(box.y + box.height).toBeLessThanOrEqual(viewport.height);
  const id = await popup.getAttribute('id');
  const trigger = await page.locator(`[popovertarget="${id}"]`).boundingBox();
  const gapX = Math.max(0, box.x - (trigger.x + trigger.width), trigger.x - (box.x + box.width));
  const gapY = Math.max(0, box.y - (trigger.y + trigger.height), trigger.y - (box.y + box.height));
  expect(Math.hypot(gapX, gapY)).toBeLessThan(20);
  return popup;
}

test('studio help stays compact and model selection explains its current scope', async ({ page, request }) => {
  const project = await (await request.get('/fixtures/new')).json();
  await page.setViewportSize({ width: 1173, height: 900 });
  await page.goto(`/projects/${project.id}/script`);
  await toolsTab(page, 'Assistant');
  const picker = await modelOptions(page);
  await expect(picker).toContainText('The project follows the global default.');
  await picker.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await expect(picker).toContainText('For this request only.');
  await picker.getByRole('button', { name: 'Done', exact: true }).click();
  await expect(picker).not.toBeVisible();
  const help = page.getByRole('button', { name: 'Help: Outline', exact: true });
  const heading = page.locator('.script-outline .pane-heading');
  const rowHeight = await help.evaluate(el => el.getBoundingClientRect().height);
  const headingHeight = await heading.evaluate(el => el.getBoundingClientRect().height);
  expect(rowHeight).toBeLessThanOrEqual(headingHeight);
  await page.keyboard.press('Tab');
  await help.focus();
  await expect(await visibleHelp(page)).toContainText('Add after the selected item.');
  await page.keyboard.press('Escape');
  await expect(page.locator('.help-hint-content:popover-open')).toHaveCount(0);
  await expect(help).toBeFocused();
  await help.click(); await visibleHelp(page);
  await page.screenshot({ path: 'artifacts/validation/help-hints-desktop.png' });
  await page.locator('.script-outline .pane-title').click();
  await expect(page.locator('.help-hint-content:popover-open')).toHaveCount(0);
});

test('hover help remains readable under the pointer and can be pinned or dismissed', async ({ page, request }) => {
  const project = await (await request.get('/fixtures/new')).json();
  await page.setViewportSize({ width: 1173, height: 900 });
  await page.goto(`/projects/${project.id}/script`);
  await toolsTab(page, 'Assistant');
  const help = page.getByRole('button', { name: 'Help: Outline', exact: true });
  await help.hover();
  const popup = await visibleHelp(page);
  await popup.hover();
  // Moving from the badge into the explanation must not dismiss it midway through reading.
  await page.waitForTimeout(350);
  await expect(popup).toBeVisible();
  await page.getByLabel('Instructions', { exact: true }).hover();
  await expect(page.locator('.help-hint-content:popover-open')).toHaveCount(0);
  await help.hover(); await visibleHelp(page);
  await help.click();
  await page.getByLabel('Instructions', { exact: true }).hover();
  await page.waitForTimeout(350);
  await visibleHelp(page);
  await page.keyboard.press('Escape');
  await expect(page.locator('.help-hint-content:popover-open')).toHaveCount(0);
  await page.getByLabel('Instructions', { exact: true }).focus();
  await help.focus(); await visibleHelp(page);
  await page.keyboard.press('Tab');
  await expect(page.locator('.help-hint-content:popover-open')).toHaveCount(0);
});

test.describe('touch help', () => {
  test.use({ hasTouch: true, viewport: { width: 390, height: 844 } });
  test('help dismissal keeps its drawer open and retains input focus', async ({ page, request }) => {
    const project = await (await request.get('/fixtures/new')).json();
    const library = await (await request.post(`/fixtures/${project.id}/images`)).json();
    await page.goto(`/projects/${project.id}/script`);
    await toolsTab(page, 'Assistant');
    await page.locator('.workspace-right [data-close-pane]').click();
    await page.getByRole('button', { name: 'Show Outline', exact: true }).click();
    const drawer = page.getByRole('dialog', { name: 'Outline', exact: true });
    const help = drawer.getByRole('button', { name: 'Help: Outline', exact: true });
    await help.tap(); await visibleHelp(page);
    await page.screenshot({ path: 'artifacts/validation/help-hints-mobile.png' });
    await page.keyboard.press('Escape');
    await expect(page.locator('.help-hint-content:popover-open')).toHaveCount(0);
    await expect(drawer).toBeVisible();
    await expect(help).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(drawer).not.toBeVisible();
  });
  test('look help keeps the nested details modal and look editor open', async ({ page, request }) => {
    const project = await (await request.get('/fixtures/new')).json();
    const library = await (await request.post(`/fixtures/${project.id}/images`)).json();
    await page.goto(`/projects/${project.id}/assets?assetId=${library.assets[0].id}`);
    await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    await assetView(page, 'Asset details');
    await page.getByRole('button', { name: 'Add look', exact: true }).tap();
    const dialog = page.locator('.look-editor-dialog');
    await dialog.locator('.look-preservation summary').tap();
    const lookHelp = dialog.getByRole('button', { name: 'Help: Look preservation', exact: true });
    await lookHelp.tap();
    await expect(await visibleHelp(page)).toContainText('Clothing and styling');
    await page.keyboard.press('Escape');
    await expect(page.locator('.help-hint-content:popover-open')).toHaveCount(0);
    await expect(dialog).toBeVisible();
    await expect(lookHelp).toBeFocused();
    await dialog.getByLabel('This look', { exact: true }).fill('Keep the jacket.');
    await lookHelp.tap(); await lookHelp.tap();
    await expect(page.locator('.help-hint-content:popover-open')).toHaveCount(0);
    await expect(dialog.getByLabel('This look', { exact: true })).toHaveValue('Keep the jacket.');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).tap();
  });
});
