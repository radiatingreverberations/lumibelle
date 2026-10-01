import { test, expect } from './fixtures.js';

test('an added shot is drafted from its scene with directions, reviewed, applied and undone', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: '+ Shot', exact: true }).click();
  await expect(page.getByLabel('Shot title', { exact: true })).toHaveValue('Untitled shot');
  const shots = async () => (await (await request.get(`/fixtures/${id}/shots`)).json()).shots;

  await page.locator('.shot-draft-assist').getByRole('button', { name: 'Draft this shot', exact: true }).click();
  const composer = page.locator('.ai-assist-dialog').filter({ has: page.getByRole('heading', { name: 'Draft this shot', exact: true }) });
  await expect(composer).toBeVisible();
  await expect(composer).toContainText('1 other shot is sent as context');
  await expect(composer.locator('.shot-draft-place')).toContainText('It ends the scene, after 1. Production test.');
  await composer.getByLabel('Directions (optional)').fill('Hold on the key as she hesitates.');
  await composer.getByRole('button', { name: 'Draft shot', exact: true }).click();

  const review = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Drafted shot' }) });
  await expect(review).toBeVisible({ timeout: 30000 });
  await expect(review).toContainText('The missing reaction');
  await expect(review).toContainText('Hold on the key as she hesitates.');
  await page.screenshot({ path: 'test-results/shot-draft-review.png' });
  await review.getByRole('button', { name: 'Apply to this shot', exact: true }).click();
  await expect(review).toBeHidden();
  await expect(page.getByLabel('Shot title', { exact: true })).toHaveValue('The missing reaction');
  await expect.poll(async () => (await shots()).find(s => s.title === 'The missing reaction')?.description ?? '', { timeout: 30000 }).toContain('Hold on the key');
  const added = (await shots()).find(s => s.title === 'The missing reaction').id;
  // The breakdown control in the sidebar is not taken over by a single-shot draft.
  await expect(page.locator('.shots-library-tools').getByRole('button', { name: 'Draft shots', exact: true })).toBeVisible();

  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(page.getByLabel('Shot title', { exact: true })).toHaveValue('Untitled shot');
  await expect.poll(async () => (await shots()).find(s => s.id === added).title, { timeout: 30000 }).toBe('Untitled shot');
});
