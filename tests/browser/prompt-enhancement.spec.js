import { test, expect } from './fixtures.js';
import { cropImageInput, toolsTab, addImageReference } from './workspace-tools.js';
import { closeComposer } from './text-assistance-tools.js';

const review = page => page.locator('.prompt-enhancement-dialog');
const action = page => page.locator('.prompt-enhancement .request-action-button');
async function setup(page, request) {
  const project = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${project.id}/images`)).json();
  await page.goto(`/projects/${project.id}/assets`);
  await toolsTab(page, 'Prompt');
  return { project, library };
}
async function openComposer(page) {
  const start = page.getByRole('button', { name: 'Improve prompt', exact: true });
  if (await start.isVisible()) await start.click();
  else {
    await page.locator('.prompt-enhancement .request-action-button').click();
    await page.locator('.prompt-enhancement-dialog').getByRole('button', { name: 'New request', exact: true }).click();
  }
  return page.locator('.ai-assist-dialog').last();
}
async function chooseVision(page) {
  const composer = await openComposer(page);
  await composer.getByRole('button', { name: 'Text model options', exact: true }).click();
  const options = page.locator('.ai-assist-dialog').last();
  await options.getByLabel('Text model', { exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  const optionsId = await options.getAttribute('id');
  await options.getByRole('button', { name: 'Set as project default', exact: true }).click();
  await expect(page.locator(`[id="${optionsId}"]`)).toBeHidden();
  await closeComposer(page);
}
async function submit(page, queued = false) {
  const composer = await openComposer(page);
  await composer.getByRole('button', { name: 'Enhance', exact: true }).click();
  await expect(composer).toBeHidden();
  if (queued) await action(page).click();
  await expect(review(page)).toBeVisible();
}
for (const workflow of ['Krea2', 'Flux2Klein9bKv']) {
  test(`${workflow} Create: review, apply, Undo, project default and exact generation`, async ({ page, request }) => {
    const { project } = await setup(page, request);
    await page.getByLabel('Image workflow', { exact: true }).selectOption(workflow);
    // Improve prompt needs a prompt before its model options can be opened.
    const original = `A mouse under a chair. ${workflow} create`;
    await page.getByLabel('Image prompt', { exact: true }).fill(original);
    await chooseVision(page);
    await submit(page);
    await expect(review(page).locator('#enhancement-original')).toHaveValue(original);
    const apply = review(page).getByRole('button', { name: 'Apply changes', exact: true });
    await expect(apply).toBeEnabled();
    await expect(page.locator('#image-prompt')).toHaveValue(original);
    await review(page).locator('#enhancement-suggestion').fill('A small watercolor mouse resting under a wooden chair.');
    await apply.click();
    await expect(review(page)).toBeHidden();
    await expect(action(page)).toBeFocused();
    await page.getByRole('button', { name: 'Undo enhancement', exact: true }).click();
    await expect(page.locator('#image-prompt')).toHaveValue(original);
    await submit(page); await expect(apply).toBeEnabled(); await apply.click();
    const expected = 'A carefully composed illustration. ' + original;
    await expect(page.locator('#image-prompt')).toHaveValue(expected);
    await page.getByRole('button', { name: 'Generate images', exact: true }).click();
    await expect(page.locator('.reference-card')).toHaveCount(2);
    const state = (await (await request.get(`/fixtures/${project.id}`)).json()).assets;
    expect(state.assets[0].images.at(-1).generation.prompt).toBe(expected);
    const calls = await (await request.get('/fixtures/enhancements')).json();
    const call = calls.find(c => c.context.authorRequest === original);
    expect(call.images).toEqual([]); expect(call.model).toBe('mock/alternate');
    expect(call.context.profile).toBe(workflow === 'Krea2' ? 'krea-create-v1' : 'klein-create-v1');
    await page.reload(); await toolsTab(page, 'Prompt');
    await page.getByLabel('Image prompt', { exact: true }).fill('A mouse beside a lamp.');
    const composer = await openComposer(page);
    await expect(composer.getByRole('button', { name: 'Text model options', exact: true })).toContainText('Project');
    const prefs = await (await request.get(`/fixtures/${project.id}/preferences`)).json();
    expect(prefs.textDefault.model).toBe('mock/alternate');
    await closeComposer(page);
  });

  test(`${workflow} Edit: inspect ordered crops, apply and generate`, async ({ page, request }) => {
    const { project, library } = await setup(page, request);
    await page.getByLabel('Image workflow', { exact: true }).selectOption(workflow);
    await page.locator('.asset-media-card[data-media-kind=Image] .media-select').click();
    await addImageReference(page, `${library.assets[1].id}/${library.assets[1].images[0].id}`);
    await cropImageInput(page, 0, 2); await cropImageInput(page, 1, 4);
    const original = `Transfer only the jacket from image 2 onto image 1. ${workflow} edit`;
    await page.getByLabel('Edit instruction', { exact: true }).fill(original);
    await chooseVision(page);
    const composer = await openComposer(page);
    await composer.getByRole('checkbox', { name: 'Inspect reference images' }).check();
    await closeComposer(page); await submit(page);
    const apply = review(page).getByRole('button', { name: 'Apply changes', exact: true });
    await expect(apply).toBeEnabled();
    const calls = await (await request.get('/fixtures/enhancements')).json();
    const call = calls.find(c => c.context.authorRequest === original);
    expect(call.images).toEqual([{ width: 32, height: 40 }, { width: 16, height: 20 }]);
    expect(call.context.references.map(r => r.imageNumber)).toEqual([1, 2]);
    expect(call.context.profile).toBe(workflow === 'Krea2' ? 'krea-edit-v1' : 'klein-edit-v1');
    await apply.click();
    await expect(page.locator('#image-prompt')).toHaveValue('A carefully composed illustration. ' + original);
    await page.getByRole('button', { name: 'Generate edited images', exact: true }).click();
    await expect(page.locator('.image-review-dialog')).toBeVisible();
    const state = (await (await request.get(`/fixtures/${project.id}`)).json()).assets;
    expect(state.assets[0].images.at(-1).generation.prompt).toBe('A carefully composed illustration. ' + original);
  });
}

test('narrow clarification, invalid response, cancellation and captured retry remain reachable', async ({ page, request }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await setup(page, request);
  await page.getByLabel('Image prompt', { exact: true }).fill('NEEDS_INPUT: Add lettering to a sign');
  await submit(page);
  await expect(review(page)).toContainText('More detail needed');
  await expect(review(page).getByRole('button', { name: 'Apply changes', exact: true })).toBeDisabled();
  await page.keyboard.press('Escape'); await expect(review(page)).toBeHidden();
  await expect(action(page)).toBeFocused();
  await toolsTab(page, 'Prompt');
  await page.getByLabel('Image prompt', { exact: true }).fill('INVALID response please');
  await submit(page);
  await expect(review(page)).toContainText('could not be read as a complete prompt');
  await expect(review(page).getByRole('button', { name: 'Retry', exact: true })).toBeVisible();
  await review(page).getByRole('button', { name: 'Close', exact: true }).click();
  await toolsTab(page, 'Prompt');
  await page.getByLabel('Image prompt', { exact: true }).fill('A mouse SLOW');
  await request.post('/fixtures/text-queue?paused=true');
  try {
    await submit(page, true);
    await review(page).getByRole('button', { name: 'Cancel enhancement', exact: true }).click();
    await expect(review(page)).toContainText(/cancelled/i);
  } finally { await request.post('/fixtures/text-queue?paused=false'); }
  await review(page).getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(review(page).getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  await review(page).getByRole('button', { name: 'Close', exact: true }).focus();
  await page.keyboard.press('Tab');
  expect(await review(page).evaluate(el => el.contains(document.activeElement))).toBe(true);
  await page.keyboard.press('Escape');
  await expect(page.locator('#image-prompt')).toHaveValue('A mouse SLOW');
});
