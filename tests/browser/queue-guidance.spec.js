import { submitGuidance } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';
import { assetView, imageAction } from './workspace-tools.js';

const review = page => page.locator('.guidance-dialog');
const identity = page => page.locator('.asset-editor > .asset-preservation');
// The section opens by itself only when a link names the asset; after a reload it starts collapsed.
async function openIdentity(page) {
  if (await identity(page).getAttribute('open') === null) await identity(page).locator(':scope > summary').click();
  await expect(identity(page)).toHaveAttribute('open', '');
}
const activity = page => page.getByRole('dialog', { name: 'AI activity', exact: true });
async function setup(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${id}/images`)).json();
  await page.goto(`/projects/${id}/assets?assetId=${library.assets[0].id}`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  return { id, asset: library.assets[0] };
}
async function pause(page, paused) {
  await page.locator('.ai-activity-trigger').click();
  await activity(page).getByRole('button', { name: `${paused ? 'Pause' : 'Resume'} queue OpenRouter`, exact: true }).click();
  await activity(page).getByRole('button', { name: 'Close AI activity' }).click();
}
async function jobs(request, id) { return (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id); }
async function state(request, id) { return (await (await request.get(`/fixtures/${id}`)).json()).assets; }
async function activityReview(page, id, job) {
  await page.locator('.ai-activity-trigger').click();
  await activity(page).getByRole('tab', { name: 'History', exact: true }).click();
  await activity(page).getByLabel('Project', { exact: true }).selectOption(id);
  await activity(page).locator(`[data-job-id="${job.id}"]`).getByRole('link', { name: 'Review', exact: true }).click();
  await expect(review(page)).toBeVisible();
}

test('guidance queues per field, survives navigation and reviews the exact image with no delayed popup', async ({ page, request }) => {
  const { id, asset } = await setup(page, request);
  await pause(page, true);
  await assetView(page, 'Asset details');
  await identity(page).getByRole('button', { name: 'Suggest guidance', exact: true }).click();
  await submitGuidance(page, identity(page));
  await expect(review(page)).toContainText('Queued in Lumibelle');
  await page.keyboard.press('Escape'); await expect(review(page)).toBeHidden();
  await expect(identity(page).getByRole('button', { name: /View request/ })).toBeFocused();
  await assetView(page, 'Images'); await imageAction(page, 'Edit details');
  await page.locator('.image-review-inspector .media-details-disclosure').filter({ has: page.getByText('Use guidance', { exact: true }) }).locator('summary').click();
  await page.locator('.image-review-inspector .media-details-disclosure').getByRole('button', { name: 'Suggest guidance', exact: true }).click();
  await submitGuidance(page, page.locator('.image-review-inspector .media-details-disclosure'));
  await expect(review(page)).toContainText('position 2');
  await review(page).getByRole('button', { name: 'Close', exact: true }).click(); await expect(review(page)).toBeHidden();
  await page.locator('.image-review-dialog').getByRole('button', { name: 'Close image review', exact: true }).click();
  await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true'); await assetView(page, 'Asset details');
  await openIdentity(page); await identity(page).getByRole('button', { name: /View request/ }).click();
  await expect(identity(page).locator('.text-model-picker .model-chip')).toHaveCount(0);
  await expect(review(page).getByRole('button', { name: 'Suggest', exact: true })).toHaveCount(0);
  expect(await jobs(request, id)).toHaveLength(2);
  await review(page).getByRole('button', { name: 'Close', exact: true }).click(); await expect(review(page)).toBeHidden();
  await assetView(page, 'Images');
  await page.locator('.project-tabs').getByRole('link', { name: 'Shots', exact: true }).click();
  await pause(page, false);

  await expect.poll(async () => (await jobs(request, id)).filter(j => j.state === 'Completed').length).toBe(2);
  await expect(review(page)).toBeHidden();
  const job = (await jobs(request, id)).find(j => j.target.imageId === asset.images[0].id);
  expect(job.unread).toBe(true);
  await activityReview(page, id, job);
  await expect(review(page)).toContainText('This image');
  await expect.poll(async () => (await jobs(request, id)).find(j => j.id === job.id).unread).toBe(false);
  await review(page).getByLabel('Suggested guidance', { exact: true }).fill('Only this image keeps this detail.');
  await review(page).getByRole('button', { name: 'Apply changes', exact: true }).click();
  await page.locator('.image-review-dialog').getByRole('button', { name: 'Save details', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).assets[0].images[0].preservationGuidance).toBe('Only this image keeps this detail.');
  expect((await state(request, id)).assets[0].preservationGuidance).toBe('');
  expect((await state(request, id)).assets[1].images[0].preservationGuidance).toBe('');
});

test('queued guidance cancels explicitly and preserves a newer local draft when reopening a completed result', async ({ page, request }) => {
  const { id } = await setup(page, request);
  await pause(page, true);
  try {
    await assetView(page, 'Asset details');
    await identity(page).getByRole('button', { name: 'Suggest guidance', exact: true }).click();
    await submitGuidance(page, identity(page));
    await expect(review(page)).toContainText('Queued in Lumibelle');
    await review(page).getByRole('button', { name: 'Cancel suggestion', exact: true }).click();
    await expect(review(page)).toContainText('cancelled');
    await expect(review(page).getByRole('button', { name: 'Apply changes', exact: true })).toBeDisabled();
    await review(page).getByRole('button', { name: 'Retry', exact: true }).click();
    await expect(review(page)).toContainText('Queued in Lumibelle');
    await review(page).getByRole('button', { name: 'Close', exact: true }).click(); await expect(review(page)).toBeHidden();
  } finally {
    if (await review(page).isVisible()) { await review(page).getByRole('button', { name: 'Close', exact: true }).click(); await expect(review(page)).toBeHidden(); }
    const details = page.locator('.asset-details-dialog');
    if (await details.isVisible()) await details.getByRole('button', { name: 'Close', exact: true }).click();
    const closeTools = page.getByRole('button', { name: 'Close Asset tools', exact: true });
    if (await closeTools.isVisible()) await closeTools.click();
    await pause(page, false);
  }
  await expect.poll(async () => (await jobs(request, id)).filter(j => j.state === 'Completed').length).toBe(1);
  const job = (await jobs(request, id)).find(j => j.state === 'Completed');
  await assetView(page, 'Asset details');
  await page.getByLabel('Character identity notes', { exact: true }).fill('Newer manually authored identity.');
  await identity(page).getByRole('button', { name: /^(Review changes|View response|Needs attention)$/ }).click();
  await expect(review(page)).toContainText('Since this suggestion was written, the notes changed. It may not match the character as it is now.');
  await review(page).getByLabel('Suggested guidance', { exact: true }).fill('Keep this edited suggestion while activity updates.');
  await expect.poll(async () => (await jobs(request, id)).find(j => j.id === job.id).unread).toBe(false);
  await expect(review(page).getByLabel('Suggested guidance', { exact: true })).toHaveValue('Keep this edited suggestion while activity updates.');
  expect(await jobs(request, id)).toHaveLength(2);
  await review(page).getByRole('button', { name: 'Apply anyway', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).assets[0].preservationGuidance).toBe('Keep this edited suggestion while activity updates.');
  expect((await state(request, id)).assets[0].description).toBe('Newer manually authored identity.');
});

test('look guidance reopens inline from activity on mobile without changing notes until explicit application', async ({ page, request }) => {
  const { id, asset } = await setup(page, request);
  await assetView(page, 'Asset details'); await page.getByRole('button', { name: 'Add look', exact: true }).click();
  const editor = page.locator('.look-editor-dialog');
  await editor.getByLabel('Look name', { exact: true }).fill('Everyday');
  await editor.getByLabel('Look description', { exact: true }).fill('A gray coat.');
  await editor.getByRole('button', { name: 'Save look', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).assets[0].looks.length).toBe(1);
  await page.getByRole('button', { name: 'Look actions', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Edit look', exact: true }).click();
  await editor.getByText('Keep consistent in shots', { exact: true }).click();
  await editor.getByRole('button', { name: 'Suggest guidance', exact: true }).click();
  await expect(page.locator('.ai-assist-dialog')).toBeVisible();
  await submitGuidance(page, editor);
  await expect(review(page).getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
  await editor.getByRole('button', { name: 'Cancel', exact: true }).click();
  await assetView(page, 'Images');
  await page.locator('.project-tabs').getByRole('link', { name: 'Shots', exact: true }).click();
  const job = (await jobs(request, id))[0];
  await page.setViewportSize({ width: 390, height: 844 });
  await activityReview(page, id, job);
  await expect(editor.getByLabel('Look name', { exact: true })).toHaveValue('Everyday');
  await expect(page.locator('.asset-details-dialog')).toBeVisible();
  await expect(page.locator('.look-editor-dialog')).toBeVisible();
  await review(page).getByLabel('Suggested guidance', { exact: true }).fill('Keep the gray coat.');
  await page.screenshot({ path: 'artifacts/validation/plan005-guidance-mobile.png' });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await review(page).getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(review(page)).toBeHidden();
  await expect(editor.getByLabel('This look', { exact: true })).toHaveValue('Keep the gray coat.');
  await editor.getByRole('button', { name: 'Save look', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).assets[0].looks[0].preservationGuidance).toBe('Keep the gray coat.');
  await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  // The exact link reopens the details, the look and the review itself, a moment after the page is ready.
  await expect(page.locator('.asset-details-dialog')).toBeVisible(); await expect(review(page)).toBeVisible();
  // It still shows the original result, names the change and shows the guidance it would replace.
  await expect(review(page)).toContainText('Since this suggestion was written, the guidance was edited. It may not match the look as it is now.');
  await expect(review(page).getByLabel('Current guidance, replaced when applied', { exact: true })).toHaveValue('Keep the gray coat.');
  await expect(review(page).getByRole('button', { name: 'Apply anyway', exact: true })).toBeEnabled();
  expect((await state(request, id)).assets[0].id).toBe(asset.id);
});
