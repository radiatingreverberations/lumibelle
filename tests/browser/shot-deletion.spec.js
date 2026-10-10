import { shotAction, generateTakes, composeProduction, toolsTab } from './workspace-tools.js';
import { planningComposer, submitPlanning } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

async function setup(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  await request.post(`/fixtures/${id}/approved`);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Draft shots', exact: true }).click();
  const planning = page.locator('.shot-planning-dialog');
  await submitPlanning(page);
  await planning.getByRole('button', { name: 'Add reviewed shots' }).click();
  const state = async () => (await (await request.get(`/fixtures/${id}/shots`)).json());
  await expect.poll(async () => (await state()).shots.length).toBe(1);
  return { id, state };
}

test('bulk delete includes explicitly acknowledged production takes, preserves Assets, and recovers shots', async ({ page, request }) => {
  test.setTimeout(90000);
  const { id, state } = await setup(page, request);
  const assetsBefore = (await (await request.get(`/fixtures/${id}`)).json()).assets;
  await composeProduction(page);
  await generateTakes(page);
  const review = page.locator('.shot-review-dialog');
  await expect(review).toBeVisible();
  await review.getByRole('button', { name: 'Use this take', exact: true }).click();
  await expect.poll(async () => (await state()).shots[0].selectedTakeId).not.toBeNull();
  await review.getByRole('button', { name: 'Close', exact: true }).click();
  const original = (await state()).shots[0];
  await page.goto(`/projects/${id}/shots`);
  // Single deletion is no longer a disabled dead end; cancellation is harmless.
  await shotAction(page, 'Delete');
  const dialog = page.locator('.shot-delete-dialog');
  await expect(dialog.getByRole('button', { name: 'Delete 1 shot', exact: true })).toBeDisabled();
  await expect(dialog).toContainText('Production take selected');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await state()).shots[0].selectedTakeId).toBe(original.selectedTakeId);
  await shotAction(page, 'Duplicate');
  await expect.poll(async () => (await state()).shots.length).toBe(2);
  await page.getByRole('button', { name: 'Bulk operations', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Delete shots…', exact: true }).click();
  await dialog.getByRole('button', { name: 'Select all shown', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Delete 2 shots', exact: true })).toBeDisabled();
  await dialog.getByRole('checkbox', { name: 'Also clear 1 production take selection' }).check();
  await dialog.getByRole('button', { name: 'Delete 2 shots', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect.poll(async () => (await state()).shots.length).toBe(0);
  await expect(page.getByRole('button', { name: '+ Shot', exact: true })).toBeFocused();
  let saved = await state(); expect(saved.takes).toHaveLength(0); expect(saved.trash).toHaveLength(1);
  expect(saved.trash[0].take.id).toBe(original.selectedTakeId);
  expect((await (await request.get(`/fixtures/${id}`)).json()).assets).toEqual(assetsBefore);
  await page.reload();
  await page.getByRole('button', { name: 'Recovery', exact: true }).click();
  const recovery = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Shot recovery' }) });
  await recovery.getByRole('button', { name: 'Restore draft', exact: true }).first().click();
  await expect.poll(async () => (await state()).shots.length).toBe(2);
  saved = await state(); expect(saved.shots[0].id).toBe(original.id); expect(saved.shots[0].selectedTakeId).toBeNull(); expect(saved.trash).toHaveLength(1);
});

test('delete selection stays explicit after another tab changes the shots, with keyboard and mobile controls', async ({ page, request }) => {
  const { id, state } = await setup(page, request);
  const original = (await state()).shots[0];
  await page.setViewportSize({ width: 390, height: 844 });
  await page.locator('[data-toggle-pane=left]').click();
  await page.getByRole('button', { name: 'Bulk operations', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Delete shots…', exact: true }).click();
  const dialog = page.locator('.shot-delete-dialog');
  const all = dialog.getByRole('button', { name: 'Select all shown', exact: true });
  await expect(all).toBeEnabled();
  await all.press('Enter');
  await expect(dialog.getByRole('checkbox').first()).toBeChecked();
  await expect(dialog.getByRole('button', { name: 'Delete 1 shot', exact: true })).toBeEnabled();
  await page.screenshot({ path: 'artifacts/validation/shot-deletion-mobile.png' });
  expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  // This existing fixture replaces the draft from another client and adds a second shot.
  await request.post(`/fixtures/${id}/reference-workspace`);
  await dialog.getByRole('button', { name: 'Delete 1 shot', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Nothing was deleted');
  expect((await state()).shots).toHaveLength(2);
  await dialog.getByRole('button', { name: 'Review latest shots', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Delete 0 shots', exact: true })).toBeDisabled();
  expect((await state()).shots.some(s => s.id === original.id)).toBe(false);
  await all.click();
  await expect(dialog.getByRole('button', { name: 'Delete 2 shots', exact: true })).toBeEnabled();
  await dialog.getByRole('button', { name: 'Clear selection', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Delete 0 shots', exact: true })).toBeDisabled();
  await expect(dialog.getByRole('checkbox').first()).not.toBeChecked();
  await dialog.getByRole('checkbox').first().check();
  await expect(dialog.getByRole('button', { name: 'Delete 1 shot', exact: true })).toBeEnabled();
  await dialog.getByRole('button', { name: 'Delete 1 shot', exact: true }).click();
  await expect.poll(async () => (await state()).shots.length).toBe(1);
  await expect(page.locator('[data-toggle-pane=left]')).toBeFocused();
});
