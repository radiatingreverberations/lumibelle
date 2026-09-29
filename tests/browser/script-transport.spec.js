import { test, expect } from './fixtures.js';

test('a lost editor acknowledgement cannot hold newer snapshots indefinitely', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('button', { name: /^AI activity/ })).toBeVisible();
  await page.evaluate(async () => {
    const editor = await import('/_content/Lumibelle.UI/script-editor.js');
    const element = document.createElement('div'); element.id = 'transport-test-editor'; document.body.append(element);
    const calls = [];
    window.transportTest = { editor, element, calls };
    editor.mount(element, { invokeMethodAsync: async (name, snapshot) => {
      if (name !== "EditorChanged") return;
      calls.push(snapshot);
      if (calls.length === 1) await new Promise(resolve => { window.transportTest.release = resolve; });
    } }, [{ id: crypto.randomUUID(), kind: 'Action', spans: [{ text: 'Original.' }] }]);
  });
  await expect.poll(() => page.evaluate(() => window.transportTest.calls.length)).toBe(1);
  const canvas = page.locator('#transport-test-editor [contenteditable=true]');
  await canvas.click(); await page.keyboard.press('End'); await page.keyboard.type(' Newer draft while disconnected.');
  // Snapshots can also be sent while typing is still in progress; wait for the one with the whole sentence.
  await expect.poll(() => page.evaluate(() => window.transportTest.calls.at(-1).blocks[0].spans.map(s => s.text).join('')))
    .toBe('Original. Newer draft while disconnected.');
  const calls = await page.evaluate(() => window.transportTest.calls);
  expect(calls.length).toBeGreaterThan(1);
  expect(calls.at(-1).version).toBeGreaterThan(calls[0].version);
  expect(calls.at(-1).sequence).toBeGreaterThan(calls[0].sequence);
  await page.evaluate(() => {
    const t = window.transportTest; t.release();
    t.editor.acknowledge(t.element, t.calls.at(-1).version);
    t.editor.destroy(t.element); t.element.remove(); delete window.transportTest;
  });
});
