import { test, expect } from './fixtures.js';
import { toolsTab, addImageReference } from './workspace-tools.js';

test('Klein settings preserve Krea and fit desktop and mobile', async ({ page }) => {
  await page.goto('/settings/ai');
  await expect(page.locator('h1')).toBeFocused();
  await page.getByRole('tab', { name: 'Image models', exact: true }).click();
  const workflows = page.getByRole('tablist', { name: 'Image workflows' });
  const entry = name => workflows.getByRole('tab', { name, exact: true });
  await entry('Krea 2').click();
  const krea = await page.locator('#comfy-image-model').inputValue();
  await entry('FLUX.2 Klein 9B KV').click();
  await expect(page.getByLabel('Klein 9B KV model')).toHaveValue('flux-2-klein-9b-kv-fp8.safetensors');
  await expect(page.getByLabel('Krea 2 Edit LoRA')).toHaveCount(0);
  await page.getByRole('button', { name: 'Refresh image models' }).click();
  await expect(page.getByRole('button', { name: 'Save FLUX.2 Klein 9B KV' })).toBeEnabled();
  await page.screenshot({ path: 'test-results/klein-settings-desktop.png', fullPage: true });
  await page.getByRole('button', { name: 'Save FLUX.2 Klein 9B KV' }).click();
  await expect(page.getByText('FLUX.2 Klein 9B KV saved.', { exact: true })).toBeVisible();
  await entry('Defaults').click();
  await page.getByLabel('Default image workflow').selectOption('Flux2Klein9bKv');
  await page.getByRole('button', { name: 'Save image defaults' }).click();
  await expect(page.getByText('Image defaults saved.', { exact: true })).toBeVisible();
  await page.reload();
  await expect(page.locator('h1')).toBeFocused();
  // Image models opens on the default workflow; other workflows keep their own settings.
  await expect(entry('FLUX.2 Klein 9B KV')).toHaveAttribute('aria-selected', 'true');
  await entry('Krea 2').click();
  await expect(page.getByLabel('Krea 2 model', { exact: true })).toHaveValue(krea);
  await entry('Defaults').click();
  await expect(page.getByLabel('Default image workflow')).toHaveValue('Flux2Klein9bKv');
  await entry('FLUX.2 Klein 9B KV').click();
  await page.setViewportSize({ width: 390, height: 844 });
  await entry('FLUX.2 Klein 9B KV').focus();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Refresh image models', exact: true })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.locator('#comfy-image-model')).toBeFocused();
  await page.keyboard.press('Tab');
  const downloads = page.locator('.image-model-downloads[data-image-file=Model]');
  await expect(downloads.locator('summary')).toBeFocused();
  await page.keyboard.press('Space');
  await expect(downloads).toHaveAttribute('open');
  await page.keyboard.press('Tab');
  await expect(downloads.getByRole('link', { name: 'flux-2-klein-9b-kv-fp8.safetensors', exact: true })).toBeFocused();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: 'test-results/klein-settings-mobile.png', fullPage: true });
});

test('Klein multi-reference edit captures inputs and persists lineage', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  const project = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${project.id}/images`)).json();
  const [person, outfit] = library.assets;
  await page.goto(`/projects/${project.id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await toolsTab(page, 'Prompt');
  await page.getByLabel('Image workflow', { exact: true }).selectOption('Flux2Klein9bKv');
  await page.locator('.asset-media-card[data-media-kind=Image] .media-select').click();
  await toolsTab(page, 'Inputs');
  await addImageReference(page, `${outfit.id}/${outfit.images[0].id}`);
  await expect(page.getByRole('img', { name: 'Image 2', exact: true })).toBeVisible();
  await toolsTab(page, 'Prompt');
  await page.getByLabel('Edit instruction').fill('Dress the person in image 1 in the coat from image 2.');
  await toolsTab(page, 'Prompt');
  await page.getByLabel('Image workflow', { exact: true }).selectOption('Krea2');
  await expect(page.getByRole('button', { name: 'Generate edited images', exact: true })).toBeEnabled();
  await toolsTab(page, 'Prompt');
  await page.getByLabel('Image workflow', { exact: true }).selectOption('Flux2Klein9bKv');
  await expect(page.getByLabel('Reference fidelity', { exact: false })).toHaveCount(0);
  await page.screenshot({ path: 'test-results/klein-references-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await toolsTab(page, 'Prompt');
  await expect(page.getByLabel('Edit instruction')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await toolsTab(page, 'Inputs');
  await page.getByRole('button',{name:/^(?:Edit|Manage) references$/,exact:true}).focus();
  await expect(page.getByRole('button',{name:/^(?:Edit|Manage) references$/,exact:true})).toBeFocused();
  await page.screenshot({ path: 'test-results/klein-references-mobile.png', fullPage: true });
  await page.getByRole('button', { name: 'Generate edited images', exact: true }).click();
  await expect(page.getByLabel('Image workflow', { exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact:true })).toBeDisabled();
  const state = async () => (await (await request.get(`/fixtures/${project.id}`)).json()).assets;
  await expect.poll(async () => (await state()).assets[0].images.length).toBe(2);
  const edited = (await state()).assets[0].images.find(image => image.generation?.edit);
  expect(edited.origin).toBe(2); // AssetImageOrigin.Edited in the fixture HTTP response.
  expect(edited.generation.workflow).toBe(1); // ImageWorkflow.Flux2Klein9bKv.
  expect(edited.generation.edit.references).toEqual([
    { assetId: person.id, imageId: person.images[0].id },
    { assetId: outfit.id, imageId: outfit.images[0].id }
  ]);
  expect(edited.isReference).toBe(false);
  await page.reload();
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.getByRole('button',{name:/^Preview image/}).first().click();
  await page.locator('.review-image-details summary').click();
  await expect(page.locator('.preview-reference-list li')).toHaveCount(2);
  await expect(page.locator('.image-review-dialog')).toContainText('FLUX.2 Klein 9B KV');
  await expect(page.locator('.image-review-dialog')).not.toContainText('Edit LoRA');
});
