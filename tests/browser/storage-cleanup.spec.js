import { test, expect } from './fixtures.js';

async function fixture(request, count = 2) {
  const project = await (await request.get('/fixtures/new')).json();
  const response = await request.post(`/fixtures/${project.id}/archives?count=${count}`);
  expect(response.ok()).toBeTruthy();
  return { project, doc: await response.json() };
}
const state = async (request, id) => (await request.get(`/fixtures/${id}/shots`)).json();

test('archive cleanup confirms exact selections and preserves videos, provenance and unselected takes', async ({ page, request }) => {
  const first = await fixture(request); const second = await fixture(request);
  await page.goto('/trash');
  await expect(page.locator('.trash-toolbar')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('link', { name: 'Lossless archives', exact: true }).click();
  await expect(page.locator('.archive-cleanup')).toHaveAttribute('data-interactive', 'true');
  await page.getByLabel('Archive project', { exact: true }).selectOption(first.project.id);
  await expect(page.locator('.archive-cleanup-row')).toHaveCount(2);
  const row = page.locator('.archive-cleanup-row').filter({ hasText: 'Take 1' });
  await row.getByRole('checkbox').focus(); await page.keyboard.press('Space');
  await page.getByRole('button', { name: 'Remove selected archives (1)', exact: true }).click();
  const confirm = page.locator('.archive-cleanup-confirm');
  await expect(confirm).toContainText('This cannot be undone');
  await expect(confirm).toContainText('approximately');
  await expect(confirm.getByRole('button', { name: 'Cancel', exact: true })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(confirm).toHaveCount(0);
  expect((await state(request, first.project.id)).takes[0].frameArchiveRemoval).toBeUndefined();
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: 'test-results/storage-cleanup-mobile.png', fullPage: true });
  await page.getByRole('button', { name: 'Remove selected archives (1)', exact: true }).click();
  await confirm.getByRole('button', { name: 'Remove permanently' }).click();
  await expect(page.locator('.archive-cleanup-row')).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Refresh archives' })).toBeFocused();
  const doc = await state(request, first.project.id); const removed = doc.takes.find(t => t.candidate === 1);
  expect(removed.frameArchiveRemoval.completedUtc).toBeTruthy(); expect(removed.frames).toHaveLength(0);
  expect(removed.snapshot).toEqual(first.doc.takes.find(t => t.candidate === 1).snapshot);
  expect(doc.takePublications).toEqual(first.doc.takePublications);
  expect(doc.takes.find(t => t.candidate === 2).frames).toHaveLength(39);
  expect((await state(request, second.project.id)).takes.every(t => t.frames.length === 39)).toBeTruthy();
  await page.reload(); await expect(page.locator('.archive-cleanup')).toHaveAttribute('data-interactive', 'true');
  await page.getByLabel('Archive project').selectOption(first.project.id);
  await expect(page.locator('.archive-cleanup-row')).toHaveCount(1);
  // Media endpoints resolve the current manifest and extract the requested frame from MP4.
  const frame = await request.get(`/media/projects/${first.project.id}/takes/${removed.id}/frames/5`);
  expect(frame.ok()).toBeTruthy(); expect(frame.headers()['content-type']).toContain('image/png');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({ path: 'test-results/storage-cleanup-desktop.png', fullPage: true });
});

test('select all includes every page in the selected project only', async ({ page, request }) => {
  test.setTimeout(90000);
  const first = await fixture(request, 26); const other = await fixture(request, 1);
  await page.goto('/trash?view=archives');
  await expect(page.locator('.archive-cleanup')).toHaveAttribute('data-interactive', 'true');
  await page.getByLabel('Archive project').selectOption(first.project.id);
  await expect(page.locator('.archive-cleanup-row')).toHaveCount(24);
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(page.locator('.archive-cleanup-row')).toHaveCount(2);
  await page.getByRole('button', { name: 'Select all in this project scope', exact: true }).click();
  await page.getByRole('button', { name: 'Remove selected archives (26)', exact: true }).click();
  await page.locator('.archive-cleanup-confirm').getByRole('button', { name: 'Remove permanently' }).click();
  await expect(page.getByText('No lossless archives to remove', { exact: true })).toBeVisible({ timeout: 45000 });
  expect((await state(request, first.project.id)).takes.every(t => t.frameArchiveRemoval?.completedUtc)).toBeTruthy();
  expect((await state(request, other.project.id)).takes[0].frames).toHaveLength(39);
});
