import { test, expect } from './fixtures.js';

for (const [size, viewport] of [['desktop', { width: 1173, height: 1272 }], ['mobile', { width: 390, height: 844 }]]) {
  test.describe(size, () => {
  test.use({ hasTouch: size === 'mobile' });
  test(`image card actions float without resizing the grid and selection stays compact on ${size}`, async ({ page, request }) => {
    await page.setViewportSize(viewport);
    const { id } = await (await request.get('/fixtures/new')).json();
    await request.post(`/fixtures/${id}/images`);
    await request.post(`/fixtures/${id}/ux-images?count=3`);
    await page.goto(`/projects/${id}/assets`);
    await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    const cards = page.locator('.reference-card');
    await expect(cards).toHaveCount(4);
    const card = cards.first();
    const trigger = card.getByRole('button', { name: 'Image actions', exact: true });
    const menu = page.getByRole('menu', { name: 'Image actions', exact: true });
    const geometry = () => cards.evaluateAll(items => items.map(el => ({ height: el.offsetHeight, top: el.offsetTop })));
    const before = await geometry();
    await trigger.focus(); await page.keyboard.press('Enter');
    await expect(menu).toBeVisible();
    expect(await geometry()).toEqual(before);
    expect(await menu.evaluate(el => el.closest('.reference-card'))).toBeNull();
    // The menu's arrow-key handling attaches just after it opens; a busy runner can press too early.
    await expect(async () => {
      await menu.getByRole('menuitem', { name: 'Rename', exact: true }).focus();
      await page.keyboard.press('ArrowDown');
      await expect(menu.getByRole('menuitem', { name: 'Edit details', exact: true })).toBeFocused({ timeout: 1000 });
    }).toPass({ timeout: 15000 });
    await page.screenshot({ path: `artifacts/asset-card-menu-${size}.png` });
    await page.keyboard.press('Escape');
    await expect(menu).not.toBeVisible(); await expect(trigger).toBeFocused();
    expect(await geometry()).toEqual(before);

    await trigger.click(); await menu.getByRole('menuitem', { name: 'Rename', exact: true }).click();
    const name = page.locator('.image-review-dialog').getByRole('textbox', { name: 'Name', exact: true });
    await expect(name).toBeFocused();
    await expect(page.locator('.image-review-toolbar')).toHaveCount(0);
    await expect.poll(() => name.evaluate(el => el.selectionEnd - el.selectionStart === el.value.length)).toBe(true);
    const apply = page.locator('.image-review-actions').getByRole('button', { name: 'Save details', exact: true });
    const footer = await apply.boundingBox(); expect(footer.y + footer.height).toBeLessThanOrEqual(viewport.height);
    const guidanceDetails = page.locator('.image-review-inspector .media-details-disclosure').filter({ has: page.getByLabel('Use guidance', { exact: true }) });
    await expect(guidanceDetails).not.toHaveAttribute('open');
    await guidanceDetails.locator('summary').click();
    await expect(page.locator('.image-review-inspector').getByLabel('Use guidance', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Reset details', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Close image review', exact: true }).click();
    await expect(trigger).toBeFocused();

    await trigger.click();
    await page.locator('.assets-header-actions .save-status').click();
    await expect(menu).not.toBeVisible();
    await trigger.click();
    await menu.getByRole('menuitem', { name: 'Move to asset…', exact: true }).click();
    const dialog = page.locator('.image-move-dialog');
    await expect(dialog).toBeVisible(); await expect(menu).not.toBeVisible();
    await expect(dialog.getByLabel('Destination asset')).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(dialog).not.toBeVisible(); await expect(trigger).toBeFocused();

    await expect(card.getByRole('checkbox')).toHaveCount(0);
    await page.getByRole('button',{name:'Select multiple',exact:true}).click();
    const checkbox = card.getByRole('checkbox', { name: 'Select image', exact: true });
    await checkbox.focus(); await page.keyboard.press('Space');
    // In multi-select mode the card's select button toggles the same bulk selection as its checkbox.
    await expect(checkbox).toBeChecked(); await expect(card.locator('.media-select')).toHaveAttribute('aria-pressed','true');
    await expect(page.locator('.look-bulk')).toContainText('1 selected');
    await expect(page.locator('.image-review-dialog')).not.toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `artifacts/asset-card-selection-${size}.png` });
    await page.getByRole('button', { name: 'Clear selection', exact: true }).click();
    await expect(checkbox).not.toBeChecked(); await expect(card).not.toHaveClass(/selected/);
  });
  });
}
