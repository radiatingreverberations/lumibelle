import { test, expect } from './fixtures.js';
import { reelTools, reelSetup, showToolsPane as tools, openReelSetup, closeReelSetup, expand, reelFraming, openReelAssist, chooseReelReferences, useVisionModel } from './reel-tools.js';
const library = async (request, id) => (await (await request.get(`/fixtures/${id}`)).json()).assets;
for (const narrow of [false, true]) test(`prop camera presets orbit, turn and hold the object (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`); await request.post(`/fixtures/${id}/prop-reel-owner`);
  const asset = (await library(request, id)).assets[0];
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/assets?assetId=${asset.id}&view=reels`);
  await expect(page.getByLabel('Filter media', { exact: true })).toHaveValue('Reels');
  await expect(page.locator('.asset-import-menu button', { hasText: 'Import reel' })).toHaveCount(1);
  await tools(page);
  const editor = reelTools(page), setup = reelSetup(page), requested = editor.getByLabel('Requested seconds', { exact: true });
  await expect(editor.getByRole('combobox', { name: 'Aspect', exact: true })).toHaveValue('1:1');
  await expect(requested).toHaveValue('15');
  await expect(editor.getByLabel('Reel look', { exact: true })).toHaveCount(0);
  await openReelSetup(page);
  await expand(setup.locator('.reel-voice-options'));
  await expect(setup.locator('.reel-voice-options > summary')).toHaveText('Audio · Silent');
  await expect(setup.getByText('Silent visual reference · no dialogue or audio inputs.')).toBeVisible();
  await expect(setup.getByLabel('Voice mode')).toHaveCount(0);
  await reelFraming(page);
  await expect(setup.getByLabel('Camera preset', { exact: true })).toHaveValue('PropOrbit');
  const presets = [
    ['PropHalfOrbit', 'Half orbit', 8, true], ['PropTurntable', 'Turntable', 12, true], ['PropHeldViews', 'Held angles', 12, false],
    ['PropRise', 'Rise to top view', 6, false], ['PropDetail', 'Detail pass', 6, false], ['PropCustom', 'Custom move', 10, false],
    ['PropOrbit', '360° orbit', 15, true]
  ];
  const framings = { PropOrbit: 21, PropHalfOrbit: 22, PropTurntable: 23, PropHeldViews: 24, PropRise: 25, PropDetail: 26, PropCustom: 27 };
  for (const [value, label, seconds, direction] of presets) {
    await setup.getByLabel('Camera preset', { exact: true }).selectOption({ label });
    await expect(requested).toHaveValue(String(seconds));
    await expect(setup.getByLabel('Camera direction', { exact: true })).toHaveCount(direction ? 1 : 0);
    await expect(setup.getByText(/Use keyframes to reference all angles/)).toHaveCount(seconds >= 10 ? 1 : 0);
    await expect.poll(async () => (await library(request, id)).reelDrafts[0]?.framing).toBe(framings[value]);
    const draft = (await library(request, id)).reelDrafts[0];
    expect(draft.presetVersion).toBe('prop-reel-v1'); expect(draft.duration).toBe(seconds);
  }
  await expect(setup.locator('.reel-camera-summary')).toContainText('Circle the stationary prop');
  await expand(setup.locator('.reel-camera-timeline'));
  await expect(setup.locator('.reel-framing-plan')).toContainText('orbits right around the stationary prop');
  await expect(setup.locator('.reel-framing-plan')).toContainText('The prop itself never rotates');
  await setup.getByLabel('Camera direction').selectOption('Left');
  await expect(setup.locator('.reel-framing-plan')).toContainText('orbits left around the stationary prop');
  await setup.getByLabel('Camera preset').selectOption('PropTurntable');
  await expect(setup.locator('.reel-framing-plan')).toContainText('concealed turntable');
  await setup.getByLabel('Camera preset').selectOption('PropHeldViews');
  await expect(setup.locator('.reel-framing-plan')).toContainText('[Shot 4]');
  await expect(setup.locator('.reel-framing-plan')).toContainText('the rear view');
  await setup.getByLabel('Camera preset').selectOption('PropCustom');
  // A custom move needs Instructions before Assist can compose.
  const assist = await openReelAssist(page);
  await expect(assist.getByText('Describe the camera move in Instructions: starting view, path around the prop and end view.', { exact: true })).toBeVisible();
  await expect(assist.getByRole('button', { name: 'Compose pair', exact: true })).toBeDisabled();
  await page.screenshot({ path: `artifacts/prop-presets-${narrow ? 'narrow' : 'desktop'}.png` });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('prop reel composes, generates and is offered to Shots', async ({ page, request }) => {
  test.setTimeout(150000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images?patterned=true`); await request.post(`/fixtures/${id}/prop-reel-owner`);
  const asset = (await library(request, id)).assets[0];
  await page.setViewportSize({ width: 1173, height: 1000 });
  await page.goto(`/projects/${id}/assets?assetId=${asset.id}&view=reels`); await tools(page);
  const editor = reelTools(page), setup = reelSetup(page);
  const pictures = await chooseReelReferences(page, [`${asset.id}/${asset.images[0].id}`]);
  await pictures.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(pictures).not.toBeVisible();
  await openReelSetup(page);
  await setup.getByLabel('Reel name', { exact: true }).fill('Armchair orbit');
  const prompt = setup.getByRole('textbox', { name: 'H3 prompt', exact: true });
  const assist = await openReelAssist(page);
  await useVisionModel(page, assist);
  await assist.getByRole('button', { name: 'Compose pair', exact: true }).click();
  await expect(assist).not.toBeVisible();
  await expect(prompt).toContainText('prop reference reel');
  const composed = (await (await request.get('/fixtures/reel-compositions')).json()).find(c => c.context.prop?.assetId === asset.id);
  expect(composed.context.prop.visualNotes).toBe(asset.description);
  expect(composed.context.presetViews).toContain('orbits right around the stationary prop');
  const draft = (await library(request, id)).reelDrafts[0];
  expect(draft.presetVersion).toBe('prop-reel-v1'); expect(draft.voiceMode).toBe(2);
  expect(draft.useGuidance).toContain('Visual prop reference'); expect(draft.prompt).not.toContain('<Audio');
  await closeReelSetup(page);
  await editor.getByRole('button', { name: 'Generate reel', exact: true }).click();
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(1);
  const reel = (await library(request, id)).reels[0];
  expect(reel.generation.snapshot.profile).toBe('prop-reel-v1'); expect(reel.assetId).toBe(asset.id);
  await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`);
  await page.goto(`/projects/${id}/shots`); await tools(page);
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  const picker = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Manage references', exact: true }) });
  const card = picker.locator(`[data-reel-id="${reel.id}"]`);
  await expect(card).toContainText('Armchair orbit');
  await expect(card.locator('.media-kind')).toHaveAttribute('title', /^Prop reel ·/);
  await card.locator('.add-reel').click();
  await expect(picker.getByLabel('Visuals', { exact: true })).toHaveValue('Keyframes');
  await picker.getByLabel('Visuals', { exact: true }).selectOption('FullReel');
  await picker.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(picker).not.toBeVisible();
  const composition = (await (await request.get(`/fixtures/${id}/production`)).json()).compositions[0];
  expect(composition.inputs.videos[0].media.id).toBe(reel.media.id);
  expect(composition.inputs.videos[0].ownerCategory).toBe(2);
  expect(composition.inputs.videos[0].useSoundtrack).toBe(false);
});

test('a short window leaves the reel recipe room to scroll between its fixed header and Generate footer', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`); await request.post(`/fixtures/${id}/prop-reel-owner`);
  const asset = (await library(request, id)).assets[0];
  await page.setViewportSize({ width: 1150, height: 745 });
  await page.goto(`/projects/${id}/assets?assetId=${asset.id}&view=reels`);
  await tools(page);
  const editor = reelTools(page), setup = reelSetup(page), footer = editor.locator('.workspace-pane-footer');
  await expect(footer.getByRole('button', { name: 'Prompt', exact: true })).toBeAttached();
  // The headers and footer squeezed the recipe to about 160 pixels; the compact footer and headers keep it usable.
  expect(await editor.locator('.workspace-pane-body').evaluate(b => b.clientHeight)).toBeGreaterThanOrEqual(240);
  await expect(editor.locator('.workspace-pane-body').getByRole('button', { name: 'Manage references', exact: true })).toBeVisible();
  // The Prompt step says what Generate is missing.
  await expect(footer.getByRole('button', { name: 'Prompt', exact: true })).toContainText('Missing');
  await expect(footer.getByRole('button', { name: 'Prompt', exact: true })).toBeInViewport();
  await expect(footer.locator('[title="Add a prompt and use guidance in Prompt."]')).toHaveCount(1);
  await expect(editor.getByRole('button', { name: 'Generate reel', exact: true })).toBeDisabled();
  await expect(editor.getByRole('button', { name: 'Generate reel', exact: true })).toBeInViewport();
  // The Prompt dialog scrolls its recipe between its own fixed title and Done.
  await reelFraming(page);
  await expect(setup.getByRole('button', { name: 'Done', exact: true })).toBeInViewport();
  await setup.getByRole('textbox', { name: 'H3 prompt', exact: true }).scrollIntoViewIfNeeded();
  await expect(setup.getByRole('textbox', { name: 'H3 prompt', exact: true })).toBeInViewport();
  await expect(setup.getByRole('button', { name: 'Done', exact: true })).toBeInViewport();
  await expect(setup.getByRole('button', { name: 'Close reel prompt', exact: true })).toBeInViewport();
});
