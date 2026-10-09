import { test, expect } from './fixtures.js';
import { submitPlanning } from './text-assistance-tools.js';
import { composeProduction, generateTakes, toolsTab } from './workspace-tools.js';

const review = page => page.locator('.shot-review-dialog');
const state = async (request, id) => (await request.get(`/fixtures/${id}/shots`)).json();
async function setup(page, request, lossless = true) {
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
  await page.getByLabel('Save lossless frames', { exact: true }).setChecked(lossless);
  await page.getByLabel('Save latents', { exact: true }).setChecked(lossless);
  await generateTakes(page); await expect(review(page)).toBeVisible();
  await expect.poll(async () => (await state(request, id)).takes.length).toBe(1);
  return id;
}
async function trim(page, start, end) {
  await review(page).getByRole('button', { name: 'Trim…', exact: true }).click();
  const editor = review(page).getByRole('region', { name: 'Trim take', exact: true });
  await expect(editor.getByRole('button', { name: 'Save trimmed version' })).toBeDisabled();
  const first = editor.getByRole('slider', { name: 'Trim start frame' });
  await first.focus(); await page.keyboard.press('Home');
  for (let i = 0; i < start; i++) await page.keyboard.press('ArrowRight');
  const last = editor.getByRole('slider', { name: 'Trim end frame' });
  await last.focus(); await page.keyboard.press('End');
  for (let i = Number(await last.getAttribute('max')); i > end; i--) await page.keyboard.press('ArrowLeft');
  const position = editor.getByRole('slider', { name: 'Video position' });
  await expect(position).toHaveAttribute('min', String(start));
  await expect(position).toHaveAttribute('max', String(end - 1));
  await position.press('Home');
  await expect(editor.locator('.take-player')).toHaveAttribute('data-frame-index', String(start));
  await editor.getByRole('button', { name: 'Set start here', exact: true }).click();
  await editor.getByRole('button', { name: 'Play', exact: true }).click();
  await expect(editor.locator('.take-player')).toHaveAttribute('data-frame-index', String(end - 1));
  await expect(editor.getByRole('button', { name: 'Play', exact: true })).toBeVisible();
  await editor.getByRole('button', { name: 'Set end here', exact: true }).click();
  await expect(editor).toContainText(`Keep ${end - start} frames`);
  await editor.getByRole('button', { name: 'Save trimmed version' }).click();
  await expect(editor).toBeHidden({ timeout: 30000 });
}

test('trim preserves full latents and refinement reapplies the range and reviews its shortened result', async ({ page, request }) => {
  test.setTimeout(150000);
  const id = await setup(page, request);
  const initial = await state(request, id); const parent = initial.takes[0];
  expect((await request.post(`/fixtures/${id}/cut`, { data: [{ id: parent.id, shotId: parent.shotId, takeId: parent.id,
    shotTitle: initial.shots[0].title, takeLabel: 'Original take', frameCount: parent.snapshot.frameCount, fps: 24, startFrame: 2, endFrameExclusive: 35 }] })).ok()).toBe(true);
  const originalCut = await (await request.get(`/fixtures/${id}/cut`)).json();
  await trim(page, 5, 30);
  let doc = await state(request, id); const shortened = doc.takes.find(t => t.trim);
  expect(doc.takes).toHaveLength(2); expect(doc.shots[0].selectedTakeId).toBeNull();
  expect(doc.shots[0].duration).toBe(initial.shots[0].duration);
  expect(await (await request.get(`/fixtures/${id}/cut`)).json()).toEqual(originalCut);
  expect(shortened.refinementPackage.sha256).toBe(parent.refinementPackage.sha256);
  expect(shortened.trim).toMatchObject({ startFrame: 5, endFrameExclusive: 30, sourceStartFrame: 5, sourceEndFrameExclusive: 30 });
  await expect(review(page).locator('video')).toHaveAttribute('src', `/media/projects/${id}/takes/${shortened.id}`);
  await expect(review(page)).toContainText('Full source latents retained');
  const videoPosition = review(page).getByRole('slider', { name: 'Video position' });
  await expect(videoPosition).toHaveAttribute('max', '24');
  await review(page).getByRole('button', { name: 'Refine…', exact: true }).click();
  const form = review(page).getByRole('region', { name: 'Refine take' });
  await expect(form).toContainText('automatically reapplies your trim');
  await form.getByLabel('Output size').selectOption('0');
  await form.getByRole('button', { name: 'Queue Refine', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).takes.length, { timeout: 45000 }).toBe(4);
  doc = await state(request, id);
  const refined = doc.takes.find(t => t.trim && t.refinement);
  expect(refined.trim.sourceStartFrame).toBe(5); expect(refined.trim.sourceEndFrameExclusive).toBe(30);
  await expect(review(page).locator('video')).toHaveAttribute('src', `/media/projects/${id}/takes/${refined.id}`);
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    review(page).getByRole('link', { name: 'Download MP4', exact: true }).click()
  ]);
  expect(download.suggestedFilename()).toBe(`take-${refined.id.replaceAll('-', '')}.mp4`);
  await review(page).getByRole('button', { name: 'Another version', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).takes.length, { timeout: 45000 }).toBe(6);
  await trim(page, 2, 20);
  await expect.poll(async () => (await state(request, id)).takes.length).toBe(7);
  await review(page).getByRole('button', { name: 'Another version', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).takes.length, { timeout: 45000 }).toBe(9);
  doc = await state(request, id);
  const revised = doc.takes.find(t => t.trim?.sourceStartFrame === 7 && t.trim?.sourceEndFrameExclusive === 25 && t.trim?.startFrame === 7);
  expect(revised).toBeTruthy();
  await expect(review(page).locator('video')).toHaveAttribute('src', `/media/projects/${id}/takes/${revised.id}`);
  await page.setViewportSize({ width: 390, height: 844 });
  await review(page).getByRole('button', { name: 'Trim…', exact: true }).click();
  await expect(review(page).getByRole('slider', { name: 'Trim start frame' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('MP4-only trim creates local frame numbering and continuation uses its last frame', async ({ page, request }) => {
  test.setTimeout(120000);
  const id = await setup(page, request, false);
  await trim(page, 5, 30);
  const shortened = (await state(request, id)).takes.find(t => t.trim);
  expect(shortened.frames).toHaveLength(0); expect(shortened.refinementPackage).toBeNull();
  const position = review(page).getByRole('slider', { name: 'Video position' });
  await position.focus(); await page.keyboard.press('End');
  await expect(review(page).locator('.take-player')).toHaveAttribute('data-frame-index', '24');
  await review(page).locator('.shot-candidate').filter({ has: page.locator(`[data-shot-take="${shortened.id}"]`) }).getByRole('button', { name: 'Use this take', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).shots[0].selectedTakeId).toBe(shortened.id);
  await review(page).getByRole('button', { name: 'Continue from this frame', exact: true }).click();
  await review(page).getByRole('region', { name: 'Continue from this frame' }).getByRole('button', { name: 'Add shot', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).shots.length).toBe(2);
  expect((await state(request, id)).shots[1].startFrame).toEqual({ takeId: shortened.id, frame: 24 });
});
