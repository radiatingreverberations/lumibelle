import { compositionState, editShotReference, openReferenceCrop, toolsTab } from './workspace-tools.js';
import { planningComposer, submitPlanning } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

test('a free crop drags its edges and corners independently and reopens free', async ({ page, request }) => {
  test.setTimeout(90000);
  await page.setViewportSize({ width: 1440, height: 1000 });
  const project = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${project.id}/images`);
  await request.post(`/fixtures/${project.id}/approved`);
  await page.goto(`/projects/${project.id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Draft shots', exact: true }).click();
  await expect(planningComposer(page).getByRole('button', { name: 'Draft shots', exact: true })).toBeEnabled();
  await submitPlanning(page);
  await page.locator('.shot-planning-dialog').getByRole('button', { name: 'Add reviewed shots' }).click();
  await expect(page.getByLabel('Action and camera')).toBeVisible();
  await toolsTab(page, 'References');
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  await page.locator('.project-image-picker .picker-grid button').first().click();
  await page.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await editShotReference(page);
  const manager = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Manage references' }) });

  let crop = await openReferenceCrop(page, manager);
  const stage = crop.locator('.crop-stage'), selection = crop.locator('.crop-selection');
  await expect(crop.locator('[data-crop-handle=top-left]')).toHaveCount(0);
  await crop.getByLabel('Crop shape').selectOption('Free');
  await expect(stage).toHaveAttribute('data-free', 'true');
  const drag = async (handle, dx, dy) => {
    const box = await crop.locator(`[data-crop-handle=${handle}]`).boundingBox();
    const x = box.x + box.width / 2, y = box.y + box.height / 2;
    await page.mouse.move(x, y); await page.mouse.down();
    await page.mouse.move(x + dx / 2, y + dy / 2); await page.mouse.move(x + dx, y + dy); await page.mouse.up();
  };
  // Measure once the dialog has finished opening; it scales in.
  let bounds;
  await expect.poll(async () => { const last = bounds; bounds = await stage.boundingBox(); return JSON.stringify(last) === JSON.stringify(bounds); }).toBe(true);
  // The corner moves two edges; an edge moves only itself.
  await drag('bottom-right', -bounds.width * .4, -bounds.height * .2);
  await drag('left', bounds.width * .1, 0);
  await expect.poll(async () => Number(await selection.getAttribute('data-x'))).toBeCloseTo(.1, 1);
  const region = await selection.evaluate(el => ({ x: +el.dataset.x, y: +el.dataset.y, width: +el.dataset.width, height: +el.dataset.height }));
  expect(region.y).toBe(0);
  expect(region.width).toBeCloseTo(.5, 1); expect(region.height).toBeCloseTo(.8, 1);
  await page.screenshot({ path: 'artifacts/free-crop.png' });
  await crop.getByRole('button', { name: 'Apply crop', exact: true }).click();
  await expect(crop).toBeHidden();
  await manager.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect.poll(async () => (await compositionState(request, project.id)).shots[0].images[0]?.crop?.height ?? 1).toBeCloseTo(.8, 1);

  // Reopened, the crop is still free, and its pixels can be typed.
  await editShotReference(page);
  crop = await openReferenceCrop(page, manager);
  await expect(crop.getByLabel('Crop shape')).toHaveValue('Free');
  await crop.getByText('Precise crop controls', { exact: true }).click();
  await crop.getByRole('spinbutton', { name: 'Crop top', exact: true }).fill('10');
  await crop.getByRole('spinbutton', { name: 'Crop top', exact: true }).press('Tab');
  await expect.poll(async () => Number(await selection.getAttribute('data-y'))).toBeGreaterThan(0);
  await crop.getByRole('button', { name: 'Cancel', exact: true }).click();
});
