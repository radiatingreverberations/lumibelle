import { generateTakes, composeProduction, closeShotSetup } from './workspace-tools.js';
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

test('a paused take frame starts a new shot that opens on it, and its takes and prompt receive that frame', async ({ page, request }) => {
  test.setTimeout(120000);
  const project = await setup(page, request);
  await composeProduction(page);
  await generateTakes(page);
  const review = page.locator('.shot-review-dialog');
  await expect(review).toBeVisible({ timeout: 20000 });
  const position = review.getByRole('slider', { name: 'Video position' });
  await position.focus(); await page.keyboard.press('End');
  await expect(review.locator('.take-player')).toHaveAttribute('data-frame-index', '38');
  // Details describe the take; the frame actions sit with the player.
  await expect(review.getByRole('region', { name: 'Take details' })).toContainText('Download MP4');
  await review.getByRole('button', { name: 'Continue from this frame', exact: true }).click();
  await expect(review).toBeHidden();

  await expect.poll(async () => (await shots(request, project)).shots.length).toBe(2);
  const [source, next] = (await shots(request, project)).shots;
  const take = (await shots(request, project)).takes.find(t => t.shotId === source.id);
  expect(next.title).toBe(`${source.title} (cont.)`);
  expect(next.sceneId).toBe(source.sceneId);
  expect(next.startFrame).toEqual({ takeId: take.id, frame: 38 });
  expect(next.description).toBe('');

  const card = page.getByRole('group', { name: 'Starts from' });
  await expect(card).toContainText(`${source.title} · Take 1 · frame 39 of 39`);
  await expect(card.locator('img')).toHaveAttribute('src', `/media/projects/${project.id}/takes/${take.id}/frames/38`);
  await expect(page.locator('.shot-outline-item.selected')).toContainText('(cont.)');

  // The prompt is composed seeing the frame after the references, and the take is generated from it.
  await page.getByLabel('Duration (seconds)').fill('1'); await page.getByLabel('Duration (seconds)').blur();
  await page.getByLabel('Action and camera').fill('She looks up from the doorway and smiles.'); await page.getByLabel('Action and camera').blur();
  const before = (await (await request.get('/fixtures/compositions')).json()).length;
  await composeProduction(page);
  const compositions = await (await request.get('/fixtures/compositions')).json();
  expect(compositions.length).toBe(before + 1);
  const composed = compositions.at(-1);
  expect(composed.context.cutContinuity.transition).toBe('continuous_from_opening_frame');
  expect(composed.images.length).toBe(composed.context.references.length + 1);
  await closeShotSetup(page);
  await generateTakes(page);
  await expect(review).toBeVisible({ timeout: 20000 });
  await expect(review.locator('.take-started-from')).toContainText(`Started from ${source.title} · Take 1 · frame 39 of 39`);
  const runs = await (await request.get(`/fixtures/${project.id}/video-runs`)).json();
  const run = runs.find(r => r.snapshot.shot.id === next.id);
  expect(run.inputs.at(-1).fileName).toBe('start-frame.png');
  await review.getByRole('button', { name: 'Close take review', exact: true }).click();
  await expect(review).toBeHidden();

  // Removing it makes the shot cut in again.
  await page.getByRole('button', { name: 'Remove the starting frame', exact: true }).click();
  await expect(card).toBeHidden();
  await expect.poll(async () => (await shots(request, project)).shots[1].startFrame ?? null).toBeNull();
});
