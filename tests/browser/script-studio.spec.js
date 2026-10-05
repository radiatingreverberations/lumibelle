import { scriptAssist, closeComposer } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

async function openProject(page, request) {
  const project = await (await request.get('/fixtures/new')).json();
  await page.goto(`/projects/${project.id}/script`);
  await expect(page.getByRole('textbox', { name: 'Screenplay', exact: true })).toBeVisible();
  await scriptAssist(page);
  await expect(page.getByRole('button', { name: 'Draft script', exact: true })).toBeEnabled();
  await closeComposer(page);
  return project.id;
}
async function draft(page) {
  await scriptAssist(page);
  await page.locator('#script-instructions').fill('A small mouse finds a key while looking for dinner.');
  await page.getByRole('button', { name: 'Draft script', exact: true }).click();
  await expect(page.getByRole('dialog', { name: /Review script proposal/ })).toBeVisible();
  await page.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(2);
  await expect(page.locator('.script-review')).toHaveCount(0);
  await expect(page.locator('.script-paper')).toHaveAttribute('aria-busy', 'false');
}
async function state(request, id) { return (await request.get(`/fixtures/${id}`)).json(); }

test('reviewed draft, passage revision, reorder, extraction and further writing', async ({ page, request }) => {
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  const id = await openProject(page, request);
  await draft(page);
  const action = page.locator('.script-canvas [data-kind=Action]').first();
  await action.evaluate(node => {
    const text = document.createTreeWalker(node, NodeFilter.SHOW_TEXT).nextNode();
    const range = document.createRange(); range.setStart(text, 2); range.setEnd(text, 12);
    const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range);
  });
  await scriptAssist(page);
  await page.locator('#script-scope').selectOption('Passage');
  await scriptAssist(page);
  await page.locator('#script-instructions').fill('Make this more polite.');
  await page.getByRole('button', { name: 'Revise', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
  await expect(action).toHaveText('A tiny mouse appears in a saucepan.');
  await page.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(action).toContainText('politely bows');
  const sceneMenu = page.locator('.script-outline-item').filter({ has: page.locator('.outline-title[title="EXT. GARDEN — DAWN"]') });
  await sceneMenu.locator('summary').click(); await sceneMenu.getByRole('button', { name: 'Move to…', exact: true }).click();
  const move = page.getByRole('dialog');
  await move.getByRole('combobox').first().selectOption('');
  await move.getByRole('combobox').nth(1).selectOption('before');
  await move.getByRole('button', { name: 'Move section', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Scene]').first()).toHaveText('EXT. GARDEN — DAWN');
  expect((await state(request, id)).approved).toBeNull();
  await page.locator('.project-tabs').getByRole('link', { name: 'Assets', exact: true }).click();
  await page.getByRole('button', { name: 'Extract assets from script', exact: true }).click();
  await expect(page.locator('.coverage-scenes')).toBeVisible();
  await page.getByRole('button', { name: 'Find assets', exact: true }).click();
  await page.locator('.apply-extraction').click();
  await expect(page.locator('#asset-name')).toHaveValue('Juniper');
  const extracted = await state(request, id);
  const capturedSource = extracted.assets.assets[0].evidence[0].approvedScriptId;
  expect(capturedSource).toBeTruthy();
  await page.locator('.project-tabs').getByRole('link', { name: 'Script', exact: true }).click();
  await page.locator('.script-canvas [data-kind=Dialogue]').click();
  await page.keyboard.press('End'); await page.keyboard.type(' With dessert?');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect.poll(async () => (await state(request, id)).script.blocks.some(b => b.spans.some(s => s.text.includes('dessert')))).toBeTruthy();
  const edited = await state(request, id);
  expect(edited.approved).toBeNull();
  expect(edited.assets.assets[0].evidence[0].approvedScriptId).toBe(capturedSource);
  expect(edited.script.blocks.some(b => b.spans.some(s => s.text.includes('dessert')))).toBeTruthy();
  await page.screenshot({ path: 'test-results/script-desktop.png', fullPage: true });
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Dialogue]')).not.toContainText('dessert');
  expect(errors).toEqual([]);
});

test('formatting, stable ids, split, delete and undo survive reopening', async ({ page, request }) => {
  const id = await openProject(page, request);
  await page.getByRole('button', { name: '+ Scene', exact: true }).click();
  const heading = page.locator('.script-canvas [data-kind=Scene]');
  await heading.click(); await page.keyboard.press('Home'); await page.keyboard.press('Shift+End'); await page.keyboard.type('INT. 森 — DAY');
  await page.keyboard.press('Enter'); await page.keyboard.type('A door opens.');
  await page.keyboard.press('Control+b'); await page.keyboard.type(' Quietly.'); await page.keyboard.press('Control+b');
  await expect(page.locator('.script-canvas strong')).toHaveText(' Quietly.');
  await page.locator('.outline-menu summary').first().click();
  await page.getByRole('button', { name: 'Split scene at cursor', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(2);
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  // Split is a single undoable author action.
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(1);
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();
  await expect.poll(async () => JSON.stringify((await state(request, id)).script.blocks)).toContain('Quietly.');
  const before = await state(request, id);
  await page.reload(); await expect(page.locator('.script-canvas strong')).toHaveText(' Quietly.');
  const after = await state(request, id); expect(after.script.blocks).toEqual(before.script.blocks);
});

test('editing during generation makes target stale and switching stays disabled', async ({ page, request }) => {
  const id = await openProject(page, request); await draft(page);
  await page.locator('.script-canvas [data-kind=Action]').first().click();
  await scriptAssist(page);
  await page.locator('#script-scope').selectOption('Scene'); await page.locator('#script-instructions').fill('SLOW');
  await page.getByRole('button', { name: 'Revise', exact: true }).click();
  await expect(page.getByRole('button', { name: /Revising.*View request/ })).toBeVisible();
  await expect(page.locator('.script-assistant .assist-composer-model .model-chip')).toBeDisabled();
  await expect.poll(async () => (await state(request, id)).history.runs.at(-1).target.name).toBe('INT. KITCHEN — NIGHT');
  await page.locator('.script-canvas [data-kind=Action]').first().click(); await page.keyboard.press('End'); await page.keyboard.type(' A new detail.');
  await expect(page.getByRole('button', { name: 'Apply changes', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Apply changes', exact: true })).toBeDisabled();
  await expect(page.getByText('The target changed or was deleted.', { exact: false })).toBeVisible();
  await page.getByRole('button', { name: 'Request fresh changes', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
});

test('cancellation and malformed or truncated output never replace script', async ({ page, request }) => {
  const id = await openProject(page, request);
  await scriptAssist(page);
  await page.locator('#script-instructions').fill('SLOW make dinner');
  await page.getByRole('button', { name: 'Draft script', exact: true }).click();
  // The running request opens in its review, which cancels it.
  await page.locator('.script-assistant .request-action-button').click();
  await page.locator('.script-review-dialog').getByRole('button', { name: 'Cancel request', exact: true }).click();
  await page.locator('.script-review-dialog').getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.locator('.script-assistant').getByText('Request cancelled.', { exact: false })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Draft script', exact: true })).toBeEnabled();
  for (const prompt of ['INVALID', 'TRUNCATED']) {
  await scriptAssist(page);
    const earlier = page.locator('.script-assistant .request-action-button');
    if (!(await earlier.getAttribute('aria-label'))?.startsWith('Draft script')) {
      await earlier.click();
      await page.locator('.script-review-dialog').getByRole('button', { name: 'New request', exact: true }).click();
    }
    await page.locator('#script-instructions').fill(prompt);
    await page.getByRole('button', { name: 'Draft script', exact: true }).click();
    await expect(page.locator('.ai-assist-dialog')).not.toBeVisible();
    await expect.poll(async () => (await state(request, id)).history.runs.at(-1).instructions).toBe(prompt);
    await expect.poll(async () => (await state(request, id)).history.runs.at(-1).status).not.toBe('Running');
    await expect(page.getByRole('button', { name: 'Needs attention', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Cancel request', exact: true })).not.toBeVisible();
    await expect(page.getByRole('button', { name: 'Apply changes', exact: true })).toHaveCount(0);
  }
  expect((await state(request, id)).script.blocks.every(b => b.spans.every(s => !s.text))).toBeTruthy();
});

test('empty scripts need a scene before extraction', async ({ page, request }) => {
  await openProject(page, request);
  await page.locator('.project-tabs').getByRole('link', { name: 'Assets', exact: true }).click();
  await page.getByRole('button', { name: 'Extract assets from script', exact: true }).click();
  await expect(page.getByText('Write a scene in Script before extracting assets.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Find assets', exact: true })).toBeDisabled();
});

test('large script and mobile drawers remain usable without horizontal overflow', async ({ page, request }) => {
  const id = await openProject(page, request);
  await request.post(`/fixtures/${id}/large`); await page.reload();
  await expect(page.locator('.script-canvas [data-kind=Scene]')).toHaveCount(160);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator('.script-outline')).not.toBeVisible();
  const overflow = await page.evaluate(() => [...document.querySelectorAll('body *')].filter(el => el.getBoundingClientRect().right > innerWidth + 1 && el.getBoundingClientRect().width > 0).slice(0, 12).map(el => ({ tag: el.tagName, class: el.className, right: el.getBoundingClientRect().right })));
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), JSON.stringify(overflow)).toBeTruthy();
  await page.locator('[data-toggle-pane=left]').click();
  await expect(page.locator('.script-outline')).toBeVisible();
  await expect(page.locator('.workspace-left')).toHaveAttribute('aria-modal', 'true');
  await expect(page.getByRole('button', { name: 'Close Outline', exact: true })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(page.locator('.script-outline')).not.toBeVisible();
  await expect(page.locator('[data-toggle-pane=left]')).toBeFocused();
  await scriptAssist(page);
  await expect(page.locator('.workspace-right')).toHaveAttribute('aria-modal', 'true');
  await page.getByRole('button', { name: 'Close Assistant', exact: true }).focus();
  await page.keyboard.press('Shift+Tab');
  expect(await page.locator('.workspace-right').evaluate(el => el.contains(document.activeElement))).toBeTruthy();
  await page.screenshot({ path: 'test-results/script-mobile.png' });
  await page.getByRole('button', { name: 'Close Assistant', exact: true }).click();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
});

test('paste strips embedded content and global save shortcut works', async ({ page, request }) => {
  const id = await openProject(page, request);
  const editor = page.getByRole('textbox', { name: 'Screenplay', exact: true });
  await editor.focus();
  await editor.evaluate(el => {
    const data = new DataTransfer(); data.setData('text/html', '<h2>INT. ROOM — DAY</h2><p>A <strong>brave</strong> mouse.</p><script>window.badPaste=true</script><img src=x onerror="window.badPaste=true"><iframe src="about:blank"></iframe>');
    el.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  });
  await expect(page.locator('.script-canvas strong')).toHaveText('brave');
  await expect(page.locator('.script-canvas script, .script-canvas img, .script-canvas iframe')).toHaveCount(0);
  await editor.focus(); await page.keyboard.press('Control+End'); await page.keyboard.type(' Saved with a keyboard shortcut.');
  await page.keyboard.press('Control+s');
  await expect.poll(async () => JSON.stringify((await state(request, id)).script.blocks)).toContain('keyboard shortcut');
  await expect.poll(async () => (await state(request, id)).script.blocks.some(b => b.spans.some(s => s.bold))).toBeTruthy();
});

test('another tab saving raises a conflict and keeps the unsaved draft', async ({ page, request, context }) => {
  const id = await openProject(page, request); await draft(page);
  const second = await context.newPage(); await second.goto(`/projects/${id}/script`);
  await expect(second.getByRole('textbox', { name: 'Screenplay', exact: true })).toBeVisible();
  const firstAction = page.locator('.script-canvas [data-kind=Action]').first(), secondAction = second.locator('.script-canvas [data-kind=Action]').first();
  await firstAction.click(); await page.keyboard.press('End'); await page.keyboard.type(' First tab saves.'); await page.keyboard.press('Control+s');
  await expect.poll(async () => JSON.stringify((await state(request, id)).script.blocks)).toContain('First tab saves.');
  await secondAction.click(); await second.keyboard.press('End'); await second.keyboard.type(' Second tab keeps its draft.'); await second.keyboard.press('Control+s');
  await expect(second.getByText('Another tab saved newer changes.', { exact: false })).toBeVisible();
  await expect(secondAction).toContainText('Second tab keeps its draft.');
  await expect(second.getByRole('button', { name: 'Download unsaved copy' })).toBeVisible();
  expect(JSON.stringify((await state(request, id)).script.blocks)).not.toContain('Second tab keeps its draft.');
  await second.getByRole('button', { name: 'Reload saved version', exact: true }).click();
  await second.getByRole('button', { name: 'Replace with saved version', exact: true }).click();
  await expect(secondAction).toContainText('First tab saves.'); await second.close();
});

test('editor draft survives a circuit reconnect and is saved afterward', async ({ page, request }) => {
  let socket;
  await page.routeWebSocket(/\/_blazor\?/, ws => { socket = ws; ws.connectToServer(); });
  const id = await openProject(page, request); await draft(page);
  const action = page.locator('.script-canvas [data-kind=Action]').first();
  await action.click(); await page.keyboard.press('End'); await page.keyboard.type(' Preserved through reconnect.');
  socket.close({ code: 1012, reason: 'Test reconnect' });
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeEnabled();
  await expect.poll(async () => (await state(request, id)).script.blocks.some(b => b.spans.some(s => s.text.includes('Preserved through reconnect.'))), { timeout: 20000 }).toBeTruthy();
  await expect(action).toContainText('Preserved through reconnect.');
});


test('proposal dialog supports reading, comparison, close and mobile focus', async ({ page, request }) => {
  await openProject(page, request); await draft(page);
  const reviewButton = page.getByRole('button', { name: /^(Review changes|View response|Needs attention)$/ });
  await scriptAssist(page);
  await page.locator('#script-instructions').fill('Make the ending warmer.');
  await page.getByRole('button', { name: 'Revise', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: /Review script proposal/ });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('region', { name: 'Proposed script', exact: true })).toBeVisible();
  await expect(dialog.getByRole('region', { name: 'Original script', exact: true })).toBeVisible();
  await dialog.getByRole('button', { name: 'Read proposal', exact: true }).click();
  await expect(dialog.getByRole('region', { name: 'Original script', exact: true })).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Compare changes', exact: true }).click();
  await expect(dialog.getByRole('region', { name: 'Original script', exact: true })).toBeVisible();
  await expect(dialog.locator('.script-review-pages')).toHaveClass(/is-comparison/);
  await page.screenshot({ path: 'test-results/script-review-desktop.png' });
  await page.keyboard.press('Escape'); await expect(dialog).toHaveCount(0);
  await reviewButton.click(); await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Close review', exact: true }).click();
  await expect(dialog).toHaveCount(0); await expect(reviewButton).toBeFocused();
  await page.setViewportSize({ width: 390, height: 844 });
  await scriptAssist(page);
  await reviewButton.click(); await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Close review', exact: true }).focus();
  await page.keyboard.press('Shift+Tab');
  await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toBeFocused();
  await expect.poll(async () => (await dialog.boundingBox())?.width ?? 0).toBeGreaterThanOrEqual(320);
  const bounds = await dialog.boundingBox();
  expect(bounds.x).toBeGreaterThanOrEqual(0);
  expect(bounds.x + bounds.width).toBeLessThanOrEqual(390);
  expect(bounds.width).toBeGreaterThanOrEqual(320);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: 'test-results/script-review-mobile.png' });
  await page.keyboard.press('Escape'); await expect(dialog).toHaveCount(0);
  await expect(page.locator('.script-assistant')).toBeVisible(); await expect(reviewButton).toBeFocused();
});


test('start an act around existing scenes without changing their content', async ({ page, request }) => {
  const id = await openProject(page, request); await draft(page);
  const original = (await state(request, id)).script.blocks;
  await page.locator('.outline-menu summary').first().click();
  await page.getByRole('button', { name: 'Start new act here', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Act]')).toHaveText('ACT 1');
  await expect(page.locator('.script-outline-item.in-act')).toHaveCount(2);
  await page.keyboard.press('Control+s');
  await expect.poll(async () => (await state(request, id)).script.blocks[0].kind).toBe('Act');
  expect((await state(request, id)).script.blocks.slice(1)).toEqual(original);
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Act]')).toHaveCount(0);
  await page.getByRole('button', { name: 'Redo', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Act]')).toHaveCount(1);
  await page.locator('.outline-menu summary').last().click();
  await page.getByRole('button', { name: 'Start new act here', exact: true }).click();
  await expect(page.locator('.script-canvas [data-kind=Act]')).toHaveCount(2);
  await expect(page.locator('.script-outline-item')).toHaveCount(4);
  expect(await page.locator('.script-canvas [data-kind=Act], .script-canvas [data-kind=Scene]').allTextContents()).toEqual(['ACT 1', 'INT. KITCHEN — NIGHT', 'ACT 2', 'EXT. GARDEN — DAWN']);
  await page.keyboard.press('Control+s');
  await expect.poll(async () => (await state(request, id)).script.blocks.filter(b => b.kind === 'Act').length).toBe(2);
  await page.reload(); await expect(page.locator('.script-outline-item.in-act')).toHaveCount(2);
  expect((await state(request, id)).script.blocks.filter(b => b.kind !== 'Act')).toEqual(original);
});
