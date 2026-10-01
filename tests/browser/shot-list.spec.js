import { test, expect } from './fixtures.js';

// Three shots in one scene, numbered by their saved order.
async function threeShots(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  for (let i = 0; i < 3; i++) await request.post(`/fixtures/${id}/production-shot`);
  const ids = (await (await request.get(`/fixtures/${id}/shots`)).json()).shots.map(s => s.id);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await expect(page.locator('.shot-list [data-shot-row]')).toHaveCount(3);
  const order = async () => (await (await request.get(`/fixtures/${id}/shots`)).json()).shots.map(s => s.id);
  return { id, ids, order };
}
const row = (page, shotId) => page.locator(`[data-shot-row="${shotId}"]`);

test('shots reorder within their scene by drag, row menu and Move to…, with Undo', async ({ page, request }) => {
  const { ids, order } = await threeShots(page, request);
  // Drag the third shot above the first by its handle.
  const handle = row(page, ids[2]).locator('[data-shot-drag]');
  const target = await row(page, ids[0]).boundingBox();
  const from = await handle.boundingBox();
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2); await page.mouse.down();
  await page.mouse.move(from.x + from.width / 2, target.y + 12, { steps: 8 });
  await expect(row(page, ids[0])).toHaveClass(/shot-drop-before/);
  await page.mouse.up();
  await expect.poll(order).toEqual([ids[2], ids[0], ids[1]]);
  await page.screenshot({ path: 'test-results/shot-list-dragged.png' });

  // Row menu: move the now-first shot down one place.
  await page.getByRole('button', { name: /^Actions for shot 1:/ }).click();
  await page.getByRole('menuitem', { name: 'Move down', exact: true }).click();
  await expect.poll(order).toEqual([ids[0], ids[2], ids[1]]);
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect.poll(order).toEqual([ids[2], ids[0], ids[1]]);

  // A plain click on the handle opens Move to… for keyboard and precise moves.
  await row(page, ids[2]).locator('[data-shot-drag]').click();
  const dialog = page.getByRole('dialog').filter({ hasText: 'Move shot' });
  await dialog.getByLabel('Position').selectOption('after');
  await dialog.getByLabel('Destination shot').selectOption(ids[1]);
  await dialog.getByRole('button', { name: 'Move', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect.poll(order).toEqual([ids[0], ids[1], ids[2]]);
});

test('scenes collapse to their heading and stay collapsed after a reload', async ({ page, request }) => {
  const { ids } = await threeShots(page, request);
  const toggle = page.locator('.shot-scene-toggle').first();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  await expect(toggle).toContainText('3');
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(row(page, ids[0])).toHaveCount(0);
  await page.reload();
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await expect(page.locator('.shot-scene-toggle').first()).toHaveAttribute('aria-expanded', 'false');
  await page.locator('.shot-scene-toggle').first().click();
  await expect(page.locator('.shot-list [data-shot-row]')).toHaveCount(3);
});
