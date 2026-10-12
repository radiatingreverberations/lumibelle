import { test, expect } from './fixtures.js';
import { toolsTab, closeShotSetup } from './workspace-tools.js';

test('review distinguishes the chosen take from the take being viewed', async ({ page, request }) => {
  test.setTimeout(120000);
  await page.setViewportSize({ width: 1440, height: 1000 });
  const { id } = await (await request.get('/fixtures/new')).json();
  for (const fixture of ['approved', 'production-shot', 'take-generation-setup']) {
    expect((await request.post(`/fixtures/${id}/${fixture}`)).ok()).toBe(true);
  }
  const state = async () => (await request.get(`/fixtures/${id}/shots`)).json();
  await page.goto(`/projects/${id}/shots`);
  await toolsTab(page, 'Generate');
  await closeShotSetup(page);
  // This fixture uses the isolated BrowserHost's mock generator.
  await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
  await expect.poll(async () => (await state()).takes.length, { timeout: 45000 }).toBe(2);
  const review = page.locator('.shot-review-dialog');
  const cards = review.locator('.take-review-sidebar .shot-candidate');
  const first = cards.nth(0), second = cards.nth(1);
  const firstId = await first.locator('[data-shot-take]').getAttribute('data-shot-take');
  await first.getByRole('button', { name: 'Use this take', exact: true }).click();
  await second.locator('[data-shot-take]').click();
  await expect(first.locator('.take-selected-badge')).toHaveText('Selected');
  await expect(first.locator('.take-viewing-badge')).toHaveCount(0);
  await expect(second.locator('.take-viewing-badge')).toHaveText('Viewing');
  await expect(second.locator('.take-selected-badge')).toHaveCount(0);
  await expect(first.getByRole('button', { name: 'Discard', exact: true })).toBeDisabled();
  await expect(second.getByRole('button', { name: 'Discard', exact: true })).toBeEnabled();
  expect((await state()).shots[0].selectedTakeId).toBe(firstId);
  const colors = await second.evaluate(card => {
    const style = selector => getComputedStyle(card.querySelector(selector));
    return { preview: style('[data-shot-take]').borderColor, action: style('button:nth-of-type(2)').backgroundColor };
  });
  expect(colors.preview).not.toBe(await first.locator('[data-shot-take]').evaluate(el => getComputedStyle(el).borderColor));
  expect(colors.action).toBe(await first.getByRole('button', { name: 'Deselect take', exact: true }).evaluate(el => getComputedStyle(el).backgroundColor));
  await page.screenshot({ path: 'artifacts/take-selection-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await second.scrollIntoViewIfNeeded();
  expect(await review.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  const firstAction = await first.getByRole('button', { name: 'Deselect take', exact: true }).boundingBox();
  const secondAction = await second.getByRole('button', { name: 'Use this take', exact: true }).boundingBox();
  expect(Math.abs(firstAction.y - secondAction.y)).toBeLessThanOrEqual(1);
  await page.screenshot({ path: 'artifacts/take-selection-narrow.png', fullPage: true });
  // Viewing the chosen take can show both states; browsing never adopts a take.
  await first.locator('[data-shot-take]').click();
  await expect(first.locator('.take-viewing-badge')).toHaveText('Viewing');
  await expect(first.locator('.take-selected-badge')).toHaveText('Selected');
  await first.getByRole('button', { name: 'Deselect take', exact: true }).click();
  await expect(review.locator('.take-selected-badge')).toHaveCount(0);
  await expect(first.locator('.take-viewing-badge')).toHaveText('Viewing');
});

test('takes stay new until they have been shown in the Takes view', async ({ page, request }) => {
  test.setTimeout(120000);
  await page.setViewportSize({ width: 1173, height: 1000 });
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  expect((await request.post(`/fixtures/${id}/take-generation-setup`)).ok()).toBe(true);
  const state = async () => (await request.get(`/fixtures/${id}/shots`)).json();
  const job = async () => (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.target.projectId === id && j.kind === 'Video');
  await page.goto(`/projects/${id}/shots`);
  await toolsTab(page, 'Generate');
  await closeShotSetup(page);
  await page.getByRole('button', { name: 'Generate takes', exact: true }).click();
  await expect.poll(async () => (await state()).takes.length, { timeout: 45000 }).toBe(2);
  // The batch review marks what it shows as seen.
  await expect.poll(async () => (await job()).unread).toBe(false);
  await page.locator('.shot-review-dialog').getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.locator('.shot-new-takes')).toHaveCount(0);

  await request.post(`/fixtures/ai-jobs/${(await job()).id}/unread`);
  await expect(page.locator('.shot-new-takes')).toHaveText('· 2 new');
  const shotTab = page.locator('[data-workspace-group=center]').getByRole('tab', { name: 'Shot', exact: true });
  await shotTab.click();
  // A hidden Takes view must not count as seen; acknowledgement checks every second.
  await page.waitForTimeout(1500);
  expect((await job()).unread).toBe(true);
  await page.locator('[data-workspace-group=center]').getByRole('tab', { name: 'Takes', exact: true }).click();
  await expect(page.locator('.unified-take-card .new-take-badge')).toHaveCount(0);
  await expect(page.locator('.shot-new-takes')).toHaveCount(0);
  expect((await job()).unread).toBe(false);
});
