import { toolsTab, generateTakes, openShotSetup, composeProduction } from './workspace-tools.js';
import { test, expect } from './fixtures.js';
test.beforeEach(async ({ page }) => page.setDefaultTimeout(15000));

const setups = async (request, id) => (await (await request.get(`/fixtures/${id}/production`)).json()).compositions;
const shots = async (request, id) => (await (await request.get(`/fixtures/${id}/shots`)).json());
async function fixture(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shot-setup-summary select.generation-preset-select')).toBeVisible();
  const response = await request.post(`/fixtures/${id}/reference-video-take`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const take = await response.json();
  const lib = await (await request.post(`/fixtures/${id}/reference-reels?takeId=${take.id}&environment=true`)).json();
  await page.reload(); await expect(page.locator('.shot-setup-summary select.generation-preset-select')).toBeVisible();
  return { id, take, reels: lib.reels };
}
async function manage(page) { await toolsTab(page, 'References');
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  const dialog = page.locator('.manual-reference-dialog'); await expect(dialog).toBeVisible(); return dialog;
}
async function addFullReel(dialog, id) {
  await dialog.locator(`[data-reel-id="${id}"] .add-reel`).click();
  const selected = dialog.getByRole('tab', { name: /^Selected references/ });
  if (await selected.isVisible()) await selected.click();
  await dialog.getByRole('combobox', { name: 'Visuals', exact: true }).selectOption('FullReel');
}
async function apply(dialog) {
  await dialog.getByRole('button', { name: 'Apply changes', exact: true }).click(); await expect(dialog).not.toBeVisible();
}

test('reel replacement keeps the binding position and copies the new recipe guidance', async ({ page, request }) => {
  test.setTimeout(90000);
  const { id, take, reels } = await fixture(page, request);
  let dialog = await manage(page);
  await addFullReel(dialog, reels[0].id);
  await apply(dialog);
  await expect.poll(async () => (await setups(request, id))[0].inputs.videos.length).toBe(1);
  const original = (await setups(request, id))[0].inputs.videos[0];
  await page.reload();
  dialog = await manage(page);
  await expect(dialog.locator('.video-reference-list > li')).toHaveCount(1);
  await dialog.locator('.video-reference-list .reference-settings-button').click();
  await dialog.getByLabel('Name', { exact: true }).fill('Character reference');
  await dialog.getByRole('button', { name: 'Replace reel', exact: true }).click();
  await addFullReel(dialog, reels[1].id);
  await apply(dialog);
  const videos = (await setups(request, id))[0].inputs.videos;
  expect(videos).toHaveLength(1);
  expect(videos[0].id).toBe(original.id);
  expect(videos[0].name).toBe(reels[1].name);
  expect(videos[0].description).toBe(reels[1].useGuidance);
  expect(videos[0].media.id).not.toBe(original.media.id);
});

test('environment reel reference composes with declared context, captures audio once, and survives source deletion', async ({ page, request }) => {
  test.setTimeout(120000);
  const { id, take, reels } = await fixture(page, request);
  const dialog = await manage(page);
  await addFullReel(dialog, reels[0].id);
  await expect(dialog.getByLabel('Use audio', { exact: true })).toBeChecked();
  await dialog.getByLabel('Dialogue speaker (optional)').selectOption('JUNIPER');
  await dialog.getByLabel('Use guidance', { exact: true }).fill('Author-described character turn, appearance and clean voice. Keep new scene actions.');
  await page.screenshot({ path: 'artifacts/reference-videos-manager-desktop.png' });
  await apply(dialog);
  expect((await setups(request, id))[0].inputs.videos).toHaveLength(1);
  let setup = (await setups(request, id))[0]; const media = setup.inputs.videos[0].media;
  const url = `/media/projects/${id}/reference-videos/${media.id}`;
  const range = await request.get(url, { headers: { Range: 'bytes=12-43' } }); expect(range.status()).toBe(206); expect((await range.body()).length).toBe(32);
  expect((await request.head(url)).status()).toBe(200);
  await composeProduction(page);
  await expect(page.getByRole('textbox', { name: 'H3 prompt', exact: true })).toContainText('<Video 1>');
  const contexts = await (await request.get('/fixtures/compositions')).json();
  expect(contexts.at(-1).context.videos[0]).toMatchObject({ video: 1, audio: 1, speaker: 'JUNIPER', authorProvidedDescription: setup.inputs.videos[0].description });
  expect(contexts.at(-1).images).toEqual([]);
  await page.locator('[data-video="1"]').first().click();
  const playback = page.getByLabel('Reference video playback'); await expect(playback).toBeVisible();
  await expect.poll(() => playback.evaluate(v => v.readyState)).toBeGreaterThan(0);
  await playback.evaluate(async v => { v.muted = true; await v.play(); v.currentTime = 2; });
  await expect.poll(() => playback.evaluate(v => v.currentTime)).toBeGreaterThanOrEqual(2);
  // Scope to the player: a confirmation snackbar also has a Close button.
  await page.getByRole('dialog').filter({ has: playback }).getByRole('button', { name: 'Close', exact: true }).click();
  await expect(playback).toBeHidden();
  await expect(page.locator('.shot-setup-dialog')).toBeVisible();
  const editor = page.getByRole('textbox', { name: 'H3 prompt', exact: true });
  await editor.locator('[data-prompt-line]').last().click();
  await expect(editor).toBeFocused();
  await page.keyboard.press('Control+End');
  await page.keyboard.press('Enter'); await page.keyboard.type('Keep the target scene quiet.');
  await expect.poll(async () => (await setups(request, id))[0].prompt).toContain('Keep the target scene quiet.');
  setup = (await setups(request, id))[0];
  await generateTakes(page);
  const review = page.locator('.shot-review-dialog'); await expect(review).toBeVisible();
  await expect.poll(async () => (await shots(request, id)).takes.length).toBe(2);
  const generated = (await shots(request, id)).takes.find(t => t.id !== take.id);
  expect(generated.snapshot.prompt).toBe(setup.prompt); expect(generated.snapshot.shot.videos[0].media.sha256).toBe(media.sha256);
  await review.getByRole('button', { name: 'Use this take', exact: true }).click();
  await expect.poll(async () => (await shots(request, id)).shots[0].selectedTakeId).toBe(generated.id);
  expect((await request.post(`/fixtures/${id}/cut-trash/${take.id}`)).ok()).toBe(true);
  await expect.poll(async () => (await shots(request, id)).takes.length).toBe(1);
  expect((await request.get(url)).status()).toBe(200);
  await review.getByRole('button', { name: 'One more take', exact: true }).click();
  await expect.poll(async () => (await shots(request, id)).takes.length).toBe(2);
  expect((await shots(request, id)).takes.every(t => t.snapshot.prompt === setup.prompt)).toBe(true);
  await review.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(review).not.toBeVisible();
  await page.screenshot({ path: 'artifacts/reference-videos-desktop.png' });
});

test('reel ordering, guidance drafts and removal Undo work in the narrow tools drawer', async ({ page, request }) => {
  test.setTimeout(90000);
  const { id, reels } = await fixture(page, request);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: /Show Shot tools/ }).click();
  let dialog = await manage(page);
  await addFullReel(dialog, reels[0].id);
  await dialog.getByRole('tab', { name: 'Selected references (1)', exact: true }).click();
  await dialog.getByLabel('Use audio').uncheck();
  await dialog.getByLabel('Use guidance', { exact: true }).fill('A quiet alternate view.');
  await dialog.getByRole('tab', { name: 'Browse', exact: true }).click();
  await addFullReel(dialog, reels[1].id);
  await dialog.getByRole('tab', { name: 'Selected references (2)', exact: true }).click();
  await dialog.getByRole('button', { name: 'Move Video 2 up', exact: true }).click();
  await expect(dialog.locator('.video-reference-list > li').first().getByLabel('Name', { exact: true })).toHaveValue(reels[1].name);
  await page.screenshot({ path: 'artifacts/reference-videos-manager-narrow.png' });
  await apply(dialog);
  const saved = (await setups(request, id))[0]; expect(saved.inputs.videos[0].useSoundtrack).toBe(true); expect(saved.inputs.videos[1].description).toBe('A quiet alternate view.');
  dialog = await manage(page);
  await dialog.getByRole('tab', { name: 'Selected references (2)', exact: true }).click();
  await dialog.getByRole('button', { name: 'Remove Video 1', exact: true }).click();
  await apply(dialog);
  await page.getByRole('button', { name: 'Close Shot tools', exact: true }).click();
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect.poll(async () => (await setups(request, id))[0].inputs.videos.length).toBe(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'artifacts/reference-videos-narrow.png' });
});
