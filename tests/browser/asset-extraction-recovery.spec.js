import { test, expect } from './fixtures.js';
import { submitExtraction } from './text-assistance-tools.js';

for (const width of [1280, 390]) {
  test(`failed extraction explains scene references and recovers locally at ${width}px`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 1000 });
    const { id } = await (await request.get('/fixtures/new')).json();
    await request.post(`/fixtures/${id}/looks-script?invalidEvidence=true`);
    const source = await (await request.get(`/fixtures/${id}/script-source`)).json();
    const scene = source.blocks.find(b => b.kind.toLowerCase() === 'scene');
    await page.goto(`/projects/${id}/assets`);
    await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    const libraryToggle = page.locator('[data-toggle-pane=left]');
    if (await libraryToggle.isVisible() && await libraryToggle.getAttribute('aria-expanded') === 'false') await libraryToggle.click();
    await expect(page.getByRole('button', { name: 'Extract assets from script', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Extract assets from script', exact: true }).click();
    await submitExtraction(page);
    const dialog = page.locator('.extraction-dialog');
    await expect(dialog.getByRole('heading', { name: 'Asset suggestions need correction', exact: true })).toBeVisible();
    await expect(dialog).toContainText('Asset 1.evidence[1].sceneId');
    await expect(dialog.locator('.apply-extraction')).toHaveCount(0);
    await page.screenshot({ path: `artifacts/assets-extraction-failure-${width}.png` });
    await dialog.getByRole('button', { name: 'Edit JSON', exact: true }).click();
    const editor = dialog.getByLabel('Asset suggestions JSON', { exact: true });
    await editor.fill('[{');
    await dialog.getByRole('button', { name: 'Review corrected JSON', exact: true }).click();
    await expect(dialog.locator('#corrected-extraction-json-error')).toContainText('line 1, column');
    await expect(dialog).toContainText(scene.id);
    await editor.fill(JSON.stringify([{ category: 'prop', name: 'Brass key', description: 'A brass key.', evidence: [{ label: 'Bedroom', sceneId: scene.id, excerpt: 'A brass key' }] }]));
    await page.screenshot({ path: `artifacts/assets-extraction-editor-${width}.png` });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await dialog.getByRole('button', { name: 'Review corrected JSON', exact: true }).click();
    await expect(dialog.locator('.extraction-proposal')).toHaveCount(1);
    await expect(dialog).toContainText('Showing your saved JSON correction');
    const jobs = (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id);
    expect(jobs).toHaveLength(1); expect(jobs[0].state).toBe('Completed');
    expect((await (await request.get(`/fixtures/${id}`)).json()).assets.assets).toHaveLength(0);
    await dialog.getByRole('button', { name: 'Add to library', exact: true }).click();
    await expect(dialog).toBeHidden();
    expect((await (await request.get(`/fixtures/${id}`)).json()).assets.assets[0].name).toBe('Brass key');
  });
}
