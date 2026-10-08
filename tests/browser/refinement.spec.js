import { planningComposer, submitPlanning } from './text-assistance-tools.js';
import { generateTakes, composeProduction, closeShotSetup as closeSetup, toolsTab } from './workspace-tools.js';
import { test, expect } from './fixtures.js';

const review = page => page.locator('.shot-review-dialog');
const state = async (request, id) => (await request.get(`/fixtures/${id}/shots`)).json();
async function setup(page, request, saveLatents = true) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Draft shots', exact: true }).click();
  const planning = page.locator('.shot-planning-dialog');
  await submitPlanning(page);
  await planning.getByRole('button', { name: 'Add reviewed shots' }).click();
  await page.getByLabel('Duration (seconds)').fill('1'); await page.getByLabel('Duration (seconds)').blur();
  await expect.poll(async () => (await state(request, id)).shots[0].duration).toBe(1);
  await composeProduction(page);
  await toolsTab(page, 'Generate');
  await page.getByLabel('Save lossless frames', { exact: true }).check();
  await page.getByLabel('Save latents', { exact: true }).setChecked(saveLatents);
  await generateTakes(page);
  await expect.poll(async () => (await state(request, id)).takes.length, { timeout: 30000 }).toBe(1);
  await expect(review(page)).toBeVisible();
  return id;
}

test('Save latents is optional; a take without them is regenerated with its seed to refine it', async ({ page, request }) => {
  test.setTimeout(120000);
  const id = await setup(page, request, false);
  const original = (await state(request, id)).takes[0];
  expect(original.snapshot.captureRefinementData).toBe(false);
  expect(original.refinementPackage).toBeNull();
  const refine = review(page).getByRole('button', { name: 'Refine…', exact: true });
  await expect(refine).toBeDisabled();
  await expect(refine).toHaveAttribute('title', /^This take has no saved latents\. To refine it, regenerate it with the same seed and resolution/);
  await review(page).getByRole('button', { name: 'Close', exact: true }).click();
  // Regenerating with the same seed and size, with latents saved, gives a take that can be refined.
  await page.goto(`/projects/${id}/shots?view=Takes`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  const closeTools = page.getByRole('button', { name: 'Close Shot tools', exact: true });
  if (await closeTools.isVisible()) await closeTools.click();
  await page.locator(`[data-take-id='${original.id}']`).getByRole('button', { name: 'Regenerate…', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Regenerate take', exact: true });
  await expect(dialog.getByLabel('Save latents', { exact: true })).not.toBeChecked();
  await dialog.getByLabel('Save latents', { exact: true }).check();
  // The source's own resolution and seed.
  const resolution = dialog.getByLabel('Resolution', { exact: true });
  await resolution.selectOption(await resolution.locator('option', { hasText: `${original.width} × ${original.height}` }).getAttribute('value'));
  await dialog.getByRole('button', { name: 'Generate take', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect.poll(async () => (await state(request, id)).takes.length, { timeout: 30000 }).toBe(2);
  const again = (await state(request, id)).takes.find(t => t.id !== original.id);
  expect([again.seed, again.width, again.height]).toEqual([original.seed, original.width, original.height]);
  expect(again.snapshot.captureRefinementData).toBe(true);
  expect(again.refinementPackage).not.toBeNull();
  // The new take opens in review; close it to use the Takes view's own buttons.
  await expect(review(page)).toBeVisible();
  await review(page).getByRole('button', { name: 'Close', exact: true }).click();
  await expect(review(page)).toBeHidden();
  await expect(page.locator(`[data-take-id='${original.id}']`).getByRole('button', { name: 'Refine…', exact: true })).toBeDisabled();
  await page.locator(`[data-take-id='${again.id}']`).getByRole('button', { name: 'Refine…', exact: true }).click();
  await expect(review(page).getByRole('region', { name: 'Refine take' })).toBeVisible();
  await expect(review(page).locator('video')).toHaveAttribute('src', `/media/projects/${id}/takes/${again.id}`);
});

test('Save latents explains when ComfyUI lacks the nodes', async ({ page, request }) => {
  test.setTimeout(90000);
  await request.post('/fixtures/refinement-capture?available=false');
  try {
    const id = await setup(page, request, false);
    expect((await state(request, id)).takes[0].refinementPackage).toBeNull();
    await review(page).getByRole('button', { name: 'Close', exact: true }).click();
    await toolsTab(page, 'Generate');
    await page.getByLabel('Save latents', { exact: true }).check();
    await closeSetup(page);
    await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
    await expect(page.getByText('Update ComfyUI to save latents.', { exact: false }).first()).toBeVisible();
    expect((await state(request, id)).takes).toHaveLength(1);
  } finally {
    await request.post('/fixtures/refinement-capture?available=true');
  }
});

test('saved take -> Refine -> Another version -> Rework stays in one review and keeps source context', async ({ page, request }) => {
  test.setTimeout(120000);
  const id = await setup(page, request);
  // The companion nodes are gone: refinement uses stock ComfyUI nodes.
  expect((await request.get('/downloads/lumibelle-h3-companion.zip')).status()).toBe(404);
  const original = (await state(request, id)).takes[0];
  await review(page).getByRole('button', { name: 'Refine…', exact: true }).click();
  const form = review(page).getByRole('region', { name: 'Refine take' });
  await expect(form).toBeVisible();
  await expect(form.getByLabel('Mode', { exact: true })).toHaveValue('Refine');
  await expect(form.getByLabel('Output size')).toHaveValue('1');
  await expect(form).toContainText('7 steps · denoise 0.35');
  await expect(form).not.toContainText('Experimental');
  await form.getByLabel('Output size').selectOption('0');
  await form.getByRole('button', { name: 'Queue Refine', exact: true }).click();
  await expect(form).not.toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await expect.poll(async () => (await state(request, id)).takes.length, { timeout: 30000 }).toBe(2);
  let saved = await state(request, id); const refined = saved.takes.find(t => t.refinement);
  expect(refined.refinement.parentTakeId).toBe(original.id);
  expect(refined.snapshot).toEqual(original.snapshot);
  expect(refined.refinementPackage.id).not.toBe(original.refinementPackage.id);
  expect(saved.shots[0].selectedTakeId).toBeNull();
  await review(page).locator(`[data-shot-take="${refined.id}"]`).click();
  await expect(review(page).locator('video')).toHaveAttribute('src', `/media/projects/${id}/takes/${refined.id}`);
  await review(page).getByRole('button', { name: 'Another version', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).takes.length, { timeout: 30000 }).toBe(3);
  saved = await state(request, id);
  expect(new Set(saved.takes.map(t => t.seed)).size).toBe(3);
  await expect(review(page).locator('video')).toHaveAttribute('src', `/media/projects/${id}/takes/${refined.id}`);
  await review(page).getByRole('button', { name: 'Close', exact: true }).click();
  await page.reload(); await toolsTab(page, 'Generate');
  await closeSetup(page);
  await page.getByRole('button', { name: 'Review latest batch', exact: true }).click();
  await review(page).locator(`[data-shot-take="${refined.id}"]`).click();
  await review(page).getByRole('button', { name: 'Refine…', exact: true }).click();
  await form.getByLabel('Mode', { exact: true }).selectOption('Rework');
  await form.getByLabel('Output size').selectOption('0');
  await form.getByRole('button', { name: 'Queue Rework', exact: true }).click();
  await review(page).getByRole('button', { name: 'Close', exact: true }).click();
  await page.goto(`/projects/${id}/assets`);
  await expect.poll(async () => (await state(request, id)).takes.length, { timeout: 30000 }).toBe(4);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await page.goto(`/projects/${id}/shots`); await toolsTab(page, 'Generate');
  await closeSetup(page);
  await page.getByRole('button', { name: 'Review latest batch', exact: true }).click();
  const rework = (await state(request, id)).takes.find(t => t.refinement?.mode === 1);
  expect(rework.refinement.parentTakeId).toBe(refined.id);
  const card = review(page).locator('.shot-candidate').filter({ has: page.locator(`[data-shot-take="${rework.id}"]`) });
  await card.getByRole('button', { name: 'Use this take', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).shots[0].selectedTakeId).toBe(rework.id);
  await expect(card.getByRole('button', { name: 'Discard', exact: true })).toBeDisabled();
  await page.screenshot({ path: 'artifacts/validation/refinement-desktop.png', fullPage: true });
  await card.getByRole('button', { name: 'Deselect take', exact: true }).click();
  await card.getByRole('button', { name: 'Discard', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).takes.length).toBe(3);
  await review(page).getByRole('button', { name: 'Undo', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).takes.length).toBe(4);
});

test('a refinement that fails shows its error and can be removed', async ({ page, request }) => {
  test.setTimeout(90000);
  const id = await setup(page, request);
  await review(page).getByRole('button', { name: 'Refine…', exact: true }).click();
  const form = review(page).getByRole('region', { name: 'Refine take' });
  await form.getByLabel('Output size').selectOption('2');
  await form.getByRole('button', { name: 'Queue Refine', exact: true }).click();
  const failed = review(page).locator('.take-versions');
  await expect(failed.getByRole('alert')).toContainText('mock out of GPU memory', { timeout: 30000 });
  const remove = failed.getByRole('button', { name: /^Remove failed Refine · \d+ × \d+$/ });
  await remove.scrollIntoViewIfNeeded();
  await page.screenshot({ path: 'artifacts/validation/refinement-failed.png' });
  await remove.click();
  await expect(failed).toHaveCount(0);
  expect((await state(request, id)).takes).toHaveLength(1);
  await expect(review(page).getByRole('button', { name: 'Refine…', exact: true })).toBeEnabled();
});

test('refinement options remain keyboard accessible and fit a narrow review', async ({ page, request }) => {
  test.setTimeout(90000);
  await setup(page, request);
  await page.setViewportSize({ width: 390, height: 844 });
  const improve = review(page).getByRole('button', { name: 'Refine…', exact: true });
  await improve.focus(); await page.keyboard.press('Enter');
  const form = review(page).getByRole('region', { name: 'Refine take' });
  await expect(form.getByRole('button', { name: 'Queue Refine', exact: true })).toBeEnabled();
  await expect(form.getByLabel('Mode', { exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'artifacts/validation/refinement-mobile.png', fullPage: true });
  await form.getByRole('button', { name: 'Back to review', exact: true }).click();
  await expect(form).not.toBeVisible();
  await expect(improve).toBeFocused();
});
