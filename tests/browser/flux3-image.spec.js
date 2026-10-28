import { test, expect } from './fixtures.js';
import { toolsTab } from './workspace-tools.js';

for (const width of [1280, 390]) {
  test(`FLUX 3 variant and independent files survive save at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 960 });
    await page.goto('/settings/ai?tab=images');
    await expect(page.locator('h1')).toBeFocused();
    await page.locator('#ai-image-Flux3ImageKlein').click();
    await expect(page.locator('#flux3-steps')).toHaveValue('16');
    await expect(page.locator('#flux3-guidance')).toHaveCount(0);
    await expect(page.locator('#comfy-image-edit-lora')).toHaveCount(0);
    await page.getByRole('button', { name: 'Refresh image models', exact: true }).click();
    await page.locator('#comfy-image-model').selectOption('flux3-image-klein-base-int8-convrot.safetensors');
    await page.locator('#flux3-variant').selectOption('Base');
    await expect(page.locator('#flux3-steps')).toHaveValue('50');
    await expect(page.locator('#flux3-guidance')).toHaveValue('3.5');
    await page.locator('#flux3-reference-size').fill('0.5');
    await page.getByRole('button', { name: 'Save FLUX 3 Image Klein', exact: true }).click();
    await expect(page.getByText('FLUX 3 Image Klein saved.', { exact: true })).toBeVisible();
    await page.locator('#ai-image-Krea2').click();
    await expect(page.locator('#comfy-image-edit-lora')).toHaveValue('krea2_identity_edit_v1_2.safetensors');
    await page.locator('#ai-image-Flux2Klein9bKv').click();
    await expect(page.locator('#comfy-image-model')).toHaveValue('flux-2-klein-9b-kv-fp8.safetensors');
    await page.reload(); await expect(page.locator('h1')).toBeFocused();
    await page.locator('#ai-image-Flux3ImageKlein').click();
    await expect(page.locator('#flux3-variant')).toHaveValue('Base');
    await expect(page.locator('#flux3-reference-size')).toHaveValue('0.5');
    await expect(page.locator('#comfy-image-model')).toHaveValue('flux3-image-klein-base-int8-convrot.safetensors');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    await page.screenshot({ path: `test-results/flux3-settings-${width}.png`, fullPage: true });
  });
}

test('FLUX 3 has a separate 4K canvas and caption guidance in the existing Assets tools', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  const project = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${project.id}/images`);
  await page.goto(`/projects/${project.id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await toolsTab(page, 'Prompt');
  const workflow = page.getByLabel('Image workflow', { exact: true });
  await workflow.selectOption('Flux3ImageKlein');
  await expect(page.locator('.flux3-caption-hint')).toBeVisible();
  const resolution = page.getByLabel('Resolution', { exact: true });
  await resolution.selectOption('4096');
  await expect(page.locator('.image-resolution-options')).toContainText('4096 × 4096');
  await workflow.selectOption('Krea2');
  await expect(resolution).toHaveValue('1024');
  await expect(resolution.locator('option[value="4096"]')).toHaveCount(0);
  await workflow.selectOption('Flux3ImageKlein');
  await expect(resolution).toHaveValue('4096');
  await page.setViewportSize({ width: 390, height: 960 });
  await toolsTab(page, 'Prompt');
  await expect(page.locator('.flux3-caption-hint')).toBeVisible();
  const resolutionBox = await resolution.boundingBox();
  const outputBox = await page.locator('.image-resolution-options').boundingBox();
  expect(resolutionBox.width).toBeGreaterThan(120);
  expect(resolutionBox.x + resolutionBox.width).toBeLessThanOrEqual(outputBox.x + outputBox.width + 1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
  await page.screenshot({ path: 'test-results/flux3-assets-mobile.png', fullPage: true });
});
