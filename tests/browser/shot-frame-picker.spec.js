import { test, expect } from './fixtures.js';
import { toolsTab } from './workspace-tools.js';

async function setup(page, request, width) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  // Initialize the current workspace before publishing takes; legacy migration retires pre-workspace takes.
  await request.post(`/fixtures/${id}/take-generation-setup`);
  const savedTake = async () => {
    const response = await request.post(`/fixtures/${id}/reference-video-take`);
    expect(response.ok(), await response.text()).toBe(true);
    return await response.json();
  };
  const first = await savedTake(); const second = await savedTake();
  const selected = await request.post(`/fixtures/${id}/select-saved-take/${first.id}`);
  expect(selected.ok(), await selected.text()).toBe(true);
  await request.post(`/fixtures/${id}/production-shot`);
  await request.post(`/fixtures/${id}/production-shot`);
  const doc = await (await request.get(`/fixtures/${id}/shots`)).json();
  expect(doc.takes).toHaveLength(2);
  expect(doc.takes[0].shotId).toBe(doc.shots[0].id);
  expect(doc.takes[0].snapshot.frameCount).toBeGreaterThan(1);
  const shots = doc.shots;
  const target = shots[2];
  await page.setViewportSize({ width, height: 950 });
  await page.goto(`/projects/${id}/shots?shotId=${target.id}`);
  await toolsTab(page, 'References');
  const editor = page.getByRole('dialog', { name: 'Manage references', exact: true });
  await page.getByRole('button', { name: 'Edit references', exact: true }).click();
  await editor.getByRole('button', { name: 'Juniper General · Juniper Add image', exact: true }).click();
  if (width < 600) await editor.getByRole('tab', { name: /Selected references/ }).click();
  await expect(editor.getByRole('heading', { name: 'Pictures · 1/9', exact: true })).toBeVisible();
  await editor.getByRole('combobox', { name: 'Voice for Juniper', exact: true }).selectOption('none');
  await editor.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(editor).toBeHidden();
  const open = async () => {
    await page.getByRole('button', { name: 'Edit references', exact: true }).click();
    await editor.getByRole('group', { name: 'Reference sources' }).getByRole('button', { name: 'Shot frames', exact: true }).click();
    await expect(editor.getByRole('button', { name: 'Shot frames', exact: true })).toHaveAttribute('aria-pressed', 'true');
  };
  const production = async () => (await (await request.get(`/fixtures/${id}/production`)).json());
  const attached = async () => (await production()).shotContent.find(s => s.shotId === target.id);
  return { id, shots, target, first, second, editor, open, production, attached };
}

for (const width of [1152, 390]) test(`shot-frame references are pinned, editable and cancellable at ${width}px`, async ({ page, request }) => {
  test.setTimeout(90000);
  const f = await setup(page, request, width);
  const { id, first, second, editor, open, production, attached } = f;
  const before = await production();
  await open();
  await expect(editor.getByRole('combobox', { name: 'Source shot', exact: true })).toHaveValue(f.shots[0].id);
  await expect(editor.getByRole('combobox', { name: 'Source take', exact: true })).toHaveValue(first.id);
  await expect(editor.getByRole('combobox', { name: 'Source frame position', exact: true })).toHaveValue('last');
  const picker = editor.locator('.shot-frame-picker');
  await expect(picker.locator('img')).toHaveAttribute('src', `/media/projects/${id}/takes/${first.id}/frames/${first.snapshot.frameCount - 1}`);
  await picker.getByRole('button', { name: 'Add picture', exact: true }).click();
  await expect(editor).toContainText('Added shot frame. Apply changes to save.');
  await editor.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect(await production()).toEqual(before);

  await open();
  await picker.getByRole('button', { name: 'Add picture', exact: true }).click();
  if (width < 600) await editor.getByRole('tab', { name: /Selected references/ }).click();
  await expect(editor.locator('.shot-frame-reference-list')).toContainText('Shot 1 · Take 1 · Last frame');
  await page.screenshot({ path: `artifacts/shot-frame-selected-${width}.png` });
  expect(await editor.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  await editor.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(editor).toBeHidden();
  const saved = await attached();
  expect(saved.continuityFrame.takeId).toBe(first.id); expect(saved.continuityFrame.frame).toBe(first.snapshot.frameCount - 1);
  const original = before.shotContent.find(c => c.shotId === f.target.id);
  expect(saved.images).toEqual(original.images);
  expect(saved.videos).toEqual(original.videos);
  expect(saved.characterVoices).toEqual(original.characterVoices);
  expect(saved.startFrame ?? null).toBe(null);

  // A newer selected take does not silently change the pinned reference.
  await request.post(`/fixtures/${id}/select-saved-take/${second.id}`);
  await page.reload(); await toolsTab(page, 'References');
  await open();
  await expect(editor.getByRole('combobox', { name: 'Source take', exact: true })).toHaveValue(first.id);
  expect((await attached()).continuityFrame).toEqual(saved.continuityFrame);
  await picker.getByRole('button', { name: 'Preview selected take', exact: true }).click();
  await editor.getByRole('combobox', { name: 'Source frame position', exact: true }).selectOption('first');
  await expect(picker.locator('img')).toHaveAttribute('src', `/media/projects/${id}/takes/${second.id}/frames/0`);
  await page.screenshot({ path: `artifacts/shot-frame-picker-${width}.png` });
  await picker.getByRole('button', { name: 'Replace shot frame', exact: true }).click();
  await editor.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(editor).toBeHidden();
  expect((await attached()).continuityFrame).toMatchObject({ id: saved.continuityFrame.id, takeId: second.id, frame: 0 });
  if (width < 600) await page.setViewportSize({ width: 1152, height: 950 });
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect.poll(async () => (await attached()).continuityFrame).toEqual(saved.continuityFrame);
  expect((await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id)).toHaveLength(0);
});

test('a source discarded during reference review cannot be applied', async ({ page, request }) => {
  const { id, first, editor, open, production } = await setup(page, request, 1152);
  const before = await production();
  await open();
  await editor.getByRole('button', { name: 'Add picture', exact: true }).click();
  expect((await request.post(`/fixtures/${id}/cut-trash/${first.id}`)).ok()).toBe(true);
  await editor.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(editor).toContainText('The selected shot frame is no longer available');
  expect(await production()).toEqual(before);
  await editor.getByRole('button', { name: 'Cancel', exact: true }).click();
});
