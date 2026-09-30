import { test, expect } from './fixtures.js';

const activity = page => page.getByRole('dialog', { name: 'AI activity', exact: true });
const modelDialog = page => page.getByRole('dialog', { name: 'Model details & test', exact: true });
// ComfyUI advanced tests open in their own dialog, also when reopened from AI activity.
const advancedDialog = page => page.getByRole('dialog', { name: 'Advanced model test', exact: true });
async function pause(page, paused, provider = 'ComfyUI') {
  await page.locator('.ai-activity-trigger').click();
  await expect(activity(page)).toBeVisible();
  const button = activity(page).getByRole('button', { name: `${paused ? 'Pause' : 'Resume'} queue ${provider}`, exact: true });
  if (await button.isVisible()) await button.click();
  else await expect(activity(page).getByRole('button', { name: `${paused ? 'Resume' : 'Pause'} queue ${provider}`, exact: true })).toBeVisible();
  await activity(page).getByRole('button', { name: 'Close AI activity' }).click();
}

test('model tests queue, survive closing, reopen with their captured response and preserve unrelated drafts', async ({ page, request }) => {
  await page.goto('/settings/ai');
  await expect(page.locator('h1')).toBeFocused();
  await pause(page, true);
  try {
    await page.getByRole('tab', { name: 'Connections', exact: true }).click();
    await page.locator('#comfy-url').fill('http://unsaved.invalid:8188');
    await page.getByRole('tab', { name: 'Text models', exact: true }).click();
    const row = page.locator('.text-model-row').filter({ has: page.getByRole('button', { name: 'Star Mock script writer', exact: true }) }).first();
    await row.getByRole('button', { name: 'Mock script writer', exact: true }).click();
    await row.getByRole('button', { name: 'Advanced test', exact: true }).click();
    const prompt = `Queued test ${Date.now()} <plain text>`;
    await advancedDialog(page).getByLabel('Test message', { exact: true }).fill(prompt);
    await expect(advancedDialog(page).getByLabel('Maximum reply tokens')).toHaveAttribute('max', '32768');
    await advancedDialog(page).getByLabel('Maximum reply tokens').fill('32769');
    await expect(advancedDialog(page).getByRole('button', { name: 'Run advanced test', exact: true })).toBeDisabled();
    await advancedDialog(page).getByLabel('Maximum reply tokens').fill('32768');
    await advancedDialog(page).getByRole('button', { name: 'Run advanced test', exact: true }).click();
    await expect(advancedDialog(page)).toContainText('This provider queue is paused');
    await expect(advancedDialog(page).getByRole('button', { name: 'Run advanced test', exact: true })).toBeDisabled();
    await advancedDialog(page).getByRole('button', { name: 'Close', exact: true }).click();
    await expect(advancedDialog(page)).not.toBeVisible();
    await page.getByRole('tab', { name: 'Connections', exact: true }).click();
    await expect(page.locator('#comfy-url')).toHaveValue('http://unsaved.invalid:8188');
    await pause(page, false);
    const jobs = await (await request.get('/fixtures/ai-jobs')).json();
    const job = jobs.filter(j => j.kind === 'TextAdvancedTest').at(-1);
    expect(job).toBeTruthy();
    await expect.poll(async () => (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.id === job.id).state).toBe('Completed');
    await expect(advancedDialog(page)).not.toBeVisible();
    await expect(page.locator('#comfy-url')).toHaveValue('http://unsaved.invalid:8188');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.locator('.ai-activity-trigger').click();
    await activity(page).getByRole('tab', { name: 'History', exact: true }).click();
    await activity(page).locator(`[data-job-id="${job.id}"]`).getByRole('link', { name: 'Review', exact: true }).click();
    await expect(advancedDialog(page)).toBeVisible();
    await expect(advancedDialog(page).getByLabel('Test message', { exact: true })).toHaveValue(prompt);
    await expect(advancedDialog(page).getByLabel('Maximum reply tokens')).toHaveValue('32768');
    await expect(advancedDialog(page).locator('.advanced-model-test-result pre')).toHaveText(`Mock response: ${prompt}`);
    await expect(advancedDialog(page)).toContainText('Test result saved.');
    await expect.poll(async () => (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.id === job.id).unread).toBe(false);
    await expect(advancedDialog(page).locator('script')).toHaveCount(0);
    await page.screenshot({ path: 'test-results/queue-model-test-mobile.png' });
    await page.keyboard.press('Escape');
    await expect(advancedDialog(page)).not.toBeVisible();
  } finally {
    if (await advancedDialog(page).isVisible()) await advancedDialog(page).getByRole('button', { name: 'Close', exact: true }).click();
    await pause(page, false);
  }
});

test('OpenRouter benchmarks and custom tests retain replies, metrics and the correct provider when reopened', async ({ page, request }) => {
  await page.goto('/settings/ai?tab=text&provider=openrouter');
  await expect(page.locator('h1')).toBeFocused();
  const row = page.locator('.text-model-row').filter({ has: page.getByRole('button', { name: 'Mock script writer', exact: true }) });
  await row.getByRole('button', { name: 'Mock script writer', exact: true }).click();
  await row.getByRole('button', { name: 'Details & test', exact: true }).click();
  await expect(modelDialog(page)).toContainText('can incur OpenRouter charges');
  await expect(modelDialog(page)).not.toContainText('Run a test before starring');
  await modelDialog(page).getByRole('button', { name: 'Run benchmark · 2,048 total tokens', exact: true }).click();
  await expect(modelDialog(page)).toContainText('Test result saved.');
  await expect(modelDialog(page)).toContainText('32.0 reply tokens/s');
  await expect(modelDialog(page)).toContainText('$0.000001 reported cost');
  await expect(modelDialog(page)).toContainText('A quiet forest at dawn.');
  await modelDialog(page).getByRole('button', { name: 'Close', exact: true }).click();
  await pause(page, true, 'OpenRouter');
  try {
    await row.getByRole('button', { name: 'Details & test', exact: true }).click();
    await modelDialog(page).getByText('Advanced model test', { exact: true }).click();
    const prompt = `Content-boundary test ${Date.now()}: describe your limits. <script>untrusted()</script>`;
    await modelDialog(page).getByLabel('Test message', { exact: true }).fill(prompt);
    await modelDialog(page).getByLabel('Maximum total output tokens').fill('0');
    await expect(modelDialog(page).getByRole('button', { name: 'Run advanced test', exact: true })).toBeDisabled();
    await modelDialog(page).getByLabel('Maximum total output tokens').fill('1024');
    await modelDialog(page).getByRole('button', { name: 'Run advanced test', exact: true }).click();
    await expect(modelDialog(page)).toContainText('This provider queue is paused');
    await modelDialog(page).getByRole('button', { name: 'Close', exact: true }).click();
    await pause(page, false, 'OpenRouter');
    const jobs = await (await request.get('/fixtures/ai-jobs')).json();
    const job = jobs.filter(j => j.kind === 'TextAdvancedTest').at(-1);
    await expect.poll(async () => (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.id === job.id).state).toBe('Completed');
    const project = await (await request.get('/fixtures/new')).json();
    const { slug } = await (await request.get(`/fixtures/${project.id}/project-route`)).json();
    const origin = `/projects/${slug}/assets`;
    await page.goto(`/settings/ai?jobId=${job.id}&returnUrl=${encodeURIComponent(origin)}`);
    await expect(modelDialog(page)).toBeVisible();
    // The reopened test selects its provider in page state; the URL keeps only the page and tab.
    await expect(page.locator('#ai-text-provider-openrouter')).toHaveAttribute('aria-selected', 'true');
    await expect(page).not.toHaveURL(/provider=/);
    await expect(modelDialog(page).getByRole('button', { name: 'Run benchmark · 2,048 total tokens', exact: true })).toBeEnabled();
    await expect(page.locator('.writing-back')).toHaveAttribute('href', origin);
    await expect(modelDialog(page).locator('.advanced-model-test-result pre')).toHaveText(`Mock reply to: ${prompt}`);
    await expect(modelDialog(page).locator('script')).toHaveCount(0);
    await expect(modelDialog(page)).toContainText('1024-token custom test');
    await modelDialog(page).getByText('Advanced model test', { exact: true }).click();
    await expect(modelDialog(page).getByLabel('Maximum total output tokens')).toHaveValue('1024');
    await page.setViewportSize({ width: 1173, height: 1272 });
    await page.screenshot({ path: 'artifacts/openrouter-model-test-desktop.png' });
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
    await page.screenshot({ path: 'artifacts/openrouter-model-test-mobile.png' });
    const count = jobs.filter(j => j.kind === 'TextAdvancedTest').length;
    await page.reload(); await expect(modelDialog(page)).toContainText('Test result saved.');
    expect((await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.kind === 'TextAdvancedTest').length).toBe(count);
  } finally {
    if (await modelDialog(page).isVisible()) await modelDialog(page).getByRole('button', { name: 'Close', exact: true }).click();
    await pause(page, false, 'OpenRouter');
  }
});
