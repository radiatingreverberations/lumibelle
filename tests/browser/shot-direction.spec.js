import { test, expect } from './fixtures.js';
import { openPlanning, planningComposer, submitPlanning } from './text-assistance-tools.js';

for (const width of [1440, 430]) {
  test(`scene strategy and direction survive pending review and application at ${width}px`, async ({ page, request }) => {
    const { id } = await (await request.get('/fixtures/new')).json();
    await request.post(`/fixtures/${id}/approved`);
    await page.setViewportSize({ width, height: 1000 });
    await page.goto(`/projects/${id}/shots`);
    await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
    await openPlanning(page);
    await expect(planningComposer(page).getByRole('combobox', { name: 'Direction style', exact: true })).toHaveValue('Auto');
    await expect(planningComposer(page)).toContainText('Recommended for most scenes');
    await planningComposer(page).getByRole('combobox', { name: 'Direction style', exact: true }).selectOption('PerformanceComedy');
    await expect(planningComposer(page)).toContainText('Let performances, reactions');
    await submitPlanning(page);
    const review = page.locator('.shot-planning-dialog');
    await expect(review.locator('.scene-direction-strategy')).toHaveCount(1);
    await review.getByLabel('Dramatic progression', { exact: true }).fill('Trust gives way to doubt.');
    await review.locator('.shot-direction > details > summary').first().click();
    await review.getByLabel('Purpose', { exact: true }).first().fill('Make the hesitation matter');
    const jobs = (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id);
    const job = jobs.find(j => j.kind === 'ShotPlanning');
    const pending = async () => (await (await request.get(`/fixtures/ai-jobs/${job.id}/review`)).json()).value;
    await expect.poll(async () => (await pending())?.shots[0].direction.intent.purpose).toBe('Make the hesitation matter');
    await expect.poll(async () => (await pending())?.strategies[0].dramaticProgression).toBe('Trust gives way to doubt.');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await review.getByRole('button', { name: 'Close', exact: true }).click();
    const state = async () => (await (await request.get(`/fixtures/${id}/shots`)).json());
    expect((await state()).shots).toHaveLength(0);
    await page.goto(`/projects/${id}/shots?jobId=${job.id}`);
    await expect(review).toBeVisible();
    await expect(review.getByLabel('Dramatic progression', { exact: true })).toHaveValue('Trust gives way to doubt.');
    await expect(review).toContainText('Make the hesitation matter');
    await page.screenshot({ path: `test-results/direction-scene-${width}.png`, fullPage: true, animations: 'disabled' });
    await review.getByRole('button', { name: 'Add reviewed shots', exact: true }).click();
    await expect.poll(async () => (await state()).shots.length).toBeGreaterThan(0);
    const saved = await state();
    expect(saved.directionStrategies).toHaveLength(1);
    expect(saved.directionStrategies[0].dramaticProgression).toBe('Trust gives way to doubt.');
    expect(saved.shots[0].directionStrategyId).toBe(saved.directionStrategies[0].id);
    expect(saved.shots[0].direction.intent.purpose).toBe('Make the hesitation matter');
    await expect(review).toBeHidden();
    await expect(page.locator('section.shot-direction')).not.toContainText('Needs review');
    await openPlanning(page);
    await expect(planningComposer(page).getByRole('combobox', { name: 'Direction style', exact: true })).toHaveValue('PerformanceComedy');
  });

  test(`single shot direction is reviewable, persistent and undoable at ${width}px`, async ({ page, request }) => {
    const { id } = await (await request.get('/fixtures/new')).json();
    await request.post(`/fixtures/${id}/approved`);
    await request.post(`/fixtures/${id}/production-shot`);
    await page.setViewportSize({ width, height: 1000 });
    await page.goto(`/projects/${id}/shots`);
    await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
    if (await page.getByRole('button', { name: 'Show Shots', exact: true }).isVisible())
      await page.getByRole('button', { name: 'Show Shots', exact: true }).click();
    await page.getByRole('button', { name: '+ Shot', exact: true }).click();
    if (width === 430) {
      await page.getByRole('button', { name: 'Appearance', exact: true }).click();
      await page.getByLabel(/^Dark appearance/).click();
      await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    }
    await page.locator('.shot-draft-assist').getByRole('button', { name: 'Draft this shot', exact: true }).click();
    const composer = page.locator('.ai-assist-dialog').filter({ has: page.getByRole('heading', { name: 'Draft this shot', exact: true }) });
    await composer.getByRole('combobox', { name: 'Direction style', exact: true }).selectOption('EnsembleClassical');
    await expect(composer).toContainText('Keep characters and their relationships visible');
    await composer.getByRole('button', { name: 'Draft shot', exact: true }).click();
    const review = page.locator('.shot-draft-dialog');
    await expect(review).toBeVisible({ timeout: 30000 });
    await review.locator('.shot-direction > details > summary').click();
    await review.getByLabel('Purpose', { exact: true }).fill('Protect the delayed reaction');
    const job = (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.target.projectId === id && j.target.shotId);
    await review.getByRole('button', { name: 'Close', exact: true }).click();
    await page.goto(`/projects/${id}/shots?jobId=${job.id}&shotId=${job.target.shotId}`);
    await expect(review).toContainText('Protect the delayed reaction');
    await page.screenshot({ path: `test-results/direction-single-${width}.png`, fullPage: true, animations: 'disabled' });
    await review.getByRole('button', { name: 'Apply to this shot', exact: true }).click();
    const state = async () => (await (await request.get(`/fixtures/${id}/shots`)).json());
    await expect.poll(async () => (await state()).shots.find(s => s.id === job.target.shotId)?.direction?.intent.purpose).toBe('Protect the delayed reaction');
    expect((await state()).directionStrategies).toHaveLength(1);
    await page.getByRole('button', { name: 'Undo', exact: true }).click();
    await expect.poll(async () => (await state()).shots.find(s => s.id === job.target.shotId)?.direction ?? null).toBeNull();
    expect((await state()).directionStrategies).toHaveLength(0);
  });
}

test('a changed authored target blocks a captured single-shot redraft and exposes request inspection', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: '+ Shot', exact: true }).click();
  await page.locator('.shot-draft-assist').getByRole('button', { name: 'Draft this shot', exact: true }).click();
  await page.locator('.ai-assist-dialog').getByRole('button', { name: 'Draft shot', exact: true }).click();
  const review = page.locator('.shot-draft-dialog');
  await expect(review).toBeVisible();
  await review.locator('.request-details > summary').click();
  await expect(review.locator('.submitted-input')).toContainText('contractVersion:4');
  const job = (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.target.projectId === id && j.target.shotId);
  await review.getByRole('button', { name: 'Close', exact: true }).click();
  await page.getByLabel('Action and camera', { exact: true }).fill('Keep this newer authored action.');
  const saved = async () => (await (await request.get(`/fixtures/${id}/shots`)).json()).shots.find(s => s.id === job.target.shotId);
  await expect.poll(async () => (await saved()).description).toBe('Keep this newer authored action.');
  await page.goto(`/projects/${id}/shots?jobId=${job.id}&shotId=${job.target.shotId}`);
  await expect(review).toContainText('This shot changed after the request');
  await expect(review.getByRole('button', { name: 'Apply to this shot', exact: true })).toBeDisabled();
  expect((await saved()).description).toBe('Keep this newer authored action.');
  expect((await saved()).direction ?? null).toBeNull();
});
