import { test, expect } from './fixtures.js';
import { toolsTab } from './workspace-tools.js';

for (const studio of ['Assets', 'Shots']) {
  test(`${studio} reference picker stacks thumbnails and readable labels on desktop and mobile`, async ({ page, request }) => {
    await page.setViewportSize({ width: 1173, height: 1272 });
    const { id } = await (await request.get('/fixtures/new')).json();
    await request.post(`/fixtures/${id}/images`);
    await request.post(`/fixtures/${id}/ux-images?count=12`);
    if (studio === 'Shots') {
      await request.post(`/fixtures/${id}/approved`);
      await request.post(`/fixtures/${id}/reference-workspace?silent=true`);
    }
    await page.goto(`/projects/${id}/${studio.toLowerCase()}`);
    if (studio === 'Assets') {
      await toolsTab(page, 'Prompt');
      await page.getByLabel('Image workflow', { exact: true }).selectOption('Flux2Klein9bKv');
      await page.locator('.asset-media-card[data-media-kind=Image] .media-select').first().click();
      await toolsTab(page, 'Inputs');
    } else await toolsTab(page, 'References');
    const trigger = page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true });
    await trigger.click();
    const picker = page.getByRole('region', { name: studio === 'Assets' ? 'Project image picker' : 'Project reference picker', exact: true });
    const apply = page.getByRole('button', { name: 'Apply changes', exact: true });
    const cards = picker.locator('.picker-grid > button');
    await expect(cards).toHaveCount(14);
    for (const [width, height] of [[1173, 1272], [390, 844], [587, 636]]) {
      await page.setViewportSize({ width, height });
      // Test actual rendered geometry so a shared button reset cannot silently turn cards into rows.
      for (const card of await cards.evaluateAll(items => items.map(el => {
        const image = el.querySelector('img').getBoundingClientRect();
        const title = el.querySelector('strong').getBoundingClientRect();
        const metadata = el.querySelector('small').getBoundingClientRect();
        return { imageHeight: image.height, imageBottom: image.bottom, imageWidth: image.width,
          titleTop: title.top, titleBottom: title.bottom, titleWidth: title.width,
          metadataTop: metadata.top, metadataWidth: metadata.width, overflow: el.scrollWidth > el.clientWidth };
      }))) {
        // Rendered sizes: the dialog may still be scaling in. Square previews, with a padded caption below that stays readable and inside the card.
        expect(Math.abs(card.imageHeight - card.imageWidth)).toBeLessThan(1.5);
        expect(card.titleTop).toBeGreaterThanOrEqual(card.imageBottom);
        expect(card.metadataTop).toBeGreaterThanOrEqual(card.titleBottom);
        for (const width of [card.titleWidth, card.metadataWidth]) { expect(width).toBeLessThanOrEqual(card.imageWidth); expect(width).toBeGreaterThan(card.imageWidth - 30); }
        expect(card.overflow).toBe(false);
      }
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await apply.scrollIntoViewIfNeeded();
      await expect(apply).toBeInViewport();
      await page.screenshot({ path: `artifacts/reference-picker-${studio.toLowerCase()}-${width}.png` });
    }
    const choice = picker.locator('.picker-grid > button:not([disabled])').first();
    const chosenKey = await choice.getAttribute('data-reference');
    await choice.focus(); await choice.press('Enter');
    await expect(picker.locator(`[data-reference="${chosenKey}"]`)).toBeDisabled();
    await expect(apply).toBeEnabled();
    // Restore the original layout so the opener is visible when the dialog returns focus.
    await page.setViewportSize({ width: 1173, height: 1272 });
    await expect.poll(() => trigger.evaluate(el => !!el.closest('[inert]'))).toBe(false);
    await page.keyboard.press('Escape');
    await expect(picker).not.toBeVisible();
    await expect(trigger).toBeFocused();
  });
}
