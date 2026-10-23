import { test, expect } from './fixtures.js';

for (const viewport of [{ name: 'desktop', width: 1280, height: 900 }, { name: 'mobile', width: 390, height: 844 }]) {
  test(`automatic screenplay review is visible and gates applying on ${viewport.name}`, async ({ page, request }) => {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    const { id } = await (await request.get('/fixtures/new')).json();
    const response = await request.post(`/fixtures/${id}/planning-review?stage=${viewport.name === 'mobile' ? 'repairing' : 'checking'}`);
    expect(response.ok()).toBeTruthy();
    const job = await response.json();
    await page.goto(`/projects/${id}/script?jobId=${job.id}`);
    const review = page.getByRole('dialog');
    await expect(review.getByRole('heading', { name: viewport.name === 'mobile' ? 'Repairing screenplay' : 'Checking screenplay', exact: true })).toBeVisible();
    await expect(review.locator('.planning-stages > li')).toHaveCount(3);
    await expect(review.locator('.planning-stages [aria-current=step]')).toContainText('Review draft');
    await expect(review.getByRole('button', { name: 'Apply changes', exact: true })).toHaveCount(0);
    await expect(review.getByRole('button', { name: 'Cancel request', exact: true })).toBeInViewport();
    expect(await review.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
    await page.screenshot({ path: `artifacts/sequence-quality-checking-${viewport.name}.png`, fullPage: true });
    expect((await request.post(`/fixtures/${job.id}/complete-writing-preview`)).ok()).toBeTruthy();
    await expect(review.getByRole('heading', { name: 'Review script proposal', exact: true })).toBeVisible();
    await expect(review.locator('.planning-stages [aria-current=step]')).toContainText('Passed');
    const apply = review.getByRole('button', { name: 'Apply changes', exact: true });
    await expect(apply).toBeEnabled();
    await expect(apply).toBeInViewport();
    await review.locator('.sequence-quality-review > summary').click();
    await expect(review.locator('.sequence-quality-review')).toContainText('7 / 7 required checks');
    await expect(review.getByRole('region', { name: 'Review notes', exact: true }).locator('li')).toHaveCount(2);
    await expect(review.getByRole('region', { name: 'Review notes', exact: true }).locator('em')).toHaveText('action');
    await review.locator('.sequence-quality-review').scrollIntoViewIfNeeded();
    await page.screenshot({ path: `artifacts/sequence-quality-notes-${viewport.name}.png`, fullPage: true });
    await review.locator('.sequence-quality-review > summary').click();
    await review.locator('.planning-stages').scrollIntoViewIfNeeded();
    await page.screenshot({ path: `artifacts/sequence-quality-passed-${viewport.name}.png`, fullPage: true });
    await apply.click();
    await expect(review).not.toBeVisible();
    expect((await (await request.get(`/fixtures/${id}`)).json()).script.sourceSceneLinks).toHaveLength(1);
  });
}

test('an interrupted automatic review keeps a readable draft and continues the same saved request', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const response = await request.post(`/fixtures/${id}/planning-review?stage=review-interrupted`);
  expect(response.ok()).toBeTruthy();
  const job = await response.json();
  await page.goto(`/projects/${id}/script?jobId=${job.id}`);
  const review = page.getByRole('dialog');
  await expect(review.getByRole('alert')).toContainText('Automatic review could not be verified');
  await expect(review.getByRole('region', { name: 'Unapproved draft', exact: true })).toContainText('Eira follows the paper map');
  await expect(review.getByRole('button', { name: 'Apply changes', exact: true })).toHaveCount(0);
  await review.getByRole('button', { name: 'Continue review', exact: true }).click();
  await expect(review.getByRole('heading', { name: 'Checking screenplay', exact: true })).toBeVisible();
  expect((await request.post(`/fixtures/${job.id}/complete-writing-preview`)).ok()).toBeTruthy();
  await expect(review.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
  expect((await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.kind === 'ScriptAssistant')).toHaveLength(1);
});

for (const recovery of ['recheck', 'manual']) {
  test(`a saved invalid review explains its cause and supports ${recovery} recovery`, async ({ page, request }) => {
    await page.setViewportSize(recovery === 'manual' ? { width: 390, height: 844 } : { width: 1280, height: 900 });
    const { id } = await (await request.get('/fixtures/new')).json();
    const fixture = await request.post(`/fixtures/${id}/planning-review?stage=review-invalid`);
    expect(fixture.ok()).toBeTruthy();
    const { id: jobId } = await fixture.json();
    await page.goto(`/projects/${id}/script?jobId=${jobId}`);
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('alert')).toContainText('19 citations');
    await expect(dialog.getByRole('alert')).toContainText('does not confirm a screenplay error');
    await expect(dialog.getByRole('alert')).toContainText('without another AI call');
    await expect(dialog.locator('.sequence-quality-review .saved-quality-response')).not.toHaveAttribute('open');
    await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toHaveCount(0);
    await page.screenshot({ path: `artifacts/sequence-review-recovery-${recovery}.png`, fullPage: true });
    if (recovery === 'recheck') {
      await dialog.getByRole('button', { name: 'Recheck saved review', exact: true }).click();
      await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
      await dialog.locator('.sequence-quality-review > summary').click();
      await expect(dialog.locator('.sequence-quality-review')).toContainText('1 citation(s) could not be verified');
      await expect(dialog.locator('.sequence-quality-review')).toContainText('18 verified citation(s) remain');
      await expect(dialog.getByRole('region', { name: 'Review notes', exact: true })).toBeVisible();
      await expect(dialog.getByRole('region', { name: 'Citation warnings', exact: true }).locator('li')).toHaveCount(2);
      await dialog.locator('.sequence-quality-review').scrollIntoViewIfNeeded();
      await page.screenshot({ path: 'artifacts/sequence-quality-notes-and-warnings.png', fullPage: true });
    } else {
      await dialog.getByRole('button', { name: 'Review draft manually', exact: true }).click();
      await expect(dialog).toContainText('This is a manual-review copy');
      await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
      await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toBeInViewport();
      expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
    }
    expect((await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.kind === 'ScriptAssistant')).toHaveLength(1);
    expect((await (await request.get(`/fixtures/${id}`)).json()).script.blocks.every(b => b.spans.every(s => !s.text))).toBe(true);
    await dialog.getByRole('button', { name: 'Apply changes', exact: true }).click();
    await expect(dialog).not.toBeVisible();
    expect((await (await request.get(`/fixtures/${id}`)).json()).script.sourceSceneLinks).toHaveLength(1);
  });
}
