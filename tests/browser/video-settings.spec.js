import { test, expect } from './fixtures.js';

test('video setup checks on open and keeps chosen community files through save and reload', async ({ page }) => {
  await page.goto('/settings/ai');
  await expect(page.locator('h1')).toBeFocused();
  await page.getByRole('tab', { name: 'Video models', exact: true }).click();
  const panel = page.locator('.video-model-form');
  const model = panel.locator('[data-requirement=model]');
  // The page checks ComfyUI by itself and says where each file goes and where to get it.
  await expect(panel.locator('[data-requirement=encoder] .video-status')).toHaveText('Installed');
  await expect(panel.locator('.video-setup-summary')).toHaveText(/presets ready|Every preset/);
  await expect(panel.locator('[data-requirement=encoder]')).toContainText('models/text_encoders');
  await expect(panel.locator('[data-requirement=encoder] a').first()).toHaveAttribute('href', /koongrizzly/);
  await expect(panel.locator('.video-preset-settings > .video-preset-setup')).toHaveCount(7);
  await expect(panel.locator('.video-preset-setup')).toHaveCount(10);
  await expect(panel.getByText(/refinement/i)).toHaveCount(0);
  const attention = panel.getByLabel('H3 dense attention', { exact: true });
  await panel.locator('[data-add-on=attention] > summary').click();
  await expect(panel.getByRole('checkbox', { name: /Sol-Attn/ })).toHaveCount(0);
  await expect(attention).toHaveValue('ServerDefault');
  await attention.selectOption('Sage');
  await expect(panel.locator('[data-requirement=sage-nodes] .video-status')).toHaveText('Not found');
  await panel.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(attention).toHaveValue('ServerDefault');
  await attention.selectOption('Kitchen');
  // Retired presets keep their setup, out of the way.
  await panel.locator('.video-retired-presets > summary').click();
  await panel.locator('[data-preset=turbo8] > summary').click();
  const turbo8 = panel.locator('[data-preset=turbo8] [data-requirement=turbo8-lora]');
  const eightStepFile = 'h3/minimax_h3_ref2v_turbo_8step_v1.0_768p_comfyui_bf16.safetensors';
  await panel.getByLabel('Turbo 8-step LoRA file', { exact: true }).selectOption(eightStepFile);
  await expect(turbo8.locator('.video-status')).toHaveText('Installed');
  const encoder = panel.locator('[data-requirement=encoder]');
  const custom = 'custom/renamed-h3-encoder.safetensors';
  await expect(panel.getByLabel('Encoder file', { exact: true }).locator('optgroup[label="Other installed files"] option', { hasText: custom })).toHaveCount(1);
  await panel.getByLabel('Encoder file', { exact: true }).selectOption(custom);
  await expect(encoder.locator('.video-status')).toHaveText('Other file');
  await panel.getByRole('button', { name: 'Save video models', exact: true }).click();
  await expect(panel.getByText('Video models saved.', { exact: true })).toBeVisible();
  await page.reload();
  await expect(page.locator('h1')).toBeFocused();
  await page.getByRole('tab', { name: 'Video models', exact: true }).click();
  await panel.locator('[data-add-on=attention] > summary').click();
  await expect(attention).toHaveValue('Kitchen');
  await expect(panel.getByLabel('Encoder file', { exact: true })).toHaveValue(custom);
  await expect(encoder.locator('.video-status')).toHaveText('Other file');
  await panel.locator('.video-retired-presets > summary').click();
  await panel.locator('[data-preset=turbo8] > summary').click();
  await expect(panel.getByLabel('Turbo 8-step LoRA file', { exact: true })).toHaveValue(eightStepFile);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(encoder.locator('.video-status')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: 'test-results/video-settings-mobile.png', fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await model.locator('.video-requirement-options > summary').click();
  await expect(panel.locator('[data-requirement=model] .video-requirement-options a')).toHaveCount(4);
  await page.screenshot({ path: 'test-results/video-settings-desktop.png', fullPage: true });
  // Cancel discards a new draft.
  const modelFile = panel.getByLabel('Model file', { exact: true });
  await modelFile.selectOption('custom/renamed-ref2va.safetensors');
  await expect(model.locator('.video-status')).toHaveText('Other file');
  await panel.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(modelFile).not.toHaveValue('custom/renamed-ref2va.safetensors');
  // Keep the shared mock settings neutral for subsequent browser walkthroughs.
  await attention.selectOption('ServerDefault');
  await panel.getByRole('button', { name: 'Save video models', exact: true }).click();
});
