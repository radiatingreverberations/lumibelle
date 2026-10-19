import { modelOptions, closeComposer } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';
import { cropImageInput, toolsTab, addImageReference } from './workspace-tools.js';

const modal = page => page.locator('.prompt-enhancement-dialog');
async function submitEnhancement(page) {
  await page.getByRole('button', { name: 'Improve prompt', exact: true }).click();
  const composer = page.locator('.ai-assist-dialog').last();
  await composer.getByRole('button', { name: 'Enhance', exact: true }).click();
  await expect(composer).toBeHidden();
  if (!await modal(page).isVisible()) await page.locator('.prompt-enhancement .request-action-button').click();
  await expect(modal(page)).toBeVisible();
}
const activity = page => page.getByRole('dialog', { name: 'AI activity', exact: true });
async function setup(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${id}/images`)).json();
  await page.goto(`/projects/${id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await toolsTab(page, 'Prompt');
  // Improve prompt stays disabled until there is a prompt to improve.
  await expect(page.getByRole('button', { name: 'Improve prompt', exact: true })).toBeDisabled();
  return { id, library };
}
async function togglePause(page, paused) {
  await page.locator('.ai-activity-trigger').click();
  await activity(page).getByRole('button', { name: `${paused ? 'Pause' : 'Resume'} queue OpenRouter`, exact: true }).click();
  await activity(page).getByRole('button', { name: 'Close AI activity' }).click();
}
async function jobFor(request, project) { return (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.target.projectId === project); }
async function done(request, project) { await expect.poll(async () => (await jobFor(request, project))?.state, { timeout: 20000 }).toBe('Completed'); }

test('queued enhancement survives reload and navigation and opens the exact saved result from activity', async ({ page, request }) => {
  const { id, library } = await setup(page, request);
  await togglePause(page, true);
  try {
    await page.locator('#image-prompt').fill('A tiny mouse, SLOW, with the exact sign HELLO.');
    await submitEnhancement(page);
    await expect(page.getByText('Queued in Lumibelle · position 1', { exact: true })).toBeVisible();
    await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    await toolsTab(page, 'Prompt');
    await expect(page.locator('#image-prompt')).toHaveValue('A tiny mouse, SLOW, with the exact sign HELLO.');
    await expect(page.locator('.prompt-enhancement').getByRole('button', { name: /View request/ })).toBeEnabled();
    await page.locator('.project-tabs').getByRole('link', { name: 'Script', exact: true }).click();
  } finally { await togglePause(page, false); }
  await done(request, id); await expect(modal(page)).not.toBeVisible();
  const job = await jobFor(request, id); expect(job.unread).toBe(true);
  await page.locator('.ai-activity-trigger').click();
  await activity(page).getByRole('tab', { name: 'History', exact: true }).click();
  await activity(page).getByLabel('Project', { exact: true }).selectOption(id);
  await activity(page).locator(`[data-job-id="${job.id}"]`).getByRole('link', { name: 'Review', exact: true }).click();
  // The review link selects its asset, then leaves the address so a reload does not replay it.
  await expect(page.locator('.asset-list-row.selected')).toHaveAttribute('data-asset-id', library.assets[0].id);
  await expect(modal(page).getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
  await expect.poll(async () => (await jobFor(request, id)).unread).toBe(false);
  await modal(page).locator('#enhancement-suggestion').fill('My reviewed mouse prompt.');
  await modal(page).getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(page.locator('#image-prompt')).toHaveValue('My reviewed mouse prompt.');
  await expect(page.getByRole('button', { name: 'Improve prompt', exact: true })).toBeFocused();
  await page.getByRole('button', { name: 'Undo enhancement', exact: true }).click();
  await expect(page.locator('#image-prompt')).toHaveValue('A tiny mouse, SLOW, with the exact sign HELLO.');
});

test('closing or switching asset keeps enhancement running without replacing a newer draft or opening a delayed modal', async ({ page, request }) => {
  const { id, library } = await setup(page, request);
  await page.locator('#image-prompt').fill('A mouse SLOW');
  await submitEnhancement(page);
  await expect(modal(page)).toBeVisible();
  await modal(page).getByRole('button', { name: 'Close', exact: true }).click();
  await page.locator('#image-prompt').fill('Newer instructions, keep these.');
  await page.locator('.asset-choice').nth(1).click();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue(library.assets[1].name);
  await toolsTab(page, 'Prompt'); await page.locator('#image-prompt').fill('A different asset prompt.');
  await expect(page.getByRole('button', { name: 'Improve prompt', exact: true })).toBeEnabled();
  await done(request, id); expect((await jobFor(request, id)).cancelRequested).toBe(false);
  await expect(modal(page)).not.toBeVisible();
  await page.locator('.asset-choice').first().click();
  await expect(page.getByLabel('Name', { exact: true })).toHaveValue(library.assets[0].name);
  await toolsTab(page, 'Prompt');
  await page.getByRole('button', { name: /^(Review changes|View response|Needs attention)$/ }).click();
  await expect(modal(page)).toContainText('Since this prompt was written, the prompt was edited');
  await expect(page.locator('#image-prompt')).toHaveValue('Newer instructions, keep these.');
  // Applying anyway replaces the prompt as it is now, which the review shows, and Undo restores it.
  await expect(modal(page).locator('#enhancement-current')).toHaveValue('Newer instructions, keep these.');
  const suggestion = await modal(page).locator('#enhancement-suggestion').inputValue();
  await modal(page).getByRole('button', { name: 'Apply anyway', exact: true }).click();
  await expect(modal(page)).not.toBeVisible();
  await expect(page.locator('#image-prompt')).toHaveValue(suggestion);
  await page.getByRole('button', { name: 'Undo enhancement', exact: true }).click();
  await expect(page.locator('#image-prompt')).toHaveValue('Newer instructions, keep these.');
});

test('reloaded edit enhancement restores the exact reference crops and crop cancellation preserves them', async ({ page, request }) => {
  const { id, library } = await setup(page, request);
  await page.locator('.asset-media-card[data-media-kind=Image] .media-select').first().click();
  await toolsTab(page, 'Inputs');
  await addImageReference(page, `${library.assets[1].id}/${library.assets[1].images[0].id}`);
  await cropImageInput(page,0,2); await cropImageInput(page,1,4);
  await toolsTab(page, 'Prompt');
  const original = `Use the jacket from image 2 on image 1. Queued crop test ${id}`;
  await page.locator('#image-prompt').fill(original);
  await modelOptions(page, page.locator('.prompt-enhancement'));
  await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await page.locator('.ai-assist-dialog').last().getByRole('button', { name: 'Close', exact: true }).click();
  await page.getByRole('checkbox', { name: 'Inspect reference images' }).check();
  await closeComposer(page);
  await togglePause(page, true);
  try {
    await submitEnhancement(page);
    await expect(page.getByText('Queued in Lumibelle · position 1', { exact: true })).toBeVisible();
    await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    await toolsTab(page, 'Prompt');
    await expect(page.locator('#image-prompt')).toHaveValue(original);
    await expect(page.locator('.prompt-enhancement').getByRole('button', { name: /View request/ })).toBeEnabled();
  } finally { await togglePause(page, false); }
  await done(request, id);
  await expect(modal(page)).not.toBeVisible();
  await page.getByRole('button', { name: /^(Review changes|View response|Needs attention)$/ }).click();
  await expect(modal(page).getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
  await modal(page).getByRole('button', { name: 'Close', exact: true }).click();
  await toolsTab(page, 'Inputs');
  await page.getByRole('button',{name:/^(?:Edit|Manage) references$/,exact:true}).click();
  const inputs=page.locator('.image-input-manager-dialog'); await inputs.locator('.reference-crop-button').nth(1).click();
  const cropDialog = page.locator('.image-input-crop-dialog');
  await cropDialog.getByText('Precise crop controls',{exact:true}).click(); await expect(cropDialog.getByRole('slider',{name:'Crop zoom',exact:true})).toHaveValue('4');
  await cropDialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await inputs.getByRole('button',{name:'Cancel',exact:true}).click();
  await toolsTab(page, 'Prompt');
  await page.getByRole('button', { name: /^(Review changes|View response|Needs attention)$/ }).click();
  await expect(modal(page).getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
  const calls = await (await request.get('/fixtures/enhancements')).json();
  const call = calls.find(c => c.context.authorRequest === original);
  expect(call.images).toEqual([{ width: 32, height: 40 }, { width: 16, height: 20 }]);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'artifacts/validation/plan005-enhancement-review-mobile.png' });
  await modal(page).getByRole('button', { name: 'Apply changes', exact: true }).click();
  await toolsTab(page, 'Prompt');
  await page.getByRole('button', { name: 'Generate edited images', exact: true }).click();
  await expect(page.locator('.image-review-dialog')).toBeVisible();
  const saved = (await (await request.get(`/fixtures/${id}`)).json()).assets.assets[0].images.at(-1).generation.edit;
  expect(saved.sourceCrop.width).toBe(0.5); expect(saved.referenceCrops[0].crop.width).toBe(0.25);
});
