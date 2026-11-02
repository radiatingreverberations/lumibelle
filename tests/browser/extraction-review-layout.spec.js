import { extractionComposer, submitExtraction } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

for (const width of [1173, 390]) {
  test(`extraction review separates pending choices from the library at ${width}px`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 1272 });
    const { id } = await (await request.get('/fixtures/new')).json();
    await request.post(`/fixtures/${id}/looks-script?many=true`);
    const state = async () => (await (await request.get(`/fixtures/${id}`)).json()).assets;
    async function openReview(label) {
      const action = page.getByRole('button', { name: label, exact: true });
      if (!await action.isVisible()) await page.locator('[data-toggle-pane=left]').click();
      await action.click();
    }
    await page.goto(`/projects/${id}/assets`);
    await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    await expect(page.locator('.studio-workspace')).toHaveAttribute('aria-busy', 'false');
    await openReview('Extract assets from script');
    const dialog = page.locator('.extraction-dialog');
    await submitExtraction(page);
    await expect(dialog.getByRole('heading', { name: 'Review asset suggestions', exact: true })).toBeVisible();
    await expect(dialog).toContainText('13 assets to add');
    await expect(dialog).toContainText('Not applied');
    await expect(dialog.locator('.extraction-proposal')).toHaveCount(13);
    const character = dialog.locator('.extraction-proposal').first();
    const look = character.locator('.look-proposal').first();
    await look.locator('summary').first().focus();
    await page.keyboard.press('Enter');
    await expect(look).toHaveAttribute('open', '');
    const name = 'Everyday outfit with soft fabric and carefully stitched silver embroidery';
    await look.getByLabel('Look name', { exact: true }).fill(name);
    await look.getByLabel('Look description', { exact: true }).fill('A soft gray hoodie, comfortable trousers and dark shoes.');
    await expect.poll(async () => {
      const jobs = await (await request.get('/fixtures/ai-jobs')).json();
      const job = jobs.find(j => j.target.projectId === id);
      return (await (await request.get(`/fixtures/ai-jobs/${job.id}/review`)).json()).value?.proposals?.[0]?.looks?.[0]?.name;
    }).toBe(name);
    expect((await state()).assets).toHaveLength(0);
    const add = dialog.getByRole('button', { name: 'Add to library', exact: true });
    const assertFooter = async () => {
      const box = await add.boundingBox();
      expect(box.y + box.height).toBeLessThanOrEqual(page.viewportSize().height);
      expect(box.y).toBeGreaterThan(0);
    };
    await assertFooter();
    await page.screenshot({ path: `test-results/extraction-review-${width}.png` });
    await dialog.locator('.extraction-content').evaluate(el => { el.scrollTop = el.scrollHeight; });
    await assertFooter();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await dialog.locator('.extraction-footer').getByRole('button', { name: 'Close', exact: true }).click();
    await expect(dialog).toBeHidden();
    await page.reload();
    await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    await expect(page.locator('.studio-workspace')).toHaveAttribute('aria-busy', 'false');
    await openReview('Review changes');
    await expect(dialog).toContainText('13 assets to add');
    await look.locator('summary').first().click();
    await expect(look.getByLabel('Look name', { exact: true })).toHaveValue(name);
    await add.click();
    await expect(dialog).toBeHidden();
    await expect.poll(async () => (await state()).assets.length).toBe(13);
    expect((await state()).assets[0].looks[0].name).toBe(name);
  });
}
