import { generateTakes, composeProduction, closeShotSetup, shotAction } from './workspace-tools.js';
import { planningComposer, submitPlanning } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

async function setup(page, request) {
  const project = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${project.id}/images`);
  await request.post(`/fixtures/${project.id}/approved`);
  await page.goto(`/projects/${project.id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Draft shots', exact: true }).click();
  const plan = page.locator('.shot-planning-dialog');
  await expect(planningComposer(page).getByRole('button', { name: 'Draft shots', exact: true })).toBeEnabled();
  await submitPlanning(page);
  await expect(plan.getByRole('button', { name: 'Add reviewed shots' })).toBeEnabled();
  await plan.getByRole('button', { name: 'Add reviewed shots' }).click();
  await expect(page.getByLabel('Action and camera')).toBeVisible();
  return project;
}
const shots = async (request, project) => (await (await request.get(`/fixtures/${project.id}/shots`)).json());

test('paused-frame extension captures ordered motion stills and supports both continuation destinations', async ({ page, request }) => {
  test.setTimeout(150000);
  const project = await setup(page, request);
  await composeProduction(page); await generateTakes(page);
  const review = page.locator('.shot-review-dialog');
  await expect(review).toBeVisible({ timeout: 20000 });
  const source = (await shots(request, project)).takes[0];
  const position = review.getByRole('slider', { name: 'Video position' });
  await position.press('End');
  await expect(review.locator('.take-player')).toHaveAttribute('data-frame-index', '38');
  await review.getByRole('button', { name: 'Continue from this frame', exact: true }).click();
  const form = review.getByRole('region', { name: 'Extend take', exact: true });
  await expect(form).toContainText('Retain frames 1–39 of 39');
  await expect(form.getByLabel('Next action', { exact: true })).toHaveValue('');
  await expect(form.getByLabel('New dialogue', { exact: true })).toHaveValue('');
  await form.getByLabel('Next action', { exact: true }).fill('She looks up from the doorway and smiles.');
  await form.getByLabel('Added duration', { exact: true }).fill('1');
  const before = (await (await request.get('/fixtures/compositions')).json()).length;
  await form.getByRole('button', { name: 'Text model options', exact: true }).click();
  await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await page.locator('.ai-assist-dialog').last().getByRole('button', { name: 'Close', exact: true }).click();
  await form.getByRole('button', { name: 'Compose extension prompt', exact: true }).click();
  await expect.poll(async () => (await (await request.get('/fixtures/compositions')).json()).length).toBe(before + 1);
  await expect(form.getByRole('button', { name: 'Queue extension', exact: true })).toBeEnabled();
  const composed = (await (await request.get('/fixtures/compositions')).json()).at(-1);
  expect(composed.context.cutContinuity.transition).toBe('continuous_from_motion_window');
  expect(composed.context.precedingAction).toBe(source.snapshot.shot.description);
  expect(composed.context.shot.dialogue).toEqual([]);
  expect(composed.context.motionStillsInTimeOrder).toHaveLength(3);
  expect(composed.images.length).toBe(composed.context.references.length + 3);
  await expect(form.getByLabel('Extension prompt', { exact: true })).not.toHaveValue('');
  await form.getByLabel('Separate continuation shot', { exact: true }).check();
  await expect(form.getByLabel('Continuation destination')).toHaveValue('');
  await form.getByRole('button', { name: 'Queue extension', exact: true }).click();
  await expect.poll(async () => (await shots(request, project)).takes.filter(t => t.composition).length, { timeout: 45000 }).toBe(1);
  let doc = await shots(request, project); const next = doc.shots[1];
  expect(doc.shots).toHaveLength(2); expect(next.startFrame ?? null).toBeNull();
  const separate = doc.takes.find(t => t.composition);
  expect(separate.shotId).toBe(next.id); expect(separate.composition.segments).toHaveLength(1);
  expect(doc.shots[0].selectedTakeId).toBeNull(); expect(next.selectedTakeId).toBeNull();
  await expect(review.locator('video')).toHaveAttribute('src', `/media/projects/${project.id}/takes/${separate.id}`);
  await review.getByRole('button', { name: 'Preview join', exact: true }).click();
  const preview = review.locator('video[aria-label="Join preview"]');
  await expect(preview).toBeVisible();
  expect((await request.get(`/media/projects/${project.id}/takes/${separate.id}/join-preview`)).ok()).toBe(true);
  await review.getByRole('button', { name: 'Close join preview', exact: true }).click();
  await review.getByRole('slider', { name: 'Video position' }).press('End');
  await review.getByRole('button', { name: 'Continue from this frame', exact: true }).click();
  await form.getByLabel('Next action', { exact: true }).fill('She carries on walking.');
  await form.getByLabel('Added duration', { exact: true }).fill('1');
  await form.getByLabel('Separate continuation shot', { exact: true }).check();
  await form.getByLabel('Continuation destination').selectOption(doc.shots[0].id);
  await form.getByRole('button', { name: 'Queue extension', exact: true }).click();
  await expect.poll(async () => (await shots(request, project)).takes.filter(t => t.composition).length, { timeout: 45000 }).toBe(2);
  doc = await shots(request, project);
  expect(doc.shots).toHaveLength(2);
  expect(doc.takes.filter(t => t.composition && t.shotId === doc.shots[0].id)).toHaveLength(1);
  expect(doc.shots[0].startFrame ?? null).toBeNull();
});

test('copying references from the previous shot adds its production take\'s last frame as a continuity picture', async ({ page, request }) => {
  test.setTimeout(120000);
  const project = await setup(page, request);
  await composeProduction(page);
  await generateTakes(page);
  const review = page.locator('.shot-review-dialog');
  await expect(review).toBeVisible({ timeout: 20000 });
  await review.getByRole('button', { name: 'Use this take', exact: true }).first().click();
  await expect.poll(async () => (await shots(request, project)).shots[0].selectedTakeId ?? null).not.toBeNull();
  await review.getByRole('button', { name: 'Close take review', exact: true }).click();
  await expect(review).toBeHidden();
  const { shots: [source], takes } = await shots(request, project);
  const take = takes.find(t => t.id === source.selectedTakeId);

  await shotAction(page, 'Duplicate');
  await expect.poll(async () => (await shots(request, project)).shots.length).toBe(2);
  const picker = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Manage references', exact: true }) });
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  // The last frame can be added on its own, after the images, without copying the other references.
  await picker.getByRole('button', { name: `Add the last frame of ${source.title}`, exact: true }).click();
  await expect(picker.getByRole('group', { name: 'Continuity picture' })).toContainText(`${source.title} · last frame`);
  await picker.getByRole('button', { name: 'Remove the continuity picture', exact: true }).click();
  await expect(picker.getByRole('group', { name: 'Continuity picture' })).toHaveCount(0);
  // The previous shot comes first, numbered as in the shot list; it is not repeated among the earlier shots.
  const copyFrom = picker.getByLabel('Copy references from');
  await expect(copyFrom.locator('option[value=previous]')).toHaveText(`Previous shot · 01 · ${source.title}`);
  await expect(copyFrom.locator('optgroup')).toHaveCount(0);
  await copyFrom.selectOption('previous');
  await expect(picker.getByRole('group', { name: 'Continuity picture' })).toHaveCount(0);
  await picker.getByRole('button', { name: 'Copy references', exact: true }).click();
  const continuity = picker.getByRole('group', { name: 'Continuity picture' });
  await expect(continuity).toContainText(`${source.title} · last frame`);
  await expect(continuity.locator('img')).toHaveAttribute('src', `/media/projects/${project.id}/takes/${take.id}/frames/38`);
  await expect(picker).toContainText('added as a continuity picture');
  await picker.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(picker).toBeHidden();
  await expect(page.locator('.shot-reference-tile').filter({ hasText: `${source.title} · last frame` })).toBeVisible();
  const copy = (await shots(request, project)).shots[1];
  await expect.poll(async () => (await (await request.get(`/fixtures/${project.id}/production`)).json()).shotContent?.find(c => c.shotId === copy.id)?.continuityFrame ?? null)
    .toMatchObject({ takeId: take.id, frame: 38 });

  // The composer sees it as one more Picture, numbered after the images.
  const before = (await (await request.get('/fixtures/compositions')).json()).length;
  await composeProduction(page);
  const compositions = await (await request.get('/fixtures/compositions')).json();
  expect(compositions.length).toBe(before + 1);
  const composed = compositions.at(-1);
  expect(composed.context.continuityPicture.picture).toBe(composed.context.references.length + 1);
  expect(composed.images.length).toBe(composed.context.references.length + 1);
});
