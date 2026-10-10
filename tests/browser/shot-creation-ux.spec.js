import { test, expect } from './fixtures.js';
import { composeProduction, closeShotSetup } from './workspace-tools.js';

async function openProject(page, request, existing = true, alternativeSetup = false) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  if (existing) await request.post(`/fixtures/${id}/production-shot`);
  if (alternativeSetup) {
    await request.post(`/fixtures/${id}/images`);
    await request.post(`/fixtures/${id}/legacy-setup-inputs`);
  }
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  const state = async () => (await (await request.get(`/fixtures/${id}/shots`)).json());
  return { id, state };
}

for (const existing of [false, true]) {
  test(`undo an autosaved added shot restores a usable ${existing ? 'existing' : 'empty'} workspace`, async ({ page, request }) => {
    const { id, state } = await openProject(page, request, existing, existing);
    let preset;
    if (existing) {
      const setup = page.getByRole('combobox', { name: 'Generation setup', exact: true });
      await setup.selectOption({ label: 'Earlier alternative' });
      preset = await setup.inputValue();
    }
    const original = (await state()).shots;
    await page.getByRole('button', { name: '+ Shot', exact: true }).click();
    await expect(page.getByLabel('Shot title', { exact: true })).toHaveValue('Untitled shot');
    await expect(page.locator('.shot-start-guide')).toContainText('compose the prompt');
    await expect.poll(async () => (await state()).shots.length).toBe(original.length + 1);
    // The production composition must exist: this is the case that used to leave
    // Undo pointing at the removed shot and make every subsequent save fail.
    await expect(page.getByRole('button', { name: 'Manage references', exact: true })).toBeVisible();
    await expect(page.locator('.shots-heading [role=status]')).toHaveText('Saved');
    await page.locator('.shots-heading').getByRole('button', { name: 'Undo', exact: true }).click();
    await expect.poll(async () => (await state()).shots.map(s => s.id)).toEqual(original.map(s => s.id));
    await expect(page.locator('.shots-heading [role=status]')).toHaveText('Saved');
    await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();
    await expect(page.getByRole('alert')).toHaveCount(0);
    if (existing) {
      await expect(page.getByLabel('Shot title', { exact: true })).toHaveValue(original[0].title);
      await expect(page.getByRole('combobox', { name: 'Generation setup', exact: true })).toHaveValue(preset);
    }
    else await expect(page.getByRole('heading', { name: 'Add your first shot' })).toBeVisible();
    await page.getByRole('button', { name: '+ Shot', exact: true }).click();
    await expect(page.getByLabel('Shot title', { exact: true })).toHaveValue('Untitled shot');
    await expect.poll(async () => (await state()).shots.length).toBe(original.length + 1);
    expect(await (await request.get('/fixtures/ai-jobs')).json()).toEqual([]);
  });
}

test('new shots stay beside the current shot and remain visible through filters', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const script = await (await request.post(`/fixtures/${id}/coverage-script`)).json();
  const scenes = script.blocks.filter(b => b.kind === 'Scene');
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  const state = async () => (await (await request.get(`/fixtures/${id}/shots`)).json());
  await page.getByRole('button', { name: '+ Shot', exact: true }).click();
  await page.getByLabel('Shot title', { exact: true }).fill('Current scene shot');
  await page.getByRole('combobox', { name: 'Scene', exact: true }).selectOption(scenes[1].id);
  await expect.poll(async () => (await state()).shots[0]?.sceneId).toBe(scenes[1].id);
  await page.getByRole('searchbox', { name: 'Search shots' }).fill('Current scene shot');
  await page.getByRole('button', { name: '+ Shot', exact: true }).click();
  await expect(page.getByRole('searchbox', { name: 'Search shots' })).toHaveValue('');
  await expect(page.getByRole('combobox', { name: 'Scene', exact: true })).toHaveValue(scenes[1].id);
  await expect(page.locator('.shot-list [data-shot-row]')).toHaveCount(2);
  await expect.poll(async () => (await state()).shots.length).toBe(2);
  expect((await state()).shots.map(s => s.sceneId)).toEqual([scenes[1].id, scenes[1].id]);
});

test('single delete names one shot, supports cancel, deletion and undo without a bulk picker', async ({ page, request }) => {
  const { state } = await openProject(page, request);
  const original = (await state()).shots[0];
  await page.getByRole('button', { name: /^Actions for shot 1:/ }).click();
  await page.getByRole('menuitem', { name: 'Delete', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Delete shot?', exact: true });
  await expect(dialog).toContainText(original.title);
  await expect(dialog.locator('.shot-picker')).toHaveCount(0);
  await expect(dialog).toContainText('Trash for 30 days');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await state()).shots.map(s => s.id)).toEqual([original.id]);
  await page.getByRole('button', { name: /^Actions for shot 1:/ }).click();
  await page.getByRole('menuitem', { name: 'Delete', exact: true }).click();
  await dialog.getByRole('button', { name: 'Delete 1 shot', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect.poll(async () => (await state()).shots.length).toBe(0);
  await page.locator('.shots-heading').getByRole('button', { name: 'Undo', exact: true }).click();
  await expect.poll(async () => (await state()).shots.map(s => s.id)).toEqual([original.id]);
  await expect(page.locator('.shots-heading [role=status]')).toHaveText('Saved');
});

test('a fresh project can create and compose standalone shots without a script', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await page.goto(`/projects/${id}/shots`);
  await expect(page.getByRole('heading', { name: 'Add your first shot' })).toBeVisible();
  await expect(page.locator('.shot-empty-guide')).toContainText('no script required');
  await page.getByRole('button', { name: 'Add blank shot', exact: true }).click();
  await page.getByLabel('Shot title', { exact: true }).fill('A light in the alley');
  await page.getByLabel('Action and camera', { exact: true }).fill('Slowly push toward a flickering street lamp.');
  await page.getByRole('spinbutton', { name: 'Duration (seconds)', exact: true }).fill('5');
  await page.getByRole('spinbutton', { name: 'Duration (seconds)', exact: true }).press('Tab');
  await expect(page.getByRole('combobox', { name: 'Scene', exact: true })).toHaveValue('');
  await expect(page.locator('.shots-heading [role=status]')).toHaveText('Saved');
  await composeProduction(page);
  const requests = await (await request.get('/fixtures/compositions')).json();
  expect(JSON.stringify(requests.at(-1).context)).toContain('flickering street lamp');
  await closeShotSetup(page);
  await page.reload();
  await expect(page.getByLabel('Shot title', { exact: true })).toHaveValue('A light in the alley');
  const shots = (await (await request.get(`/fixtures/${id}/shots`)).json()).shots;
  expect(shots[0].sceneId).toBeNull();
  expect(shots[0].approvedScriptId).toBeNull();
  expect((await (await request.get(`/fixtures/${id}`)).json()).script.blocks.some(b => b.spans.some(s => s.text.trim()))).toBe(false);
});

test('a shot can be unlinked from its script without losing its direction', async ({ page, request }) => {
  const { state } = await openProject(page, request);
  const original = (await state()).shots[0];
  await page.getByRole('combobox', { name: 'Scene', exact: true }).selectOption('');
  await expect.poll(async () => (await state()).shots[0].sceneId).toBeNull();
  const unlinked = (await state()).shots[0];
  expect(unlinked.description).toBe(original.description);
  expect(unlinked.sourceBlockIds).toEqual([]);
  expect(unlinked.approvedScriptId).toBeNull();
});
