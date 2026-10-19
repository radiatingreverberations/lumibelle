import { test, expect } from './fixtures.js';

test('vLLM connection saves, discovers models and becomes the default', async ({ page }) => {
  await page.goto('/settings/ai?provider=vllm');
  await expect(page.locator('h1')).toBeFocused();
  const connection = page.locator('#connection-vllm-form');
  await expect(connection).toBeVisible();
  await page.locator('#vllm-url').fill('http://llm-server:8000');
  await connection.getByRole('button', { name: 'Check connection', exact: true }).click();
  await expect(connection.getByRole('status')).toHaveText('Mock provider ready');
  await connection.getByRole('button', { name: 'Save connection', exact: true }).click();
  await expect(page.locator('#vllm-url')).toHaveValue('http://llm-server:8000/v1');
  await page.getByRole('tab', { name: 'Text models', exact: true }).click();
  await page.getByRole('button', { name: 'Refresh vLLM models' }).click();
  await page.getByRole('button', { name: 'Star Mock script writer', exact: true }).click();
  await page.getByRole('tab', { name: 'Defaults & profiles', exact: true }).click();
  await page.locator('#global-text-default').selectOption({ label: 'vLLM · Mock script writer' });
  await expect(page.getByRole('status').filter({ hasText: 'Global default updated.' })).toBeVisible();
  await page.reload();
  await expect(page.locator('h1')).toBeFocused();
  await page.getByRole('tab', { name: 'Defaults & profiles', exact: true }).click();
  await expect(page.locator('#global-text-default option:checked')).toHaveText('vLLM · Mock script writer');
});
