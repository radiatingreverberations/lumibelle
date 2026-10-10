import { test, expect } from './fixtures.js';
import { submitPlanning } from './text-assistance-tools.js';
import { composeProduction, generateTakes, toolsTab } from './workspace-tools.js';

const review = page => page.locator('.shot-review-dialog');
const state = async (request, id) => (await request.get(`/fixtures/${id}/shots`)).json();
async function setup(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Draft shots', exact: true }).click();
  await submitPlanning(page);
  await page.locator('.shot-planning-dialog').getByRole('button', { name: 'Add reviewed shots' }).click();
  await page.getByLabel('Duration (seconds)').fill('1');
  await page.getByLabel('Duration (seconds)').blur();
  await expect.poll(async () => (await state(request, id)).shots[0].duration).toBe(1);
  await composeProduction(page); await toolsTab(page, 'Generate');
  await page.getByLabel('Save lossless frames', { exact: true }).setChecked(true);
  await page.getByLabel('Save latents', { exact: true }).setChecked(true);
  await generateTakes(page); await expect(review(page)).toBeVisible();
  return id;
}

test('extend preserves the source, reviews the join and leaves selection explicit', async ({ page, request }) => {
  test.setTimeout(150000);
  const id = await setup(page, request); const before = await state(request, id); const source = before.takes[0];
  await review(page).getByRole('slider', { name: 'Video position' }).press('End');
  await review(page).getByRole('button', { name: 'Continue', exact: true }).click();
  const form = review(page).getByRole('region', { name: 'Extend take', exact: true });
  await expect(form.getByLabel('Added duration', { exact: true })).toHaveValue('5');
  await expect(form.getByLabel('Next action', { exact: true })).toHaveValue('');
  await expect(form.getByLabel('New dialogue', { exact: true })).toHaveValue('');
  await expect(form).toContainText('Saved motion');
  await form.getByLabel('Next action', { exact: true }).fill('She walks onwards, lifting her hand.');
  await form.getByLabel('Added duration', { exact: true }).fill('2');
  await form.getByRole('button', { name: 'Queue extension', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).takes.filter(t => t.composition).length, { timeout: 45000 }).toBe(1);
  const after = await state(request, id); const extended = after.takes.find(t => t.composition);
  await expect(review(page).locator('video')).toHaveAttribute('src', `/media/projects/${id}/takes/${extended.id}`);
  await expect(review(page).getByRole('slider', { name: 'Video position' })).toHaveAttribute('max', String(extended.composition.segments.reduce((n, s) => n + s.endFrameExclusive - s.startFrame, 0) - 1));
  expect(after.shots[0].selectedTakeId).toBeNull(); expect(after.shots[0].duration).toBe(before.shots[0].duration);
  expect(extended.composition.segments[0].source.refinementPackage.sha256).toBe(source.refinementPackage.sha256);
  const download = await request.get(`/media/projects/${id}/takes/${extended.id}`); expect(download.ok()).toBe(true);
  await review(page).getByRole('button', { name: 'Preview join', exact: true }).click();
  await review(page).getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(form).toBeVisible(); await form.getByRole('button', { name: 'Cancel', exact: true }).click();
  await review(page).getByRole('button', { name: 'Refine…', exact: true }).click();
  const refine = review(page).getByRole('region', { name: 'Refine take', exact: true });
  await expect(refine).toContainText('final segment'); await expect(refine.getByLabel('Output size').locator('option')).toHaveCount(1);
  await refine.getByRole('button', { name: 'Back to review', exact: true }).click();
  await review(page).getByRole('button', { name: 'Trim', exact: true }).click();
  const trim = review(page).getByRole('region', { name: 'Trim take', exact: true });
  await expect(trim.getByLabel('Snap end for continuation')).toBeVisible();
  await trim.getByLabel('Snap end for continuation').uncheck();
  await trim.getByRole('slider', { name: 'Trim start frame', exact: true }).press('ArrowRight');
  await expect(trim).toContainText('Exact-frame mode');
  await trim.getByRole('button', { name: 'Cancel', exact: true }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  await review(page).getByRole('slider', { name: 'Video position' }).press('End');
  await review(page).getByRole('button', { name: 'Continue', exact: true }).click();
  await expect(form).toBeVisible();
  expect(await form.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  await form.getByLabel('Next action', { exact: true }).scrollIntoViewIfNeeded();
  await page.screenshot({ path: 'obj/extension-narrow.png', fullPage: true });
});

for (const narrow of [false, true]) test(`extension reference drafts apply, cancel and reset locally (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(150000);
  await page.setViewportSize({ width: 1280, height: 1000 });
  const id = await setup(page, request);
  await page.setViewportSize({ width: narrow ? 390 : 1280, height: narrow ? 844 : 1000 });
  await request.post(`/fixtures/${id}/images`);
  const library = (await (await request.get(`/fixtures/${id}`)).json()).assets;
  const room = library.assets[1];
  const before = await state(request, id);
  await review(page).getByRole('button', { name: 'Continue', exact: true }).click();
  const form = review(page).getByRole('region', { name: 'Extend take', exact: true });
  const summary = form.locator('.extension-references');
  await expect(summary).toContainText('From the original take');
  await form.getByLabel('Next action', { exact: true }).fill('The camera slowly moves into the room.');
  await form.getByLabel('Extension prompt', { exact: true }).fill('Keep this authored prompt while changing the references.');
  const manage = form.getByRole('button', { name: 'Manage references', exact: true });
  const editor = page.locator('.shot-reference-dialog').filter({ has: page.getByRole('button', { name: 'Close extension references', exact: true }) });
  await manage.click(); await editor.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(summary).toContainText('From the original take');
  await expect(form.getByRole('checkbox', { name: /References changed/ })).toHaveCount(0);
  const choose = async () => {
    await manage.click();
    await editor.locator(`[data-reference="${room.id}/${room.images[0].id}"]`).click();
  };
  await choose();
  await editor.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(summary).toContainText('0 pictures');
  await choose(); await editor.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(editor).toBeHidden();
  await expect(summary).toContainText('1 picture');
  await expect(summary).toContainText('Customized for this extension');
  await expect(form.getByLabel('Extension prompt', { exact: true })).toHaveValue('Keep this authored prompt while changing the references.');
  await expect(form.getByRole('button', { name: 'Queue extension', exact: true })).toBeDisabled();
  await form.getByRole('checkbox', { name: /References changed/ }).check();
  await expect(form.getByRole('button', { name: 'Queue extension', exact: true })).toBeEnabled();
  await form.getByRole('button', { name: 'Reset', exact: true }).click();
  await expect(summary).toContainText('From the original take');
  await expect(summary).toContainText('0 pictures');
  await choose(); await editor.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(summary).toContainText('Customized for this extension');
  expect((await state(request, id)).shots).toEqual(before.shots);
  expect((await state(request, id)).takes).toEqual(before.takes);
  expect(await form.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  if (!narrow) {
    const action = await form.getByLabel('Next action', { exact: true }).boundingBox();
    const dialogue = await form.getByLabel('New dialogue', { exact: true }).boundingBox();
    expect(Math.abs(action.y - dialogue.y)).toBeLessThan(2);
    expect(dialogue.x).toBeGreaterThan(action.x + action.width);
  }
  await form.getByLabel('Next action', { exact: true }).scrollIntoViewIfNeeded();
  await page.screenshot({ path: `artifacts/ux-review/extension-references-${narrow ? 'narrow' : 'desktop'}.png` });
});

for (const leading of [false, true]) test(`composition failures are inspected from the ${leading ? 'lead-in' : 'extension'} compose control`, async ({ page, request }) => {
  test.setTimeout(150000);
  await page.setViewportSize({ width: 1280, height: 1000 });
  const id = await setup(page, request);
  const before = await state(request, id);
  await review(page).getByRole('button', { name: leading ? 'Lead into' : 'Continue', exact: true }).click();
  const form = review(page).getByRole('region', { name: leading ? 'Lead into take' : 'Extend take', exact: true });
  const action = form.getByLabel(leading ? 'Lead-in action' : 'Next action', { exact: true });
  const prompt = form.getByLabel(leading ? 'Lead-in prompt' : 'Extension prompt', { exact: true });
  const composeLabel = leading ? 'Compose lead-in prompt' : 'Compose extension prompt';
  const response = page.locator('.text-request-dialog').filter({ has: page.getByRole('heading', { name: leading ? 'Lead-in prompt response' : 'Extension prompt response', exact: true }) });
  await action.fill('INVALID_COMPOSITION: the camera enters the room.');
  await prompt.fill('Keep this manually written prompt.');
  await form.getByRole('button', { name: 'Text model options', exact: true }).click();
  const models = page.locator('.ai-assist-dialog').filter({ has: page.getByRole('heading', { name: 'Text model', exact: true }) });
  await models.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await models.getByRole('button', { name: 'Done', exact: true }).click();
  await request.post('/fixtures/text-queue?paused=true');
  await form.getByRole('button', { name: composeLabel, exact: true }).click();
  const queued = form.getByRole('button', { name: /Queued.*View request/ });
  await expect(queued).toBeEnabled();
  await queued.click();
  await expect(response).toBeVisible();
  await expect(response).toContainText('Waiting for the response');
  await response.getByRole('button', { name: 'Close', exact: true }).click();
  await request.post('/fixtures/text-queue?paused=false');
  const attention = form.getByRole('button', { name: 'Needs attention', exact: true });
  await expect(attention).toBeEnabled();
  await expect(prompt).toHaveValue('Keep this manually written prompt.');
  await expect(form.getByRole('alert')).toHaveCount(0);
  if (!leading) await page.screenshot({ path: 'artifacts/ux-review/extension-compose-needs-attention.png' });
  await attention.click();
  await expect(response.getByRole('alert')).toContainText('not a complete composition');
  await expect(response.locator('pre')).toHaveText('{"kind":"Prompt","text":"unfinished');
  if (!leading) await page.screenshot({ path: 'artifacts/ux-review/extension-compose-response.png' });
  if (!leading) {
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await response.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
    await page.screenshot({ path: 'artifacts/ux-review/extension-compose-response-narrow.png' });
    await page.setViewportSize({ width: 1280, height: 1000 });
  }
  await response.getByRole('button', { name: 'New request', exact: true }).click();
  await expect(response).toBeHidden();
  await expect(prompt).toHaveValue('Keep this manually written prompt.');
  await action.fill('The camera enters the room.');
  await form.getByRole('button', { name: composeLabel, exact: true }).click();
  await expect(form.getByRole('button', { name: 'View response', exact: true })).toBeVisible();
  await expect(prompt).toHaveValue(/Vision-composed staging/);
  await form.getByRole('button', { name: 'View response', exact: true }).click();
  await expect(response.locator('pre')).toContainText('Vision-composed staging');
  await expect(response.getByRole('alert')).toHaveCount(0);
  await response.getByRole('button', { name: 'Close', exact: true }).click();
  const after = await state(request, id);
  expect(after.shots).toEqual(before.shots);
  expect(after.takes).toEqual(before.takes);
});

async function shutdown(request, hostLifecycle) {
  const started = Date.now();
  await request.post('/fixtures/shutdown');
  let timer;
  try {
    const code = await Promise.race([hostLifecycle.exited, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(`Shutdown exceeded 35 seconds.\n${hostLifecycle.output().slice(-5000)}`)), 35000);
    })]);
    const elapsed = Date.now() - started;
    console.info(`Graceful shutdown: ${elapsed} ms`);
    expect(code, hostLifecycle.output().slice(-5000)).toBe(0);
    expect(elapsed, hostLifecycle.output().slice(-5000)).toBeLessThan(5000);
  } finally { clearTimeout(timer); }
}

test('graceful shutdown with a connected browser finishes promptly', async ({ page, request, hostLifecycle }) => {
  test.setTimeout(60000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: /^AI activity/ }).click();
  await expect(page.getByRole('dialog', { name: 'AI activity', exact: true })).toBeVisible();
  await shutdown(request, hostLifecycle);
});

test('graceful shutdown with an active extension compose control finishes promptly', async ({ page, request, hostLifecycle }) => {
  test.setTimeout(150000);
  await setup(page, request);
  await review(page).getByRole('button', { name: 'Continue', exact: true }).click();
  const form = review(page).getByRole('region', { name: 'Extend take', exact: true });
  await form.getByLabel('Next action', { exact: true }).fill('The camera enters the room.');
  await form.getByRole('button', { name: 'Text model options', exact: true }).click();
  const models = page.locator('.ai-assist-dialog').filter({ has: page.getByRole('heading', { name: 'Text model', exact: true }) });
  await models.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await models.getByRole('button', { name: 'Done', exact: true }).click();
  await request.post('/fixtures/text-queue?paused=true');
  await form.getByRole('button', { name: 'Compose extension prompt', exact: true }).click();
  await expect(form.getByRole('button', { name: /Queued.*View request/ })).toBeVisible();
  await shutdown(request, hostLifecycle);
});
