import { shotAction } from './workspace-tools.js';
import { planningComposer, submitPlanning, openPlanning } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

const dialog = page => page.locator('.shot-planning-dialog');
const activity = page => page.getByRole('dialog', { name: 'AI activity', exact: true });
const state = async (request, project) => (await request.get(`/fixtures/${project}/shots`)).json();
const jobs = async (request, project) => (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === project);
async function pause(page, paused) {
  await page.locator('.ai-activity-trigger').click();
  await activity(page).getByRole('button', { name: `${paused ? 'Pause' : 'Resume'} queue OpenRouter`, exact: true }).click();
  await activity(page).getByRole('button', { name: 'Close AI activity' }).click();
}
async function setup(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  const approval = await (await request.get(`/fixtures/${id}/script-source`)).json();
  const assets = await (await request.post(`/fixtures/${id}/planning-looks`)).json();
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  return { id, approval, character: assets.assets[0] };
}
async function begin(page) {
  await openPlanning(page);
  await submitPlanning(page);
}
async function openFromActivity(page, project, job) {
  await page.locator('.ai-activity-trigger').click();
  await activity(page).getByRole('tab', { name: 'History', exact: true }).click();
  await activity(page).getByLabel('Project', { exact: true }).selectOption(project);
  await activity(page).locator(`[data-job-id="${job}"]`).getByRole('link', { name: 'Review', exact: true }).click();
}
const savedLook = async (request, job) => (await (await request.get(`/fixtures/ai-jobs/${job}/review`)).json()).value?.shots?.[0]?.characters?.[0]?.appearance?.lookId;

test('planning survives navigation and restores its captured source, then applies only once', async ({ page, request }) => {
  const { id, approval } = await setup(page, request);
  await pause(page, true);
  try {
    await openPlanning(page);
    await planningComposer(page).getByLabel('Maximum shot length (seconds)').fill('6');
    await planningComposer(page).getByLabel('Directing instructions (optional)').fill('Hold on the quiet pause.');
    await submitPlanning(page);
    await expect(dialog(page)).toContainText('Queued in Lumibelle');
    await dialog(page).getByRole('button', { name: 'Close', exact: true }).click();
    await page.reload();
    await page.getByRole('button', { name: /^Queued.*View request$/ }).click();
    await dialog(page).getByText('Request details', { exact: true }).click();
    await expect(dialog(page).locator('.submitted-input')).toContainText('Hold on the quiet pause.');
    const queued = (await jobs(request, id))[0];
    const captured = await (await request.get(`/fixtures/ai-jobs/${queued.id}/text-selection`)).json();
    expect(captured.task).toMatchObject({ maximumSeconds: 6, instructions: 'Hold on the quiet pause.', script: { id: approval.id } });
    await expect(dialog(page).getByLabel('Directing instructions (optional)')).toHaveCount(0);
    await dialog(page).getByRole('button', { name: 'Close', exact: true }).click();
    await page.locator('.project-tabs').getByRole('link', { name: 'Assets', exact: true }).click();
    await request.post(`/fixtures/${id}/looks-script`);
  } finally { if (await dialog(page).isVisible()) await dialog(page).getByRole('button', { name: 'Close', exact: true }).click(); await pause(page, false); }
  await expect.poll(async () => (await jobs(request, id))[0]?.state).toBe('Completed');
  const job = (await jobs(request, id))[0]; expect(job.cancelRequested).toBe(false);
  await expect(dialog(page)).not.toBeVisible();
  await openFromActivity(page, id, job.id);
  await expect(dialog(page)).toContainText('A newer saved script revision is available');
  await expect(dialog(page)).toContainText('You called?');
  await dialog(page).getByRole('button', { name: 'Add reviewed shots' }).click();
  await expect.poll(async () => (await state(request, id)).shots.length).toBe(1);
  let saved = await state(request, id);
  expect(saved.shots[0].approvedScriptId).toBe(approval.id); expect(saved.shots[0].planning.requestedMaximum).toBe(6);
  expect(saved.planningReviews[0].jobId).toBe(job.id);
  await page.reload(); await expect(dialog(page)).toContainText('already added');
  await expect(dialog(page).getByRole('button', { name: 'Add reviewed shots' })).toBeDisabled();
  await dialog(page).getByRole('button', { name: 'Close', exact: true }).click();
  await shotAction(page, 'Delete shot');
  await page.locator('.shot-delete-dialog').getByRole('button', { name: 'Delete 1 shot', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).shots.length).toBe(0);
  await page.reload(); await expect(dialog(page)).toContainText('already added');
  expect(await jobs(request, id)).toHaveLength(1);
  expect((await state(request, id)).shots).toHaveLength(0);
});

test('reviewed cast choices survive reopening and conflicting tabs retain their local decisions', async ({ page, context, request }) => {
  const { id } = await setup(page, request); await begin(page);
  await expect(dialog(page).getByRole('button', { name: 'Add reviewed shots' })).toBeEnabled();
  const job = (await jobs(request, id))[0];
  const savedName = async () => (await (await request.get(`/fixtures/ai-jobs/${job.id}/review`)).json()).value?.shots?.[0]?.characters?.[0]?.name;
  await dialog(page).getByLabel('Character name', { exact: true }).fill('Mouse');
  await dialog(page).getByLabel('Character name', { exact: true }).blur();
  await expect.poll(savedName).toBe('Mouse');
  await dialog(page).getByRole('button', { name: 'Close', exact: true }).click();
  await page.goto(`/projects/${id}/shots?jobId=${job.id}`);
  await expect(dialog(page).getByLabel('Character name', { exact: true })).toHaveValue('Mouse');
  const other = await context.newPage(); await other.goto(`/projects/${id}/shots?jobId=${job.id}`);
  await expect(dialog(other).getByLabel('Character name', { exact: true })).toHaveValue('Mouse');
  await page.bringToFront(); await dialog(page).getByLabel('Character name', { exact: true }).fill('Demo');
  await dialog(page).getByLabel('Character name', { exact: true }).blur();
  await expect.poll(savedName).toBe('Demo');
  await other.bringToFront(); await dialog(other).getByLabel('Character name', { exact: true }).fill('Local');
  await dialog(other).getByLabel('Character name', { exact: true }).blur();
  await expect(dialog(other)).toContainText('Another tab saved different review decisions');
  await expect(dialog(other).getByLabel('Character name', { exact: true })).toHaveValue('Local');
  await dialog(other).getByRole('button', { name: 'Load saved review' }).click();
  await expect(dialog(other).getByLabel('Character name', { exact: true })).toHaveValue('Demo');
  await other.setViewportSize({ width: 390, height: 844 });
  expect(await other.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await other.close();
});

test('closing a queued breakdown does not cancel it and cancellation remains explicit', async ({ page, request }) => {
  const { id } = await setup(page, request); await pause(page, true);
  try {
    await begin(page); await expect(dialog(page)).toContainText('Queued in Lumibelle');
    await expect(dialog(page).getByRole('button', { name: 'New breakdown', exact: true })).toHaveCount(0);
    await page.keyboard.press('Escape'); await expect(dialog(page)).not.toBeVisible();
    expect((await jobs(request, id))[0].cancelRequested).toBe(false);
    await page.getByRole('button', { name: /^Queued.*View request$/ }).click();
    await dialog(page).getByRole('button', { name: 'Cancel request' }).click();
    await expect.poll(async () => (await jobs(request, id))[0].state).toBe('Cancelled');
    await expect(dialog(page).getByRole('button', { name: 'Add reviewed shots' })).toBeDisabled();
    expect((await state(request, id)).shots).toHaveLength(0);
    await dialog(page).getByRole('button', { name: 'Close', exact: true }).click();
    await expect(dialog(page)).not.toBeVisible();
  } finally { if (await dialog(page).isVisible()) await dialog(page).getByRole('button', { name: 'Close', exact: true }).click(); await pause(page, false); }
});


test('deleted shots can be drafted from the latest saved script despite an older failed breakdown', async ({ page, request }) => {
  const { id } = await setup(page, request);
  await openPlanning(page);
  await planningComposer(page).getByLabel('Directing instructions (optional)').fill('TRUNCATED');
  await submitPlanning(page);
  await expect(dialog(page)).toContainText('incomplete output cannot be applied');
  const failed = (await jobs(request, id))[0];
  await dialog(page).getByRole('button', { name: 'New breakdown', exact: true }).click();
  await planningComposer(page).getByLabel('Directing instructions (optional)').fill('Old directing notes');
  await submitPlanning(page);
  await dialog(page).getByRole('button', { name: 'Add reviewed shots', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).shots.length).toBe(1);
  await shotAction(page, 'Delete shot');
  await page.locator('.shot-delete-dialog').getByRole('button', { name: 'Delete 1 shot', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).shots.length).toBe(0);
  await request.post(`/fixtures/${id}/looks-script`);
  const latest = await (await request.get(`/fixtures/${id}/script-source`)).json();
  await page.reload();
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await openPlanning(page);
  await expect(dialog(page)).not.toBeVisible();
  await expect(planningComposer(page)).not.toContainText('captured source');
  await expect(planningComposer(page).getByLabel('Directing instructions (optional)')).toHaveValue('');
  await expect(planningComposer(page).getByRole('checkbox', { name: 'INT. BEDROOM — NIGHT', exact: true })).toBeChecked();
  await submitPlanning(page);
  await dialog(page).getByRole('button', { name: 'Add reviewed shots', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).shots.length).toBe(1);
  expect((await state(request, id)).shots[0].approvedScriptId).toBe(latest.id);
  const history = await jobs(request, id);
  expect(history).toHaveLength(3);
  expect(history.find(j => j.id === failed.id).state).toBe('NeedsAttention');
});

test('an old review offers a new breakdown and clears its job link without submitting automatically', async ({ page, request }) => {
  const { id } = await setup(page, request);
  await openPlanning(page);
  await planningComposer(page).getByLabel('Directing instructions (optional)').fill('TRUNCATED');
  await planningComposer(page).getByLabel('Maximum shot length (seconds)').fill('6');
  await submitPlanning(page);
  await expect(dialog(page)).toContainText('incomplete output cannot be applied');
  const failed = (await jobs(request, id))[0];
  await request.post(`/fixtures/${id}/looks-script`);
  await page.goto(`/projects/${id}/shots?jobId=${failed.id}&view=grid#focus`);
  await expect(dialog(page)).toContainText('A newer saved script revision is available');
  await page.setViewportSize({ width: 390, height: 844 });
  const fresh = dialog(page).getByRole('button', { name: 'New breakdown', exact: true });
  await fresh.focus(); await page.keyboard.press('Enter');
  await expect(planningComposer(page).getByRole('button', { name: 'Draft shots', exact: true })).toBeEnabled();
  await expect(planningComposer(page)).not.toContainText('NeedsAttention');
  await expect(planningComposer(page)).not.toContainText('captured approval');
  await expect(planningComposer(page).getByLabel('Directing instructions (optional)')).toHaveValue('');
  await expect(planningComposer(page).getByLabel('Maximum shot length (seconds)')).toHaveValue('15');
  await expect(planningComposer(page).getByRole('checkbox', { name: 'INT. BEDROOM — NIGHT', exact: true })).toBeChecked();
  const { slug } = await (await request.get(`/fixtures/${id}/project-route`)).json();
  await expect(page).toHaveURL(`/projects/${slug}/shots?view=grid#focus`);
  expect(await jobs(request, id)).toHaveLength(1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'artifacts/validation/new-shot-breakdown-mobile.png' });
  await planningComposer(page).getByRole('button', { name: 'Close', exact: true }).click();
  await page.reload();
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await expect(dialog(page)).not.toBeVisible();
});

test('recent directing instructions can be reused in a new breakdown', async ({ page, request }) => {
  await setup(page, request);
  const instructions = 'Alternate each shot between her stream camera and a phone camera held by the Figure.';
  await openPlanning(page);
  await planningComposer(page).getByLabel('Directing instructions (optional)').fill(instructions);
  await submitPlanning(page);
  await dialog(page).getByRole('button', { name: 'New breakdown', exact: true }).click();
  const field = planningComposer(page).getByLabel('Directing instructions (optional)');
  await expect(field).toHaveValue('');
  const recent = planningComposer(page).getByRole('group', { name: 'Recent directing instructions', exact: true });
  await recent.getByRole('button', { name: instructions, exact: true }).click();
  await expect(field).toHaveValue(instructions);
});
