import { openShotSetup, shotAction } from './workspace-tools.js';
import { planningComposer, submitPlanning, openPlanning, openShotsAfterPlanning } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

test('scene text and the neighbouring shots show their size and can be left out separately', async ({ page, request }) => {
  test.setTimeout(90000);
  const project = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${project.id}/images`);
  await request.post(`/fixtures/${project.id}/approved`);
  await page.goto(`/projects/${project.id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await openPlanning(page);
  const plan = page.locator('.shot-planning-dialog');
  await expect(planningComposer(page).getByRole('button', { name: 'Draft shots', exact: true })).toBeEnabled();
  await submitPlanning(page);
  await plan.getByRole('button', { name: 'Add reviewed shots' }).click();
  await openShotsAfterPlanning(page);
  await expect(page.getByLabel('Action and camera')).toBeVisible();
  // Without a prompt, the highlighted Prompt step is the hint; no sentence repeats it under the button.
  const footer = page.locator('.shot-controls .workspace-pane-footer');
  await expect(footer.getByRole('button', { name: 'Prompt', exact: true })).toHaveClass(/next-step/);
  await expect(footer.getByRole('status')).toHaveCount(0);
  const composer = page.locator('.ai-assist-dialog').filter({ has: page.getByLabel('Direction for AI', { exact: true }) });
  const scene = composer.getByRole('checkbox', { name: 'Include scene text' });
  const shots = composer.getByRole('checkbox', { name: 'Include the shots before and after' });

  // A lone shot has no neighbours to send.
  await openShotSetup(page);
  // With no prompt yet, composing one leads: the guidance says so, Assist reads Compose prompt and stands out.
  const setup = page.locator('.shot-setup-dialog');
  await expect(setup).toContainText('No prompt yet. Choose Compose prompt');
  await expect(setup.locator('.assist-emphasis')).toContainText('Compose prompt');
  await expect(setup.getByRole('button', { name: 'Copy prompt', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Prompt assistance', exact: true }).click();
  await expect(scene).toBeChecked(); await expect(shots).toBeChecked();
  const currentPrompt = composer.getByRole('checkbox', { name: 'Include current prompt' });
  await expect(currentPrompt).not.toBeChecked(); await expect(currentPrompt).toBeDisabled();
  await expect(composer.locator('[data-context=prompt]')).toHaveText('No current prompt');
  await expect(composer.locator('[data-context=scene]')).toHaveText(/^≈[\d,]+ tokens$/);
  await expect(composer.locator('[data-context=shots]')).toHaveText('none in this scene');
  // References are always sent, so their row shows only what they cost.
  const references = composer.getByRole('checkbox', { name: /^References/ });
  await expect(references).toBeChecked(); await expect(references).toBeDisabled();
  await expect(composer.locator('[data-context=references]')).toHaveText(/^(no images|≈[\d,]+ tokens)$/);
  // Each box sits on the middle of its one-line label (measured once the dialog has finished opening).
  for (const row of await composer.locator('.composition-context .check-row').all())
    await expect.poll(() => row.evaluate(r => {
      const box = r.querySelector('input').getBoundingClientRect(), label = r.querySelector('span').getBoundingClientRect();
      return Math.round(Math.abs((box.top + box.height / 2) - (label.top + label.height / 2)));
    })).toBeLessThan(3);
  await composer.getByRole('button', { name: 'Close', exact: true }).click();

  await shotAction(page, 'Duplicate');
  await expect(page.locator('.shot-outline-item.selected')).toContainText('(copy)');
  await openShotSetup(page);
  await page.getByRole('button', { name: 'Prompt assistance', exact: true }).click();
  await expect(composer.locator('[data-context=shots]')).toHaveText(/^≈[\d,]+ tokens$/);
  await scene.uncheck();
  const before = (await (await request.get('/fixtures/compositions')).json()).length;
  await composer.getByRole('button', { name: 'Compose prompt', exact: true }).click();
  await expect(composer).toBeHidden();
  await expect.poll(async () => (await (await request.get('/fixtures/compositions')).json()).length).toBe(before + 1);
  const { context } = (await (await request.get('/fixtures/compositions')).json()).at(-1);
  expect(context.sceneContext).toBe('');
  expect(context.currentPrompt).toBe('');
  expect(context.nearbyShots.length).toBe(1);
  expect(context.nearbyShots[0]).toMatch(/^Previous shot/);
});

for (const include of [false, true]) test(`current prompt reuse is explicit and captured (${include ? 'refine' : 'fresh'})`, async ({ page, request }, testInfo) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await openShotSetup(page);
  const previous = 'An earlier prompt with an obsolete <Audio 1> reference.';
  const editor = page.getByLabel('H3 prompt', { exact: true });
  await editor.fill(previous); await editor.blur();
  const saved = async () => (await (await request.get(`/fixtures/${id}/production`)).json()).compositions[0].prompt;
  await expect.poll(saved).toBe(previous);
  await page.getByRole('button', { name: 'Prompt assistance', exact: true }).click();
  const composer = page.locator('.ai-assist-dialog').filter({ has: page.getByLabel('Direction for AI', { exact: true }) });
  const currentPrompt = composer.getByRole('checkbox', { name: 'Include current prompt' });
  await expect(currentPrompt).toBeEnabled(); await expect(currentPrompt).not.toBeChecked();
  await expect(composer.locator('[data-context=prompt]')).toHaveText(/^≈[\d,]+ tokens$/);
  if (include) await currentPrompt.check();
  await composer.screenshot({ path: testInfo.outputPath('composition-context.png') });
  const before = (await (await request.get('/fixtures/compositions')).json()).length;
  await composer.getByRole('button', { name: include ? 'Revise with AI' : 'Compose prompt', exact: true }).click();
  await expect.poll(async () => (await (await request.get('/fixtures/compositions')).json()).length).toBe(before + 1);
  const { context } = (await (await request.get('/fixtures/compositions')).json()).at(-1);
  expect(context.currentPrompt).toBe(include ? previous : '');
  expect(await saved()).toBe(previous);
});
