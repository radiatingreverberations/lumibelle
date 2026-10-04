import { openShotSetup, shotAction } from './workspace-tools.js';
import { planningComposer, submitPlanning } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

test('scene text and the neighbouring shots show their size and can be left out separately', async ({ page, request }) => {
  test.setTimeout(90000);
  const project = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${project.id}/images`);
  await request.post(`/fixtures/${project.id}/approved`);
  await page.goto(`/projects/${project.id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Draft shots', exact: true }).click();
  const plan = page.locator('.shot-planning-dialog');
  await expect(planningComposer(page).getByRole('button', { name: 'Draft shots', exact: true })).toBeEnabled();
  await submitPlanning(page);
  await plan.getByRole('button', { name: 'Add reviewed shots' }).click();
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
  await page.getByRole('button', { name: 'Prompt assistance', exact: true }).click();
  await expect(scene).toBeChecked(); await expect(shots).toBeChecked();
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
  expect(context.nearbyShots.length).toBe(1);
  expect(context.nearbyShots[0]).toMatch(/^Previous shot/);
});
