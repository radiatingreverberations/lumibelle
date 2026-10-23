import { test, expect } from './fixtures.js';

const source = `Prologue\nEira inherits a brass key in a rainstorm.\n\nChapter 1\n${'She follows the river, looking for the gate her grandmother described.\n\n'.repeat(90)}Chapter 2\nShe finds the gate. The key opens it onto a sheltered garden.`;

async function openOutline(page) {
  await expect(page.getByRole('region', { name: 'Source workspace', exact: true })).toBeVisible();
  const tab = page.getByRole('tab', { name: 'Outline', exact: true });
  if (!await tab.isVisible()) {
    const closeAssistant = page.getByRole('button', { name: 'Close Assistant', exact: true });
    if (await closeAssistant.isVisible()) await closeAssistant.click();
    await page.getByRole('button', { name: /^Show (Source|Outline)$/ }).click();
  }
  await tab.click();
  await expect(page.locator('#source-mode-outline')).toHaveAttribute('aria-selected', 'true');
  const overview = page.locator('.workspace-left').getByRole('button', { name: 'Overview', exact: true });
  // Changing navigation mode reveals the center and closes the compact drawer.
  if (!await overview.isVisible())
    await page.getByRole('button', { name: 'Show Outline', exact: true }).click();
  await overview.click();
}

test('source analysis persists across navigation, pauses, and produces an editable plan without touching the screenplay', async ({ page, request }) => {
  test.setTimeout(120000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  const script = (await (await request.get(`/fixtures/${id}`)).json()).script;
  await page.goto(`/projects/${id}/source`);
  await expect(page.locator('.studio-workspace[data-studio=Source]')).toHaveAttribute('data-ready', 'true');
  const left = page.locator('.workspace-left');
  const center = page.locator('.workspace-center');
  const right = page.locator('.workspace-right');
  const boxes = await Promise.all([left, center, right].map(locator => locator.boundingBox()));
  expect(boxes[0].x + boxes[0].width).toBeLessThanOrEqual(boxes[1].x);
  expect(boxes[1].x + boxes[1].width).toBeLessThanOrEqual(boxes[2].x);
  await expect(center.getByLabel('Source title', { exact: true })).toBeVisible();
  await expect(right.getByLabel('Adaptation direction', { exact: true })).toBeVisible();
  const tabs = page.getByRole('navigation', { name: 'Project navigation' });
  await expect(tabs.getByRole('link', { name: 'Source', exact: true })).toBeVisible();
  const labels = await tabs.getByRole('link').allTextContents();
  expect(labels.indexOf('Source')).toBeLessThan(labels.indexOf('Script'));
  await page.getByLabel('Source title', { exact: true }).fill('The river key');
  await page.getByLabel('Original text', { exact: true }).fill(source);
  await page.getByLabel('Adaptation direction', { exact: true }).fill('Keep Eira’s point of view. Choose acts around the reveal.');
  await page.getByRole('button', { name: 'Save source', exact: true }).click();
  await expect(page.getByText('Source saved', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Analyze source', exact: true }).click();
  await expect(right.getByText('Reading source', { exact: true })).toBeVisible();
  await expect.poll(async () => (await (await request.get(`/fixtures/${id}/source`)).json()).runs[0].notes.length).toBeGreaterThan(0);
  await page.getByRole('button', { name: 'Pause after this request', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Resume analysis', exact: true })).toBeVisible();
  await tabs.getByRole('link', { name: 'Script', exact: true }).click();
  await expect(page.locator('.studio-workspace[data-studio=Script]')).toHaveAttribute('data-ready', 'true');
  await page.getByRole('navigation', { name: 'Project navigation' }).getByRole('link', { name: 'Source', exact: true }).click();
  await expect(page.locator('.studio-workspace[data-studio=Source]')).toHaveAttribute('data-ready', 'true');
  await expect(page.getByLabel('Original text', { exact: true })).toHaveValue(source);
  // Let the request being paused finish before switching, then retain its checkpoint.
  await expect.poll(async () => (await (await request.get('/fixtures/ai-jobs')).json())
    .some(j => j.target.projectId === id && ['Waiting', 'Running'].includes(j.state))).toBe(false);
  const paused = (await (await request.get(`/fixtures/${id}/source`)).json()).runs[0];
  await expect(page.getByRole('button', { name: 'Resume analysis', exact: true })).toBeEnabled();
  await right.getByRole('button', { name: 'Text model options', exact: true }).click();
  const modelDialog = page.getByRole('dialog', { name: 'Text model', exact: true });
  await modelDialog.getByLabel('Text model', { exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await modelDialog.getByRole('button', { name: 'Done', exact: true }).click();
  await page.getByRole('button', { name: 'Resume analysis', exact: true }).click();
  await expect(right.getByRole('button', { name: 'Text model options', exact: true })).toContainText('Alternate mock model');
  await expect(page.getByRole('button', { name: 'Accept outline', exact: true })).toBeVisible({ timeout: 70000 });
  const manuscript = page.getByLabel('Original text', { exact: true });
  const titleBefore = await page.getByLabel('Source title', { exact: true }).boundingBox();
  await manuscript.focus();
  await manuscript.press('Control+End');
  const position = await manuscript.evaluate(el => ({ top: el.scrollTop, start: el.selectionStart }));
  expect(position.top).toBeGreaterThan(0);
  const titleAfter = await page.getByLabel('Source title', { exact: true }).boundingBox();
  expect(titleAfter.y).toBe(titleBefore.y);
  expect(await page.evaluate(() => document.documentElement.scrollHeight <= innerHeight + 1)).toBeTruthy();
  await expect(left.getByRole('heading', { name: 'Source sections', exact: true })).toBeVisible();
  await left.getByRole('tab', { name: 'Outline', exact: true }).click();
  await left.getByRole('button', { name: '1.1 A key in the rain', exact: true }).click();
  await expect(left.getByRole('button', { name: '1.1 A key in the rain', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(center.getByLabel('Act 1 sequence 1 summary')).toBeVisible();
  await expect(center.getByLabel('Act 2 sequence 1 summary')).toHaveCount(0);
  await expect(center.getByRole('region', { name: 'Source coverage review', exact: true })).toHaveCount(0);
  await center.getByRole('button', { name: 'Back to original', exact: true }).click();
  await expect(manuscript).toBeVisible();
  await expect(left.getByRole('heading', { name: 'Source sections', exact: true })).toBeVisible();
  await expect(left.getByRole('button', { name: '1.1 A key in the rain', exact: true })).toHaveCount(0);
  await expect.poll(() => manuscript.evaluate(el => ({ top: el.scrollTop, start: el.selectionStart }))).toEqual(position);
  await page.screenshot({ path: 'artifacts/source-manuscript-desktop.png' });
  await openOutline(page);
  await expect(page.getByRole('heading', { name: 'Review the proposed structure', exact: true })).toBeVisible({ timeout: 70000 });
  const captured = await (await request.get(`/fixtures/${id}/source`)).json();
  expect(captured.runs[0].model.model).toBe('mock/alternate');
  expect(captured.runs[0].notes.slice(0, paused.notes.length)).toEqual(paused.notes);
  expect(captured.runs[0].chunks).toEqual(paused.chunks);
  expect(captured.runs[0].chunks.length).toBeGreaterThan(2);
  expect(captured.runs[0].notes.length).toBe(captured.runs[0].chunks.length);
  const requests = page.locator('.source-details').filter({ has: page.locator('#source-request') });
  await requests.locator('summary').first().click();
  await requests.getByText('Request details', { exact: true }).click();
  await expect(requests.locator('.submitted-input')).toContainText('Propose a reviewable movie adaptation outline');
  await requests.locator('summary').first().click();
  await page.locator('.source-links').first().getByRole('button').first().click();
  const dialog = page.getByRole('dialog').filter({ has: page.locator('.source-excerpt') });
  await expect(dialog.locator('.source-excerpt')).toContainText('inherits a brass key');
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await page.getByRole('button', { name: 'Edit outline', exact: true }).click();
  await page.getByLabel('Outline title', { exact: true }).fill('The garden beyond');
  await page.getByRole('button', { name: 'Save review edits', exact: true }).click();
  await page.reload();
  await openOutline(page);
  await expect(page.locator('.outline-reading').getByRole('heading', { name: 'The garden beyond', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Accept outline', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Accepted plan' })).toBeVisible();
  expect((await (await request.get(`/fixtures/${id}`)).json()).script).toEqual(script);
  await page.screenshot({ path: 'artifacts/source-outline-desktop.png', fullPage: true });
  await page.getByLabel('Adaptation direction', { exact: true }).fill('Use a different point of view.');
  await page.getByRole('button', { name: 'Save source', exact: true }).click();
  await expect(page.getByText(/This analysis uses an earlier version/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Accept outline', exact: true })).toBeDisabled();
});

test('source import and analysis remain usable on a narrow screen', async ({ page, request }) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/projects/${id}/source`);
  await page.locator('input[type=file]').setInputFiles({ name: 'The river key.txt', mimeType: 'text/plain', buffer: Buffer.from('Chapter 1\nEira finds a key.\n\nChapter 2\nThe key opens the garden gate.') });
  await expect(page.getByLabel('Source title', { exact: true })).toHaveValue('The river key');
  await page.getByRole('button', { name: 'Show Assistant', exact: true }).click();
  await page.getByRole('button', { name: 'Analyze source', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Accept outline', exact: true })).toBeVisible({ timeout: 50000 });
  await expect(page.getByRole('button', { name: 'Accept outline', exact: true })).toBeEnabled();
  await openOutline(page);
  await expect(page.locator('.workspace-right')).toBeHidden();
  await expect(page.getByRole('heading', { name: 'Review the proposed structure' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.getByRole('button', { name: 'Show Outline', exact: true }).click();
  await page.locator('.workspace-left').getByRole('button', { name: '1.1 A key in the rain', exact: true }).click();
  await expect(page.locator('.workspace-left')).toBeHidden();
  await page.getByLabel('Act 1 sequence 1 summary').fill('Eira finds the key and decides to investigate.');
  await page.getByRole('button', { name: 'Save or accept outline →', exact: true }).click();
  await page.getByRole('button', { name: 'Accept outline', exact: true }).click();
  await openOutline(page);
  await expect(page.getByRole('heading', { name: 'Accepted plan' })).toBeVisible();
  await page.screenshot({ path: 'artifacts/source-outline-mobile.png', fullPage: true });
});

test('a large Unicode source saves intact without exceeding the editor connection limit', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const text = '森の物語。'.repeat(80000);
  await page.goto(`/projects/${id}/source`);
  await page.getByLabel('Original text', { exact: true }).fill(text);
  await page.getByRole('button', { name: 'Save source', exact: true }).click();
  await expect(page.getByText('Source saved', { exact: true })).toBeVisible();
  const saved = await (await request.get(`/fixtures/${id}/source`)).json();
  expect(saved.text).toBe(text);
  await page.reload();
  await expect(page.getByLabel('Original text', { exact: true })).toHaveValue(text);
});
