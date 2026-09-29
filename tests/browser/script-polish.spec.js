import { scriptAssist as assist } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

async function project(page, request, acts = false) {
  const p = await (await request.get('/fixtures/new')).json();
  if (acts) await request.post(`/fixtures/${p.id}/revision-script`);
  await page.goto(`/projects/${p.id}/script`);
  await expect(page.getByRole('textbox', { name: 'Screenplay', exact: true })).toBeVisible();
  return { id: p.id, state: async () => (await request.get(`/fixtures/${p.id}`)).json() };
}
const text = b => b.spans.map(s => s.text).join('');
async function draft(page) {
  await assist(page);
  await page.getByLabel('Instructions', { exact: true }).fill('Draft a short scene about a mouse making breakfast.');
  await page.getByRole('button', { name: 'Draft script', exact: true }).click();
  await page.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(2);
}

test('outline insertion, move menu, drag, rename and Undo preserve screenplay blocks', async ({ page, request }) => {
  const p = await project(page, request, true); const before = (await p.state()).script.blocks;
  const scenes = before.filter(b => b.kind === 'Scene'), acts = before.filter(b => b.kind === 'Act');
  await page.locator(`[data-outline-id="${scenes[0].id}"] .outline-title`).click();
  await page.getByRole('button', { name: '+ Scene', exact: true }).click();
  await expect.poll(() => page.evaluate(() => getSelection()?.toString())).toBe('INT. LOCATION — DAY');
  await page.keyboard.type('INT. INSERTED — DAY');
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(scenes.length + 1);
  const firstTwo = await page.locator('.script-canvas [data-kind=Scene]').allTextContents();
  expect(firstTwo.slice(0, 2)).toEqual([text(scenes[0]), 'INT. INSERTED — DAY']);
  const row = page.locator('.script-outline-item').filter({ has: page.locator('.outline-title', { hasText: 'INT. INSERTED — DAY' }) });
  await row.locator('summary').click(); await row.getByRole('button', { name: 'Move to…', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('combobox').first().selectOption(acts[1].id);
  await dialog.getByRole('combobox').nth(1).selectOption('start');
  await dialog.getByRole('button', { name: 'Move section', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  await expect.poll(async () => { const b = (await p.state()).script.blocks; return text(b[b.findIndex(x => x.id === acts[1].id) + 1]); }).toBe('INT. INSERTED — DAY');
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Scene]').nth(1)).toHaveText('INT. INSERTED — DAY');
  const handle = page.locator(`[data-outline-id="${scenes[0].id}"] .outline-drag`);
  const target = page.locator(`[data-outline-id="${acts[1].id}"]`);
  await target.scrollIntoViewIfNeeded();
  const a = await handle.boundingBox(), b = await target.boundingBox();
  await page.mouse.move(a.x + a.width / 2, a.y + 10); await page.mouse.down();
  await page.mouse.move(b.x + b.width / 2, b.y + b.height * .5, { steps: 8 });
  await expect(target).toHaveAttribute('data-outline-drop', 'start'); await page.mouse.up();
  await expect.poll(async () => { const list = (await p.state()).script.blocks; return list[list.findIndex(x => x.id === acts[1].id) + 1]?.id; }).toBe(scenes[0].id);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.locator('[data-toggle-pane=left]').click();
  await expect(page.locator('.script-outline')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: 'artifacts/script-polish-narrow-outline.png' });
});

test('consecutive focused revisions capture the applied script and manual edits survive save and Undo', async ({ page, request }) => {
  const p = await project(page, request); await draft(page);
  await expect(page.locator('.script-brief')).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Approve.*production/ })).toHaveCount(0);
  await assist(page);
  await page.locator('#script-scope').selectOption('Document');
  await assist(page);
  await page.getByLabel('Instructions', { exact: true }).fill('FIRST_CHANGE');
  const initial = (await p.state()).script.blocks;
  await page.getByRole('button', { name: 'Revise', exact: true }).click();
  await expect(page.locator('.script-review-dialog').locator('.is-comparison')).toBeVisible({ timeout: 15000 });
  await page.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await assist(page);
  await page.getByLabel('Instructions', { exact: true }).fill('SECOND_CHANGE');
  await page.getByRole('button', { name: 'Revise', exact: true }).click();
  await page.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect.poll(async () => text((await p.state()).script.blocks[1])).toContain('First detail. Second detail.');
  const latest = await p.state(); expect(latest.script.blocks.slice(2)).toEqual(initial.slice(2));
  expect(text(latest.history.runs.at(-1).target.originalBlocks[1])).toContain('First detail.');
  const action = page.locator('.script-canvas [data-kind=Action]').first();
  await action.click(); await page.keyboard.press('End'); await page.keyboard.type(' Manual ending.');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();
  await page.reload(); await expect(action).toContainText('Manual ending.');
  await action.click(); await page.keyboard.press('End'); await page.keyboard.type(' Temporary.');
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(action).not.toContainText('Temporary.');
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();
  await page.screenshot({ path: 'artifacts/script-polish-desktop.png', fullPage: true });
});

test('changed targets and invalid responses keep the authored script', async ({ page, request }) => {
  const p = await project(page, request); await draft(page);
  await assist(page);
  await page.locator('#script-scope').selectOption('Document');
  await assist(page);
  await page.getByLabel('Instructions', { exact: true }).fill('SLOW');
  await page.getByRole('button', { name: 'Revise', exact: true }).click();
  await expect(page.getByRole('button', { name: /Revising.*View request/ })).toBeVisible();
  const action = page.locator('.script-canvas [data-kind=Action]').first();
  await action.click(); await page.keyboard.press('End'); await page.keyboard.type(' New local detail.');
  await expect(page.getByRole('button', { name: 'Apply changes', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Request fresh changes', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Close', exact: true }).click();
  await assist(page);
  await page.locator('.script-assistant .request-action-menu > summary').click();
  await page.getByRole('button', { name: 'New request', exact: true }).click();
  await page.getByLabel('Instructions', { exact: true }).fill('INVALID');
  await page.getByRole('button', { name: 'Revise', exact: true }).click();
  await expect(page.getByRole('button', { name: /^(Review changes|View response|Needs attention)$/ })).toBeVisible({ timeout: 15000 });
  expect(text((await p.state()).script.blocks[1])).toContain('New local detail.');
  await expect.poll(async () => (await p.state()).history.runs.at(-1).error).toBeTruthy();
  // An invalid response is never applicable, but the author can still retire it.
  await page.getByRole('button', { name: 'Needs attention', exact: true }).click();
  const review = page.locator('.script-review-dialog');
  await expect(review.getByRole('button', { name: 'Apply changes', exact: true })).toHaveCount(0);
  await review.getByRole('button', { name: 'Dismiss', exact: true }).click();
  await expect(review).not.toBeVisible();
  await expect(page.getByRole('button', { name: 'Revise', exact: true })).toBeEnabled();
  await expect.poll(async () => (await p.state()).history.runs.at(-1).rejected).toBe(true);
  expect(text((await p.state()).script.blocks[1])).toContain('New local detail.');
});

test('asset extraction uses the saved screenplay without production approval', async ({ page, request }) => {
  const p = await project(page, request); await draft(page);
  expect((await p.state()).approved).toBeNull();
  await page.locator('.project-tabs').getByRole('link', { name: 'Assets', exact: true }).click();
  await page.getByRole('button', { name: 'Extract assets from script', exact: true }).click();
  await expect(page.locator('.coverage-scenes')).toBeVisible();
  await page.getByRole('button', { name: 'Find assets', exact: true }).click();
  await page.locator('.apply-extraction').click();
  await expect.poll(async () => (await p.state()).assets.assets.length).toBeGreaterThan(0);
  expect((await p.state()).approved).toBeNull();
});

test('writing, outline and export work when model discovery is unavailable', async ({ page, request }) => {
  await request.post('/fixtures/text-catalog?enabled=false&failed=true');
  try {
    const p = await project(page, request);
    await page.getByRole('button', { name: '+ Scene', exact: true }).click();
    await expect.poll(() => page.evaluate(() => getSelection()?.toString())).toBe('INT. LOCATION — DAY');
    await page.keyboard.type('INT. QUIET ROOM — DAY'); await page.keyboard.press('Enter'); await page.keyboard.type('Mira opens a book.');
    await page.keyboard.press('Control+s');
    await expect.poll(async () => JSON.stringify((await p.state()).script.blocks)).toContain('Mira opens a book.');
    await assist(page);
    await expect(page.getByRole('button', { name: 'Revise', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Script', exact: true }).click();
    const download = page.waitForEvent('download');
    await page.getByText('Export Markdown', { exact: true }).click();
    expect((await download).suggestedFilename()).toMatch(/\.md$/);
    expect((await p.state()).history.runs).toHaveLength(0);
  } finally { await request.post('/fixtures/text-catalog?enabled=false&failed=false'); }
});

test('discussion enters requests only when explicitly attached and can be removed', async ({ page, request }) => {
  await request.post('/fixtures/text-catalog?enabled=false&failed=false');
  await project(page, request); await draft(page);
  await assist(page);
  await page.locator('#script-operation').selectOption('Discuss');
  await assist(page);
  await page.getByLabel('Instructions', { exact: true }).fill('Discuss an exploratory silver bell.');
  await page.getByRole('button', { name: 'Discuss idea', exact: true }).click();
  await page.locator('.script-review-dialog').getByRole('button', { name: 'Close', exact: true }).click();
  await assist(page);
  await page.locator('.script-assistant .request-action-menu > summary').click();
  await page.getByRole('button', { name: 'New request', exact: true }).click();
  await page.locator('#script-operation').selectOption('Revise'); await page.locator('#script-scope').selectOption('Document');
  await assist(page);
  await page.getByLabel('Instructions', { exact: true }).fill('FIRST_CHANGE');
  await page.getByRole('button', { name: 'Revise', exact: true }).click();
  await page.locator('.script-review-dialog').getByText('Request details', { exact: true }).click();
  await expect(page.locator('.submitted-input')).not.toContainText('silver bell');
  await page.locator('.script-review-dialog').getByRole('button', { name: 'Discard', exact: true }).click();
  await assist(page);
  await page.getByText('Attach discussion', { exact: true }).click(); await page.locator('.discussion-attachments input').check();
  await page.getByRole('button', { name: 'Revise', exact: true }).click();
  await page.locator('.script-review-dialog').getByText('Request details', { exact: true }).click();
  await expect(page.locator('.submitted-input')).toContainText('silver bell');
  await page.locator('.script-review-dialog').getByRole('button', { name: 'Discard', exact: true }).click();
  await assist(page);
  await page.getByRole('button', { name: /Remove attached discussion/ }).click();
  await expect(page.locator('.attached-context')).toHaveCount(0); await expect(page.locator('.discussion-attachments input')).not.toBeChecked();
});
