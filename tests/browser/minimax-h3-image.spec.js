import { test, expect } from './fixtures.js';
import { toolsTab } from './workspace-tools.js';

for (const width of [1280, 390]) {
  test(`H3 Image files survive save at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 960 });
    await page.goto('/settings/ai?tab=images');
    await expect(page.locator('h1')).toBeFocused();
    await page.locator('#ai-image-MiniMaxH3Image').click();
    await page.getByRole('button', { name: 'Refresh image models', exact: true }).click();
    await page.locator('#comfy-image-model').selectOption('minimax_h3_image_pruned_w4a8.safetensors');
    await expect(page.locator('#comfy-image-vae')).toHaveValue('minimax_h3_image_vae_fp16.safetensors');
    await expect(page.locator('#comfy-image-edit-lora')).toHaveCount(0);
    await page.getByRole('button', { name: 'Save MiniMax H3 Image', exact: true }).click();
    await expect(page.getByText('MiniMax H3 Image saved.', { exact: true })).toBeVisible();
    await page.reload();
    await page.locator('#ai-image-MiniMaxH3Image').click();
    await expect(page.locator('#comfy-image-model')).toHaveValue('minimax_h3_image_pruned_w4a8.safetensors');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    await page.screenshot({ path: `test-results/h3-image-settings-${width}.png`, fullPage: true });
  });
}

test('H3 Image defaults to 4 MP and offers native ratios alongside the video editor', async ({ page, request }) => {
  const project = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${project.id}/images`);
  await page.goto(`/projects/${project.id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await toolsTab(page, 'Prompt');
  const workflow = page.getByLabel('Image workflow', { exact: true });
  await workflow.selectOption('MiniMaxH3Image');
  const resolution = page.getByLabel('Resolution', { exact: true });
  await expect(resolution).toHaveValue('2048');
  await expect(page.locator('.image-output-size')).toContainText('2048 × 2048');
  await resolution.selectOption('4096');
  await page.locator('#aspect').selectOption('21:9');
  await expect(page.locator('.image-output-size')).toContainText('6272 × 2688');
  await expect(workflow.locator('option[value="MiniMaxH3"]')).toHaveText('MiniMax H3 (video-model edit)');
  await workflow.selectOption('Krea2');
  await expect(resolution).toHaveValue('1024');
  await workflow.selectOption('MiniMaxH3Image');
  await expect(resolution).toHaveValue('4096');
});
