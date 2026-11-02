import { test, expect } from './fixtures.js';
import { cropImageInput, imageOutput, toolsTab, addImageReference } from './workspace-tools.js';

const review = page => page.locator('.image-review-dialog');
const activity = page => page.getByRole('dialog', { name: 'AI activity', exact: true });
async function pause(page, value) {
  await page.locator('.ai-activity-trigger').click();
  await activity(page).getByRole('button', { name: `${value ? 'Pause' : 'Resume'} queue ComfyUI`, exact: true }).click();
  await activity(page).getByRole('button', { name: 'Close AI activity' }).click();
}
async function setup(page, request, patterned = false) {
  const { id } = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${id}/images?patterned=${patterned}`)).json();
  await page.goto(`/projects/${id}/assets`);
  await toolsTab(page, 'Prompt');
  return { id, library };
}
// Choosing an asset saves the previous one before it switches; a prompt typed in between belongs to
// the previous asset. Wait for the switch before using the composer.
async function chooseAsset(page, index) {
  const choice = page.locator('.asset-choice').nth(index);
  await choice.click(); await expect(choice).toHaveAttribute('aria-pressed', 'true');
  await toolsTab(page, 'Prompt');
}
async function jobs(request, id) { return (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id); }

for (const edit of [false, true]) {
  test(`${edit ? 'edit' : 'create'} review opens on the first saved image in a visible unfocused page and stays closed after dismissal`, async ({ page, request }) => {
    // Embedded browsers can remain visible while their host owns keyboard focus.
    await page.addInitScript(() => Object.defineProperty(document, 'hasFocus', { value: () => false }));
    const { id } = await setup(page, request);
    if (edit) await page.locator('.asset-media-card[data-media-kind=Image] .media-select').click();
    await page.getByLabel(edit ? 'Edit instruction' : 'Image prompt', { exact: true }).fill('SLOW: Keep the face and change the coat.');
    await imageOutput(page); await page.locator('#candidate-count').selectOption('2');
    await page.getByRole('button', { name: edit ? 'Generate edited images' : 'Generate images', exact: true }).click();

    await expect(review(page)).toBeVisible();
    await expect(review(page).getByRole('button', { name: 'View Take 1', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(review(page).locator('.review-pending-row')).toHaveCount(1);
    await review(page).getByRole('button', { name: 'Close image review', exact: true }).click();
    await expect.poll(async () => (await jobs(request, id))[0]?.state, { timeout: 25000 }).toBe('Completed');
    await expect(page.locator('#image-prompt')).toHaveValue('');
    await expect(page.getByRole('button', { name: edit ? 'Generate edited images' : 'Generate images', exact: true })).toBeDisabled();
    await page.locator('#image-prompt').fill('My next image');
    await page.locator('.asset-choice').nth(1).click(); await page.locator('.asset-choice').first().click();
    if (edit) await page.locator('.asset-media-card[data-media-kind=Image] .media-select').first().click();
    await expect(page.locator('#image-prompt')).toHaveValue('My next image');
    await expect(review(page)).not.toBeVisible();
  });
}

test('activity opens a waiting image batch on a freshly loaded Assets page', async ({ page, request }) => {
  const { id } = await setup(page, request);
  await page.locator('.asset-media-card[data-media-kind=Image] .media-select').click();
  await page.getByLabel('Edit instruction', { exact: true }).fill('Preserve the face and change the coat to blue.');
  await pause(page, true);
  try {
    await page.getByRole('button', { name: 'Generate edited images', exact: true }).click();
    await expect(page.locator('.generation-panel .ai-queue-wait')).toBeVisible();
    const [job] = await jobs(request, id);
    await page.locator('.project-tabs').getByRole('link', { name: 'Script', exact: true }).click();
    await page.locator('.ai-activity-trigger').click();
    await activity(page).locator(`[data-job-id="${job.id}"]`).getByRole('link', { name: 'View', exact: true }).click();
    await expect(review(page)).toBeVisible();
    await expect(review(page).locator('.review-pending-row')).toHaveCount(1);
    await expect(review(page).locator('.review-pending-row')).toContainText('Queued');
    await review(page).getByRole('button', { name: 'Close image review', exact: true }).click();
    await expect(page.locator('.prompt-enhancement')).not.toContainText('AI settings could not be loaded');
    await page.locator('.generation-panel').getByRole('button', { name: 'Cancel request', exact: true }).click();
    await expect.poll(async () => (await jobs(request, id))[0].state).toBe('Cancelled');
  } finally { await pause(page, false); }
});

test('queued image edit survives reload and navigation with exact references and durable review', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  const { id, library } = await setup(page, request, true);
  await page.locator('.asset-media-card[data-media-kind=Image] .media-select').click();
  await toolsTab(page, 'Inputs');
  await addImageReference(page, `${library.assets[1].id}/${library.assets[1].images[0].id}`);
  await cropImageInput(page,0,2); await cropImageInput(page,1,4,100,100);
  await toolsTab(page, 'Prompt'); await page.locator('#image-prompt').fill('Keep image 1 and use image 2 for clothing.');
  await imageOutput(page); await page.locator('#candidate-count').selectOption('2');
  await pause(page, true);
  try {
    await page.getByRole('button', { name: 'Generate edited images', exact: true }).click();
    await expect(page.locator('.generation-panel .ai-queue-wait')).toContainText('Queued in Lumibelle');
    await page.reload(); await toolsTab(page, 'Prompt');
    // The queued request keeps its inputs; the composer it came from stays clear and usable.
    await expect(page.locator('.asset-image-request .ai-queue-wait')).toContainText('Queued in Lumibelle');
    await expect(page.locator('#image-prompt')).toHaveValue('');
    await expect(page.locator('#image-prompt')).toBeEnabled();
    await page.locator('.project-tabs').getByRole('link', { name: 'Script', exact: true }).click();
  } finally { await pause(page, false); }
  await expect.poll(async () => (await jobs(request, id))[0]?.state).toBe('Completed');
  await expect(review(page)).not.toBeVisible();
  const [job] = await jobs(request, id); expect(job.cancelRequested).toBe(false);
  await page.locator('.ai-activity-trigger').click();
  await activity(page).getByRole('tab', { name: 'History', exact: true }).click();
  await activity(page).getByLabel('Project', { exact: true }).selectOption(id);
  await activity(page).locator(`[data-job-id="${job.id}"]`).getByRole('link', { name: 'Review', exact: true }).click();
  await expect(review(page).locator('.review-image-select strong')).toHaveText(['Source', 'Reference 2', 'Take 1', 'Take 2']);
  await review(page).getByRole('button', { name: 'Compare with Source', exact: true }).click();
  await expect(review(page).getByRole('button', { name: 'Submitted crops', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(review(page).locator('.review-wipe-overlay img')).toHaveAttribute('style', /width:200%/);
  await review(page).getByRole('button', { name: 'Discard Take 1', exact: true }).click();
  await expect(review(page).getByRole('button', { name: 'View Take 1', exact: true })).toHaveCount(0);
  await review(page).getByRole('button', { name: 'Close image review', exact: true }).click();
  await page.goto(`/projects/${id}/assets`); await toolsTab(page, 'Prompt');
  await page.getByRole('button', { name: 'Review latest edit', exact: true }).click();
  await expect(review(page).locator('.review-image-select strong')).toHaveText(['Source', 'Reference 2', 'Take 2']);
  await expect(review(page).locator('.review-pending-row')).toHaveCount(0);
  await page.screenshot({ path: 'test-results/queue-image-reopened.png' });
});

test('image jobs release the page and keep other asset drafts and cancellation independent', async ({ page, request }) => {
  const { id } = await setup(page, request);
  await page.locator('#image-prompt').fill('Captured first asset portrait');
  await pause(page, true);
  try {
    await page.getByRole('button', { name: 'Generate images', exact: true }).click();
    await expect(page.locator('.generation-panel .ai-queue-wait')).toBeVisible();
    await chooseAsset(page, 1);
    await expect(page.locator('#image-prompt')).toBeEnabled();
    await page.locator('#image-prompt').fill('Keep this other asset draft');
    await chooseAsset(page, 0);
    await expect(page.locator('#image-prompt')).toHaveValue('');
    await page.locator('.asset-image-request').getByRole('button', { name: 'Cancel request', exact: true }).click();
    await expect(page.locator('#image-prompt')).toBeEnabled();
    await expect(page.locator('.generation-panel')).toContainText('Generation cancelled');
    await chooseAsset(page, 1);
    await expect(page.locator('#image-prompt')).toHaveValue('Keep this other asset draft');
    expect((await jobs(request, id))[0].state).toBe('Cancelled');
    await expect(review(page)).not.toBeVisible();
  } finally { await pause(page, false); }
});


test('successful background image creation clears its own draft and preserves another asset draft', async ({ page, request }) => {
  const { id } = await setup(page, request);
  await page.locator('#image-prompt').fill('Captured first asset portrait');
  await pause(page, true);
  try {
    await page.getByRole('button', { name: 'Generate images', exact: true }).click();
    await expect(page.locator('.generation-panel .ai-queue-wait')).toBeVisible();
    await expect(page.locator('#image-prompt')).toHaveValue(''); await expect(page.locator('#image-prompt')).toBeEnabled();
    await chooseAsset(page, 1);
    await page.locator('#image-prompt').fill('Keep this other asset draft');
  } finally { await pause(page, false); }
  await expect.poll(async () => (await jobs(request, id))[0]?.state).toBe('Completed');
  await expect(page.locator('#image-prompt')).toHaveValue('Keep this other asset draft');
  await expect(review(page)).not.toBeVisible();
  await chooseAsset(page, 0);
  await expect(page.locator('#image-prompt')).toHaveValue('');
  await expect(page.getByRole('button', { name: 'Generate images', exact: true })).toBeDisabled();
  await chooseAsset(page, 1);
  await expect(page.locator('#image-prompt')).toHaveValue('Keep this other asset draft');
});

test('an open image review acknowledges arriving results but failed media loading retains unread status', async ({ page, request }) => {
  const { id } = await setup(page, request);
  await page.locator('#image-prompt').fill('SLOW: A rabbit in a blue coat.');
  await imageOutput(page); await page.locator('#candidate-count').selectOption('2');
  await page.getByRole('button', { name: 'Generate images', exact: true }).click();
  await expect(review(page)).toBeVisible();
  await expect.poll(async () => (await jobs(request, id))[0]?.state, { timeout: 20000 }).toBe('Completed');
  await expect.poll(async () => (await jobs(request, id))[0]?.unread).toBe(false);
  await review(page).getByRole('button', { name: 'Close image review', exact: true }).click();
  await page.route('**/media/projects/**', route => route.abort());
  await page.locator('#image-prompt').fill('A rabbit in a blue coat.');
  await page.locator('#candidate-count').selectOption('1');
  await page.getByRole('button', { name: 'Generate images', exact: true }).click();
  await expect.poll(async () => (await jobs(request, id)).at(-1)?.state, { timeout: 20000 }).toBe('Completed');
  await expect(review(page)).toBeVisible();
  await expect(review(page).locator('.review-unavailable')).toBeVisible();
  expect((await jobs(request, id)).at(-1).unread).toBe(true);
});

test('the same asset queues another image request while the first waits', async ({ page, request }) => {
  const { id } = await setup(page, request);
  await pause(page, true);
  try {
    for (const prompt of ['First portrait', 'Second portrait']) {
      await page.locator('#image-prompt').fill(prompt);
      await page.getByRole('button', { name: 'Generate images', exact: true }).click();
      await expect(page.locator('#image-prompt')).toHaveValue('');
      await expect(page.locator('#image-prompt')).toBeFocused();
    }
    await expect(page.locator('.asset-image-request')).toHaveCount(2);
    await expect(page.locator('.asset-image-requests-summary')).toHaveText('2 queued · This provider queue is paused.');
    await expect(page.locator('.asset-image-request.is-compact .asset-image-request-prompt')).toHaveText([/#1 .First portrait./, /#2 .Second portrait./]);
    await expect(page.locator('.asset-image-request.is-compact').first().getByRole('button', { name: 'View request', exact: true })).toBeVisible();
    await expect(page.locator('#image-prompt')).toBeInViewport();
    await expect(page.locator('.workspace-status')).toContainText('2 queued image requests');
    expect((await jobs(request, id)).map(j => j.state)).toEqual(['Waiting', 'Waiting']);
    await page.screenshot({ path: 'artifacts/queued-image-requests.png' });
    await page.locator('.asset-image-request').first().getByRole('button', { name: 'Cancel request', exact: true }).click();
    await expect(page.locator('.asset-image-request')).toHaveCount(1);
    await expect(page.locator('.asset-image-request .ai-queue-wait')).toBeVisible(); // A single request shows its full progress again.
    await expect(page.locator('.workspace-status')).toContainText('Queued');
  } finally { await pause(page, false); }
  await expect.poll(async () => (await jobs(request, id)).map(j => j.state).sort()).toEqual(['Cancelled', 'Completed']);
  await expect(page.locator('.asset-image-request')).toHaveCount(0);
});
