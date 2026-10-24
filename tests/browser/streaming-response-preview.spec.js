import { test, expect } from './fixtures.js';

for (const stage of ['planning', 'writing', 'review']) {
  for (const width of [1280, 390]) {
    test(stage + ' streams readable cards at ' + width + 'px without allowing partial application', async ({ page, request }) => {
      await page.setViewportSize({ width, height: 900 });
      const { id } = await (await request.get('/fixtures/new')).json();
      const fixture = await request.post('/fixtures/' + id + '/planning-review?stage=stream-' + stage);
      expect(fixture.ok()).toBeTruthy();
      const job = await fixture.json();
      await page.goto('/projects/' + id + '/script?jobId=' + job.id);
      const dialog = page.getByRole('dialog');
      const preview = dialog.getByRole('region', { name: 'Live response preview', exact: true });
      await expect(preview).toBeVisible();
      await expect(preview.locator('.streaming-preview-card')).toHaveCount(2);
      await expect(preview.locator('.streaming-preview-card').first()).toContainText('Received');
      await expect(preview.locator('.streaming-preview-card').last()).toContainText('Still writing');
      await expect(preview).toContainText('She follows the ri');
      await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toHaveCount(0);
      await preview.scrollIntoViewIfNeeded();
      expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
      const initialScroll = await dialog.locator('.script-review-content').evaluate(el => el.scrollTop);
      expect((await request.post('/fixtures/' + job.id + '/advance-streaming-preview')).ok()).toBeTruthy();
      await expect(preview).toContainText('She follows the river to the garden gate.');
      expect(await dialog.locator('.script-review-content').evaluate(el => el.scrollTop)).toBe(initialScroll);
      await expect(preview.locator('.streaming-preview-card')).toHaveCount(2);
      await page.screenshot({ path: 'artifacts/streaming-' + stage + '-' + width + '.png', fullPage: true });
      const details = dialog.locator('.planning-response-details');
      await details.locator('summary').click();
      await expect(details.locator('pre')).toContainText(stage === 'review' ? 'EXT. RIVER BANK' : '{"');
      expect((await request.post('/fixtures/' + job.id + '/complete-writing-preview')).ok()).toBeTruthy();
      await expect(preview).toHaveCount(0);
      await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
      // Completing a response makes a proposal; it never applies it automatically.
      const script = (await (await request.get('/fixtures/' + id)).json()).script;
      expect(script.blocks.every(b => b.spans.every(s => !s.text))).toBe(true);
    });
  }
}

test('the shared activity inspector refreshes streamed JSON cards while the same job is running', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const fixture = await request.post('/fixtures/' + id + '/planning-review?stage=stream-planning');
  expect(fixture.ok()).toBeTruthy();
  const job = await fixture.json();
  await page.goto('/settings/ai?tab=connections');
  await expect(page.getByRole('heading', { name: 'AI settings', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'AI settings', exact: true })).toBeFocused();
  await page.locator('.ai-activity-trigger').click();
  const drawer = page.getByRole('dialog', { name: 'AI activity', exact: true });
  await drawer.getByRole('button', { name: 'Inspect response', exact: true }).click();
  const preview = drawer.getByRole('region', { name: 'Live response preview', exact: true });
  await expect(preview).toContainText('Eira finds the brass key.');
  expect((await request.post('/fixtures/' + job.id + '/advance-streaming-preview')).ok()).toBeTruthy();
  await expect(preview).toContainText('She follows the river to the garden gate.');
  await expect(preview.locator('.streaming-preview-card')).toHaveCount(2);
  await drawer.getByText('Response details', { exact: true }).click();
  await expect(drawer.locator('.ai-job-response pre')).toContainText('{"beats":');
  expect((await request.post('/fixtures/' + job.id + '/complete-writing-preview')).ok()).toBeTruthy();
});
