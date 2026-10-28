import { test, expect } from './fixtures.js';

for (const width of [1280, 390]) {
  test(`MOSS settings preserve model and edit choices independently at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 960 });
    await page.goto('/settings/ai?tab=images');
    await expect(page.locator('h1')).toBeFocused();
    await page.locator('#ai-image-MossImage').click();
    await expect(page.locator('#moss-steps')).toHaveValue('50');
    await expect(page.locator('#comfy-image-edit-lora')).toHaveCount(0);
    await page.getByRole('button', { name: 'Refresh image models', exact: true }).click();
    await page.locator('#comfy-image-model').selectOption('moss_image_sft_bf16.safetensors');
    await page.locator('#moss-edit-lora').selectOption('moss-edit/adapter_model.safetensors');
    await page.locator('#moss-edit-format').selectOption('ai-toolkit');
    await page.locator('#moss-steps').fill('64');
    await page.locator('#moss-guidance').fill('4.5');
    await page.getByRole('button', { name: 'Save MOSS-Image', exact: true }).click();
    await expect(page.getByText('MOSS-Image saved.', { exact: true })).toBeVisible();
    // Saving Fibo must neither copy its model into MOSS nor persist MOSS's unsaved edits.
    await page.locator('#moss-steps').fill('65');
    await page.locator('#ai-image-Fibo2').click();
    await expect(page.locator('#moss-steps')).toHaveCount(0);
    await page.getByRole('button', { name: 'Refresh image models', exact: true }).click();
    await page.locator('#comfy-image-model').selectOption('fibo2_dev_int8_convrot.safetensors');
    await page.getByRole('button', { name: 'Save Fibo 2.0', exact: true }).click();
    await expect(page.getByText('Fibo 2.0 saved.', { exact: true })).toBeVisible();
    await page.locator('#ai-image-MossImage').click();
    await expect(page.locator('#comfy-image-model')).toHaveValue('moss_image_sft_bf16.safetensors');
    await expect(page.locator('#comfy-image-encoder')).toHaveValue('qwen3.5_9b_w4a8_gptq.safetensors');
    await expect(page.locator('#moss-steps')).toHaveValue('65');
    await page.locator('#image-workflow-form').getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.locator('#moss-steps')).toHaveValue('64');
    await page.locator('#ai-image-Krea2').click();
    await expect(page.locator('#comfy-image-edit-lora')).toHaveValue('krea2_identity_edit_v1_2.safetensors');
    await page.locator('#ai-image-MossImage').click();
    await expect(page.locator('#comfy-image-model')).toHaveValue('moss_image_sft_bf16.safetensors');
    await expect(page.locator('#moss-steps')).toHaveValue('64');
    await page.reload(); await expect(page.locator('h1')).toBeFocused();
    await page.locator('#ai-image-MossImage').click();
    await expect(page.locator('#moss-steps')).toHaveValue('64');
    await expect(page.locator('#moss-guidance')).toHaveValue('4.5');
    await expect(page.locator('#moss-edit-format')).toHaveValue('ai-toolkit');
    await expect(page.locator('#comfy-image-model')).toHaveValue('moss_image_sft_bf16.safetensors');
    await page.locator('#ai-image-Fibo2').click();
    await expect(page.locator('#comfy-image-model')).toHaveValue('fibo2_dev_int8_convrot.safetensors');
    await page.locator('#ai-image-MossImage').click();
    await page.locator('.image-model-downloads[data-image-file=Model] > summary').click();
    await page.locator('.image-model-downloads[data-image-file=TextEncoder] > summary').click();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true);
    await page.screenshot({ path: `test-results/moss-model-downloads-${width}.png`, fullPage: true });
  });
}
