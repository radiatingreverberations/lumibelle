import { test, expect } from './fixtures.js';
import { cropReference, toolsTab } from './workspace-tools.js';
test.beforeEach(async ({ page }) => page.setDefaultTimeout(15000));
const library = async (request, id) => (await (await request.get(`/fixtures/${id}`)).json()).assets;
async function fixture(page, request, narrow = false) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  const assets = await library(request, id), owner = assets.assets[0];
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/assets?assetId=${owner.id}&view=reels`);
  await expect(page.getByLabel('Filter media', { exact: true })).toHaveValue('Reels');
  await expect(page.getByRole('button', { name: 'Create reel', exact: true })).toHaveCount(0);
  await expect(page.locator('.reel-tools [data-prompt-ready]')).toHaveAttribute('data-prompt-ready', 'true');
  return { id, owner };
}
async function showTools(page) {
  const workspace = page.locator('.studio-workspace');
  await expect(workspace).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.asset-list-row.selected')).toHaveCount(1);
  const toggle = page.locator('[data-toggle-pane=right][aria-controls="workspace-Assets-right"]');
  await expect(async () => {
    if (!await page.locator('.workspace-right').isVisible()) await toggle.click({ timeout: 1000 });
    await expect(page.locator('.workspace-right')).toBeVisible();
  }).toPass({ timeout: 10000 });
}
async function recipe(page, owner, framing = "Custom", voice = "NewVoice", guidance = null, compose = framing !== "Custom", crop = false) {
  await showTools(page);
  const dialog = page.locator('.reel-tools');
  await expect(dialog.locator('[data-prompt-ready]')).toHaveAttribute('data-prompt-ready', 'true');

  await dialog.getByLabel('Voice mode').selectOption(voice);
  await dialog.getByRole('button', { name: 'Manage reel references' }).click();
  const pictures = page.locator('.reel-pictures-dialog');
  await pictures.locator(`[data-reference="${owner.id}/${owner.images[0].id}"]`).click();
  if ((crop || guidance) && page.viewportSize().width < 650) await pictures.getByRole('tab', { name: 'Selected references (1)', exact: true }).click();
  if (crop) { await cropReference(page, pictures); }
  if (guidance) { await pictures.getByRole('button', { name: /Customize/ }).click(); await pictures.getByRole('textbox', { name: 'Composition preservation override', exact: true }).fill(guidance); }
  await pictures.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(pictures).not.toBeVisible();
  await dialog.getByLabel('Reel name', { exact: true }).fill('Riley reel');

  await expect(dialog.getByRole('textbox', { name: 'H3 prompt', exact: true })).toBeEmpty();
  await expect(dialog.getByLabel('Use guidance', { exact: true })).toHaveValue('');
  await dialog.getByRole('button', { name: 'Reel assistance', exact: true }).click();
  const assist = page.locator('.ai-assist-dialog');
  await assist.getByLabel('Framing preset', { exact: true }).selectOption(framing);
  if (compose) {
    await selectVisionModel(page, assist);
    await assist.getByRole('button', { name: 'Compose pair', exact: true }).click();
    await expect(assist).not.toBeVisible();
    await expect(dialog.getByRole('textbox', { name: 'H3 prompt', exact: true })).toContainText('subject_definitions:');
  } else {
    await assist.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(assist).toBeHidden();
    await expect(dialog.getByRole('button', { name: 'Reel assistance', exact: true })).toBeFocused();
  }
  return dialog;
}
async function selectVisionModel(page, assist) {
  await assist.locator('.model-chip').click();
  await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await page.getByRole('button', { name: 'Set as project default', exact: true }).click();
}

async function close(dialog) {
  const page = dialog.page();
  const narrow = page.viewportSize().width < 1100;
  if (narrow) await page.getByRole('button', { name: 'Close Asset tools', exact: true }).click();
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-right-visible', narrow ? 'false' : 'true');
}

for (const narrow of [false, true]) for (const outcome of ['success', 'edited', 'cancelled']) test(`queued reels prevent duplicates and ${outcome} keeps the correct create draft (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(90000);
  const { id, owner } = await fixture(page, request, narrow);
  const tools = await recipe(page, owner, 'SideRearFace', 'Silent');
  const prompt = tools.getByRole('textbox', { name: 'H3 prompt', exact: true });
  const originalPrompt = (await library(request, id)).reelDrafts.find(d => d.assetId === owner.id).prompt;
  const jobs = async () => (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id && j.kind === 'ReelVideo');
  async function pause(value) {
    await close(tools);
    await page.locator('.ai-activity-trigger').click();
    const activity = page.getByRole('dialog', { name: 'AI activity', exact: true });
    await activity.getByRole('button', { name: `${value ? 'Pause' : 'Resume'} queue ComfyUI`, exact: true }).click();
    await activity.getByRole('button', { name: 'Close AI activity' }).click();
    await showTools(page);
  }
  await pause(true);
  try {
    await tools.getByRole('button', { name: 'Generate reel', exact: true }).click();
    await expect(tools.getByRole('button', { name: 'Queued…', exact: true })).toBeDisabled();
    expect(await jobs()).toHaveLength(1);
    await page.reload(); await showTools(page);
    await expect(tools.getByRole('button', { name: 'Queued…', exact: true })).toBeDisabled();
    await expect(prompt).toHaveText(originalPrompt, { useInnerText: true });
    await tools.getByRole('button', { name: 'View request', exact: true }).click();
    const requestDialog = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Reference reel request', exact: true }) });
    await expect(requestDialog).toBeVisible();
    if (outcome === 'cancelled') await requestDialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await requestDialog.getByRole('button', { name: 'Close', exact: true }).click();
    if (outcome === 'edited') await tools.getByLabel('Use guidance', { exact: true }).fill('My next reel guidance');
  } finally { await pause(false); }
  await expect.poll(async () => (await jobs())[0]?.state).toBe(outcome === 'cancelled' ? 'Cancelled' : 'Completed');
  if (outcome === 'success') {
    await expect(prompt).toBeEmpty();
    await expect(tools.getByLabel('Use guidance', { exact: true })).toHaveValue('');
    await expect(tools.locator('.reel-picture-summary')).toHaveCount(0);
    await expect(tools.getByRole('button', { name: 'Generate reel', exact: true })).toBeDisabled();
    await page.reload(); await showTools(page);
    await expect(prompt).toBeEmpty();
    const saved = (await library(request, id)).reels[0];
    expect(saved.generation.recipe.prompt).toBe(originalPrompt);
    await close(tools);
    await page.locator(`[data-media-id="${saved.id}"]`).getByRole('button', { name: `Actions for ${saved.name}` }).click();
    await page.getByRole('menuitem', { name: 'Create similar', exact: true }).click();
    await expect(prompt).toHaveText(originalPrompt, { useInnerText: true });
    await expect(tools.locator('.reel-picture-summary')).toHaveCount(1);
  } else {
    await expect(prompt).toHaveText(originalPrompt, { useInnerText: true });
    await expect(tools.locator('.reel-picture-summary')).toHaveCount(1);
    if (outcome === 'edited') await expect(tools.getByLabel('Use guidance', { exact: true })).toHaveValue('My next reel guidance');
  }
  expect(await jobs()).toHaveLength(1);
});

for (const narrow of [false, true]) test(`saved reel directions can be copied and reused with other pictures (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(120000);
  await page.addInitScript(() => Object.defineProperty(navigator, 'clipboard', { value: { writeText: async text => { window.copiedReelDirections = text; } } }));
  const { id, owner } = await fixture(page, request, narrow);
  const tools = await recipe(page, owner, 'Custom', 'Silent', null, false);
  await tools.getByRole('button', { name: 'Reel assistance', exact: true }).click();
  const assist = page.locator('.ai-assist-dialog');
  const directions = 'Orbit slowly to the left — posé.\nHold the final angle for a moment.';
  await assist.getByLabel('Instructions', { exact: true }).fill(directions);
  await selectVisionModel(page, assist);
  await assist.getByRole('button', { name: 'Compose pair', exact: true }).click();
  await expect(assist).not.toBeVisible();
  await expect(tools.getByRole('button', { name: 'Generate reel', exact: true })).toBeEnabled();
  await tools.getByRole('button', { name: 'Generate reel', exact: true }).click();
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(1);
  const saved = (await library(request, id)).reels[0];
  expect(saved.generation.recipe.instructions).toBe(directions);
  await close(tools);
  await page.locator(`[data-media-id="${saved.id}"]`).getByRole('button', { name: `Actions for ${saved.name}` }).click();
  await page.getByRole('menuitem', { name: 'Edit details', exact: true }).click();
  const details = page.locator('.reel-details-dialog');
  await expect(details.getByLabel('Saved directions', { exact: true })).toHaveValue(directions);
  const preview = details.locator('.media-details-preview');
  const guidance = preview.locator('details').filter({ has: page.locator('summary').getByText('Use guidance', { exact: true }) });
  const capturedRecipe = preview.locator('details').filter({ has: page.locator('summary').getByText('Captured recipe', { exact: true }) });
  await expect(guidance).not.toHaveAttribute('open');
  await expect(capturedRecipe).not.toHaveAttribute('open');
  await expect(details.locator('.media-details-editor').getByLabel('Name', { exact: true })).toBeVisible();
  const videoBox = await preview.locator('video').boundingBox();
  const guidanceBox = await guidance.locator('summary').boundingBox();
  const editorBox = await details.locator('.media-details-editor').boundingBox();
  expect(guidanceBox.y).toBeGreaterThanOrEqual(videoBox.y + videoBox.height);
  if (narrow) {
    expect(editorBox.y).toBeGreaterThan(guidanceBox.y);
    expect(videoBox.height).toBeLessThanOrEqual(page.viewportSize().height * 0.28 + 1);
  } else expect(editorBox.x).toBeGreaterThanOrEqual(videoBox.x + videoBox.width);
  await page.screenshot({ path: `artifacts/reel-details-balanced-${narrow ? 'narrow' : 'desktop'}.png` });
  await guidance.locator('summary').focus();
  await page.keyboard.press('Enter');
  await expect(guidance.getByLabel('Use guidance', { exact: true })).toHaveValue(saved.useGuidance);
  await capturedRecipe.locator(':scope > summary').click();
  const saveBox = await details.getByRole('button', { name: 'Save details', exact: true }).boundingBox();
  expect(saveBox.y + saveBox.height).toBeLessThanOrEqual(page.viewportSize().height);
  expect(await details.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
  await guidance.locator('summary').click();
  await capturedRecipe.locator(':scope > summary').click();
  await details.getByRole('button', { name: 'Copy directions', exact: true }).click();
  await expect.poll(() => page.evaluate(() => window.copiedReelDirections)).toBe(directions);
  await details.getByRole('button', { name: 'Close', exact: true }).click();

  const target = (await library(request, id)).assets.find(a => a.id !== owner.id);
  await page.goto(`/projects/${id}/assets?assetId=${target.id}&view=reels`);
  await recipe(page, target, 'BodyToFace', 'NewVoice', 'Different picture guidance', false);
  await tools.getByRole('textbox', { name: 'H3 prompt', exact: true }).fill('Keep my authored prompt.');
  await tools.getByLabel('Use guidance', { exact: true }).fill('Keep my target use guidance.');
  await tools.getByLabel('Requested seconds', { exact: true }).fill('7');
  await tools.getByRole('combobox', { name: 'Aspect', exact: true }).selectOption('16:9');
  await tools.getByRole('button', { name: 'Reel assistance', exact: true }).click();
  await assist.getByLabel('Instructions', { exact: true }).fill('Current directions before reuse.');
  const draft = async () => (await library(request, id)).reelDrafts.find(d => d.assetId === target.id);
  await expect.poll(async () => (await draft()).instructions).toBe('Current directions before reuse.');
  const before = await draft();
  await assist.getByText('Reuse directions from a reel', { exact: true }).click();
  await assist.getByLabel('Saved reel', { exact: true }).selectOption(saved.id);
  await expect(assist.getByLabel('Directions to reuse', { exact: true })).toHaveValue(directions);
  await expect(assist.getByLabel('Instructions', { exact: true })).toHaveValue(before.instructions);
  await assist.getByRole('button', { name: 'Use these directions', exact: true }).click();
  await expect(assist.getByLabel('Instructions', { exact: true })).toHaveValue(directions);
  await expect(assist.getByLabel('Framing preset', { exact: true })).toHaveValue('Custom');
  await expect(assist.getByRole('status').filter({ hasText: 'Directions loaded.' })).toBeVisible();
  await page.screenshot({ path: `artifacts/reel-directions-${narrow ? 'narrow' : 'desktop'}.png` });
  const after = await draft();
  for (const field of ['images', 'voiceMode', 'speaker', 'line', 'duration', 'aspect', 'prompt', 'useGuidance', 'lookId']) expect(after[field]).toEqual(before[field]);
  expect(after.images[0].assetId).toBe(target.id);
  await assist.getByRole('button', { name: 'Close', exact: true }).click();
  await page.reload(); await showTools(page);
  await tools.getByRole('button', { name: 'Reel assistance', exact: true }).click();
  await expect(assist.getByLabel('Instructions', { exact: true })).toHaveValue(directions);
  await assist.getByRole('button', { name: 'Revise pair', exact: true }).click();
  await expect(assist).not.toBeVisible();
  const captures = async () => (await (await request.get('/fixtures/reel-compositions')).json()).filter(c => c.context.request?.projectId === id);
  await expect.poll(async () => (await captures()).length).toBe(2);
  const captured = await captures();
  expect(captured[1].context.request.draft.instructions).toBe(directions);
  expect(captured[1].context.request.draft.images[0].assetId).toBe(target.id);
  expect(captured[1].context.request.draft.prompt).toBe(before.prompt);
  expect((await library(request, id)).reels[0].generation).toEqual(saved.generation);
});

for (const narrow of [false, true]) test(`preset generation saves independent reels, captured regeneration, playback, recovery and exact reuse in Shots (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(150000);
  const { id, owner } = await fixture(page, request, narrow);
  await expect(page.getByRole('button', { name: 'Generate images', exact: true })).not.toBeVisible();
  const dialog = await recipe(page, owner, 'SideRearFace', 'Silent');
  await expect(dialog.getByRole('textbox', { name: 'H3 prompt' })).toContainText('[Shot 3]');
  await expect(dialog.getByText(/Generated duration: 5[,.]167 seconds/)).toBeVisible();
  await close(dialog);
  await page.reload(); await showTools(page);
  await expect(dialog.getByRole('textbox', { name: 'H3 prompt' })).toContainText('[Shot 3]');
  await dialog.getByRole('button', { name: 'Generate reel', exact: true }).click();
  await expect(dialog.getByRole('textbox', { name: 'H3 prompt', exact: true })).toBeEmpty();
  await expect(dialog.getByRole('button', { name: 'Generate reel', exact: true })).toBeDisabled();
  await expect(page.locator('.mud-dialog:visible')).toHaveCount(0);
  await close(dialog);
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(1);
  await expect(page.locator('.asset-media-card[data-media-kind=Reel]')).toHaveCount(1);
  const original = (await library(request, id)).reels[0];
  await page.locator('.asset-media-card[data-media-kind=Reel] .media-select').first().click();
  const player = page.locator('.reel-details-tools video');
  await expect.poll(() => player.evaluate(v => v.readyState)).toBeGreaterThan(0);
  await player.evaluate(async v => { v.muted = true; await v.play(); v.currentTime = 2; });
  await expect.poll(() => player.evaluate(v => v.currentTime)).toBeGreaterThanOrEqual(2);
  await page.screenshot({ path: 'artifacts/reference-reels-desktop.png' });
  await expect(page.locator('.reel-details-tools')).toBeVisible();
  await page.locator('.reel-details-tools').getByRole('button', { name: 'Edit details', exact: true }).click();
  const details = page.locator('.reel-details-dialog');
  const actions = details.locator('.mud-dialog-actions');
  for (const name of ['Manage keyframes…', 'Regenerate…', 'Create similar', 'Close', 'Save details']) {
    const button = actions.getByRole('button', { name, exact: true });
    await expect(button).toBeVisible();
    const box = await button.boundingBox();
    expect(box.y).toBeGreaterThanOrEqual(0);
    expect(box.y + box.height).toBeLessThanOrEqual(page.viewportSize().height);
  }
  await page.screenshot({ path: `artifacts/reel-footer-${narrow ? 'narrow' : 'desktop'}.png` });
  await actions.getByRole('button', { name: 'Manage keyframes…', exact: true }).click();
  await page.locator('.keyframe-editor').getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(actions.getByRole('button', { name: 'Manage keyframes…', exact: true })).toBeFocused();
  await details.locator('summary').getByText('Use guidance', { exact: true }).click();
  await details.getByRole('textbox', { name: 'Use guidance', exact: true }).fill('Reviewed appearance: use the face and hands.');
  await page.getByRole('button', { name: 'Save details' }).click();
  await page.locator('.reel-details-tools').getByRole('button', { name: 'Edit details', exact: true }).click();
  await actions.getByRole('button', { name: 'Regenerate…', exact: true }).click();
  const confirmMore = page.locator('.repeat-generation-dialog');
  await expect(confirmMore).toBeVisible();
  expect((await library(request, id)).reels).toHaveLength(1);
  await confirmMore.getByRole('button', { name: 'Cancel', exact: true }).click();
  await actions.getByRole('button', { name: 'Regenerate…', exact: true }).click();
  await confirmMore.getByRole('button', { name: 'Queue 1 reel', exact: true }).click();
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(2);
  let reels = (await library(request, id)).reels;
  const second = reels.find(r => r.id !== original.id);
  expect(second.generation.snapshot.prompt).toBe(original.generation.snapshot.prompt);
  expect(second.generation.seed).not.toBe(original.generation.seed);
  expect(second.generation.snapshot.reel.regenerationSource.reelId).toBe(original.id);
  expect(second.media.id).not.toBe(original.media.id);
  expect((await (await request.get(`/fixtures/${id}/shots`)).json()).shots).toHaveLength(0);
  await actions.getByRole('button', { name: 'Create similar' }).click();
  await expect(details).not.toBeVisible();

  await expect(dialog.getByLabel('Reel name', { exact: true })).toHaveValue('Riley reel');

  await expect(dialog.getByRole('textbox', { name: 'H3 prompt' })).toContainText('[Shot 3]');
  await close(dialog);
  await page.locator(`[data-media-id="${original.id}"]`).getByRole('button', { name: `Actions for ${original.name}` }).click();
  await page.getByRole('menuitem', {name: 'Move to Trash',exact:true}).click();
  await expect.poll(async () => (await library(request, id)).reelTrash.length).toBe(1);
  await expect(page.getByText('Reel recovery', { exact: true })).not.toBeVisible();
  if (narrow && await page.locator('.workspace-right').isVisible()) await close(dialog);
  if (narrow) {
    await page.getByRole('button', { name: 'Application navigation', exact: true }).click();
    await page.getByRole('listbox').locator('a[href="/trash"]').click();
  } else await page.getByRole('link', { name: 'Trash', exact: true }).click();
  const removed = page.locator('.trash-card').filter({ hasText: original.name });
  await expect(removed).toBeVisible();
  await removed.getByRole('button', { name: 'Restore', exact: true }).click();
  await expect(removed).not.toBeVisible();
  await expect.poll(async () => (await library(request, id)).reels.length).toBe(2);
  await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`);
  await page.goto(`/projects/${id}/shots`);
  await toolsTab(page, 'References');
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  const manager = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Manage references', exact: true }) });
  await manager.locator(`[data-reel-id="${original.id}"] .add-reel`).click();
  if (narrow) await manager.getByRole('tab', { name: /Selected references/ }).click();
  await manager.getByLabel('Visuals', { exact: true }).selectOption('FullReel');
  await manager.getByLabel('Voice for Juniper', { exact: true }).selectOption('none');
  await manager.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(manager).not.toBeVisible();
  const setup = (await (await request.get(`/fixtures/${id}/production`)).json()).compositions[0];
  expect(setup.inputs.videos[0].media.id).toBe(original.media.id);
  expect(setup.inputs.videos[0].description).toBe('Reviewed appearance: use the face and hands.');
  expect(setup.inputs.videos[0].useSoundtrack).toBe(false);
});

for (const narrow of [false, true]) test(`reel request history opens the exact request from AI activity (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(90000);
  const { id, owner } = await fixture(page, request, narrow);
  const other = await (await request.post(`/fixtures/${id}/media-move-target`)).json();
  const tools = await recipe(page, owner, 'SideRearFace', 'Silent');
  await tools.getByRole('button', { name: 'Generate reel', exact: true }).click();
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(1);
  await close(tools);
  await expect(page.locator('.reel-requests')).toHaveCount(0);
  await expect(page.locator('.reel-library')).toHaveCount(0);
  const jobs = (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id);
  async function openRequest(job) {
    const activity = page.getByRole('dialog', { name: 'AI activity', exact: true });
    if (narrow) {
      await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
      // Publication and restored selection can open the tools after the API reports
      // completion. Finish dismissing that drawer before using the page header.
      await expect(async () => {
        if (await page.locator('.workspace-right').isVisible()) await close(tools);
        if (!await activity.isVisible()) await page.locator('.ai-activity-trigger').click({ timeout: 1000 });
        await expect(activity).toBeVisible();
      }).toPass({ timeout: 10000 });
    } else await page.locator('.ai-activity-trigger').click();
    await activity.getByRole('tab', { name: 'History', exact: true }).click();
    await activity.getByLabel('Project', { exact: true }).selectOption(id);
    await activity.locator(`[data-job-id="${job.id}"]`).getByRole('link', { name: 'Review', exact: true }).click();
    await expect(activity).not.toBeVisible();
    await expect(page).toHaveURL(new RegExp(`jobId=${job.id}`));
  }
  for (const kind of ['ReelVideo', 'ReelComposition']) {
    const job = jobs.find(j => j.kind === kind);
    expect(job).toBeTruthy();
    await openRequest(job);
    const review = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: kind === 'ReelVideo' ? 'Reference reel request' : 'Reel prompt pair', exact: true }) });
    await expect(review).toBeVisible();
    if (kind === 'ReelVideo') {
      await review.getByText('Request details', { exact: true }).click();
      await expect(review.locator('.submitted-input').first()).toContainText('[Shot 3]');
    } else {
      await review.getByText('Raw response', { exact: true }).click();
      await expect(review.locator('.submitted-input')).toContainText('[Shot 3]');
    }
    await review.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(review).not.toBeVisible();
    await expect(page).not.toHaveURL(/jobId=/);
    // The link's assetId is consumed once its asset is selected; selection stays with the owner.
    await expect(page.locator('.asset-list-row.selected')).toHaveAttribute('data-asset-id', owner.id);
    expect(new URL(page.url()).searchParams.get('view')).toBe('reels');
    // The same request can still be deliberately reopened without leaving this asset.
    await openRequest(job);
    await expect(review).toBeVisible();
    await review.getByRole('button', { name: 'Close', exact: true }).press('Escape');
    await expect(review).not.toBeVisible();
    await expect(page).not.toHaveURL(/jobId=/);
    if (narrow && await page.getByRole('button', { name: 'Close Asset tools', exact: true }).isVisible()) await close(tools);
    for (const asset of [other, owner]) {
      const choice = page.locator(`[data-asset-id="${asset.id}"] .asset-choice`);
      if (!await choice.isVisible()) await page.locator('[data-toggle-pane=left]').click();
      await choice.click();
      await expect(page.locator(`.asset-list-row[data-asset-id="${asset.id}"]`)).toHaveClass(/selected/);
    }
    await expect(review).not.toBeVisible();
    await page.reload();
    await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
    await expect(review).not.toBeVisible();
    if (narrow && await page.getByRole('button', { name: 'Close Asset tools', exact: true }).isVisible()) await close(tools);
  }
});

for (const narrow of [false, true]) test(`reel and voice details and moves between assets (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(120000);
  const { id, owner } = await fixture(page, request, narrow);
  const target = await (await request.post(`/fixtures/${id}/media-move-target`)).json();
  await request.post(`/fixtures/${id}/reference-workspace`);
  await page.reload();
  const tools = await recipe(page, owner, 'SideRearFace', 'Silent');
  await tools.getByRole('button', { name: 'Generate reel', exact: true }).click();
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(1);
  await close(tools);
  const before = await library(request, id), reel = before.reels[0], voice = before.voices[0];
  const card = item => page.locator(`[data-media-id="${item.id}"]`);
  async function action(item, label) {
    if (narrow && await page.getByRole('button', { name: 'Close Asset tools', exact: true }).isVisible()) await close(tools);
    await card(item).getByRole('button', { name: /^Actions for/ }).click();
    await page.getByRole('menuitem', { name: label, exact: true }).click();
    await expect(page.getByRole('menuitem', { name: label, exact: true })).not.toBeVisible();
    if (label === 'Edit details' || label === 'Rename') await expect(page.locator('.media-details-dialog')).toBeVisible();
  }
  await action(reel, 'Rename');
  const details = page.locator('.reel-details-dialog'); await expect(details).toBeVisible();
  const reelName = details.getByLabel('Name', { exact: true });
  await expect(reelName).toBeFocused();
  await expect.poll(() => reelName.evaluate(el => el.selectionEnd - el.selectionStart === el.value.length)).toBe(true);
  await details.locator('summary').getByText('Use guidance', { exact: true }).click();
  await details.getByLabel('Use guidance', { exact: true }).fill('Retained guidance before moving');
  await details.getByRole('button',{name:'Close',exact:true}).click(); await details.getByRole('button',{name:'Keep editing',exact:true}).click();
  await details.getByRole('button',{name:'Save details',exact:true}).click(); await expect(details).not.toBeVisible();
  await action(reel, 'Rename');
  await expect(details.getByLabel('Use guidance', { exact: true })).toHaveValue('Retained guidance before moving');
  await details.getByRole('button',{name:'Close',exact:true}).click();
  await action(reel, 'Move to asset…');
  const move = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: /^Move (reel|recording) to asset$/ }) });
  await move.getByLabel('Destination asset', { exact: true }).selectOption(target.id);
  await move.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await library(request, id)).reels[0].assetId).toBe(owner.id);
  await action(reel, 'Move to asset…');
  await move.getByLabel('Destination asset', { exact: true }).selectOption(target.id);
  await move.getByRole('button', { name: 'Move', exact: true }).click();
  await expect(move).not.toBeVisible();
  await page.locator('.reel-details-tools').getByRole('button',{name:'Edit details',exact:true}).click();
  await expect(details.getByLabel('Use guidance', { exact: true })).toHaveValue('Retained guidance before moving');
  await details.getByRole('button',{name:'Close',exact:true}).click();
  let saved = await library(request, id);
  expect(saved.reels[0].assetId).toBe(target.id);
  expect(saved.reels[0].media).toEqual(reel.media); expect(saved.reels[0].generation).toEqual(reel.generation);
  await page.locator('.reel-details-tools').getByRole('button', { name: 'Create similar', exact: true }).click();
  await expect(tools.getByLabel('Reel name', { exact: true })).toHaveValue('Riley reel');
  // The original draft stays with the first asset; Create similar adds one for the reel's new owner.
  await expect.poll(async () => (await library(request, id)).reelDrafts.some(d => d.name === 'Riley reel' && d.assetId === target.id)).toBe(true);
  // Regeneration retains captured inputs and follows the moved reel to its current asset.
  await action(reel, 'Regenerate…');
  await page.getByRole('button', { name: 'Queue 1 reel', exact: true }).click();
  await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(2);
  saved = await library(request, id);
  expect(saved.reels.find(r => r.id !== reel.id).assetId).toBe(target.id);
  await page.goto(`/projects/${id}/assets?assetId=${owner.id}`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await action(voice, 'Rename');
  const editor = page.locator('.voice-dialog');
  await expect(editor).toBeVisible();
  await expect(editor.getByLabel('Name', { exact: true })).toBeFocused();
  await expect.poll(() => editor.getByLabel('Name', { exact: true }).evaluate(el => el.selectionEnd - el.selectionStart === el.value.length)).toBe(true);
  await editor.getByLabel('Name', { exact: true }).fill('Moved recording');
  await editor.getByLabel('End (seconds)').fill('0');
  await expect(editor.getByRole('button',{name:'Save details',exact:true})).toBeDisabled();
  await editor.getByRole('button',{name:'Close',exact:true}).click(); await editor.getByRole('button',{name:'Keep editing',exact:true}).click();
  await editor.getByLabel('End (seconds)').fill('2');
  await editor.getByRole('button',{name:'Save details',exact:true}).click(); await expect(editor).not.toBeVisible();
  await action(voice, 'Move to asset…');
  await move.getByLabel('Destination asset').selectOption(target.id);
  await move.getByRole('button', { name: 'Move', exact: true }).click();
  await expect(move).not.toBeVisible();
  await page.locator('.voice-details-tools').getByRole('button',{name:'Edit details',exact:true}).click();
  await expect(editor.getByLabel('Name', { exact: true })).toHaveValue('Moved recording');
  const movedVoice = (await library(request, id)).voices[0];
  expect(movedVoice.id).toBe(voice.id); expect(movedVoice.assetId).toBe(target.id);
  expect(movedVoice.storageAssetId).toBe(owner.id); expect(movedVoice.excerptDuration).toBe(2);
  const audio = editor.locator('audio'); await expect.poll(() => audio.evaluate(v => v.readyState)).toBeGreaterThan(0);
  await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  // The route still points at the source. Move membership survives reload.
  expect((await library(request, id)).voices[0]).toEqual(movedVoice);
});

test('MP4 import and project-take copy keep separate identities without generated provenance', async ({ page, request }) => {
  test.setTimeout(90000);
  const { id, owner } = await fixture(page, request, true);
  await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`);
  const take = await (await request.post(`/fixtures/${id}/reference-video-take`)).json();
  await openImport(page);
  const dialog = page.getByRole('dialog').filter({ has: page.getByText('Import reference reel', { exact: true }) });
  await dialog.getByRole('combobox', { name: 'Existing take', exact: true }).selectOption(take.id);
  await dialog.getByRole('button', { name: 'Copy selected take' }).click();
  await expect(dialog.getByRole('button', { name: 'Save reel', exact: true })).toBeEnabled();
  await dialog.getByLabel('Use guidance', { exact: true }).fill('Author description for reuse.');
  await dialog.getByRole('button', { name: 'Save reel', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  const copied = (await library(request, id)).reels[0];
  expect(copied.generation).toBeNull(); expect(copied.sourceTakeId).toBe(take.id);
  const bytes = await (await request.get(`/media/projects/${id}/reference-videos/${copied.media.id}`)).body();
  await openImport(page);
  await dialog.getByLabel('Import MP4').setInputFiles({ name: 'manual-reel.mp4', mimeType: 'video/mp4', buffer: bytes });
  await expect(dialog.getByLabel('Reel name')).toHaveValue('manual-reel');
  await dialog.getByRole('button', { name: 'Save reel', exact: true }).click();
  await expect(dialog).not.toBeVisible();
  const reels = (await library(request, id)).reels;
  expect(reels).toHaveLength(2); expect(new Set(reels.map(r => r.media.id)).size).toBe(2);
  expect(reels.every(r => r.generation === null)).toBe(true);
  expect((await request.post(`/fixtures/${id}/cut-trash/${take.id}`)).ok()).toBe(true);
  const range = await request.get(`/media/projects/${id}/reference-videos/${copied.media.id}`, { headers: { Range: 'bytes=10-31' } });
  expect(range.status()).toBe(206); expect((await range.body()).length).toBe(22);
  await page.reload(); await expect(page.locator('.asset-media-card[data-media-kind=Reel]')).toHaveCount(2);
  expect((await library(request, id)).reels.every(r => r.assetId === owner.id)).toBe(true);
});

test('narrow AI pair survives closing, revisions compare both fields, manual edits and changed timing retain text', async ({ page, request }) => {
  test.setTimeout(120000);
  const { id, owner } = await fixture(page, request, true);
  const dialog = await recipe(page, owner);
  const assist = page.locator('.ai-assist-dialog');
  async function submit() {
    await dialog.getByRole('button', { name: 'Reel assistance', exact: true }).click();
    await assist.locator('.model-chip').click();
    await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
    await page.getByRole('button', { name: 'Set as project default', exact: true }).click();
    await assist.getByLabel('Instructions', { exact: true }).fill('Focus on hands');
    await assist.getByRole('button', { name: /Compose pair|Revise pair/ }).click();
    await expect(assist).not.toBeVisible();
  }
  await submit(); await close(dialog);
  await expect.poll(async () => (await library(request, id)).reelDrafts[0].prompt, { timeout: 25000 }).toContain('Focus on the hands.');
  await showTools(page);
  const before = (await library(request, id)).reelDrafts[0].prompt;
  await submit(); await close(dialog);
  await expect.poll(async () => (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id && j.kind === 'ReelComposition' && j.state === 'Completed').length, { timeout: 25000 }).toBe(2);
  await showTools(page);
  await dialog.getByRole('button', { name: /Review changes|Needs attention/ }).click();
  const diff = page.locator('.reel-review-dialog');
  await expect(diff.getByRole('heading', { name: 'Use guidance', exact: true })).toBeVisible();
  await diff.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(diff).not.toBeVisible();
  await dialog.getByLabel('Use guidance', { exact: true }).fill('Manual guidance · café 👋');
  await dialog.getByLabel('Requested seconds').fill('6'); await dialog.getByLabel('Requested seconds').blur();
  await expect(dialog.getByText(/Check prompts/)).toBeVisible();
  expect((await library(request, id)).reelDrafts[0].prompt).toBe(before);
  await page.screenshot({ path: 'artifacts/reference-reels-narrow.png' });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await close(dialog); await page.reload(); await showTools(page);
  await expect(dialog.getByLabel('Use guidance', { exact: true })).toHaveValue('Manual guidance · café 👋');
  await expect(dialog.getByLabel('Requested seconds')).toHaveValue('6');
});

test('manual writing prevents auto-application and invalid AI responses remain inspectable', async ({ page, request }) => {
  test.setTimeout(90000);
  const { id, owner } = await fixture(page, request);
  const dialog = await recipe(page, owner), assist = page.locator('.ai-assist-dialog');
  const jobs = async () => (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id);
  await dialog.getByRole('button', { name: 'Reel assistance' }).click();
  await assist.locator('.model-chip').click();
  await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await page.getByRole('button', { name: 'Set as project default', exact: true }).click();
  await assist.getByLabel('Instructions', { exact: true }).fill('SILENT_START Focus on hands');
  await assist.getByRole('button', { name: 'Compose pair', exact: true }).click();
  await expect(assist).not.toBeVisible();
  await dialog.getByLabel('Use guidance', { exact: true }).fill('My writing while AI works.');
  await close(dialog);
  await expect.poll(async () => (await jobs())[0]?.state, { timeout: 25000 }).toBe('Completed');
  expect((await library(request, id)).reelDrafts[0].prompt).toBe('');
  await showTools(page);
  await dialog.getByRole('button', { name: /Review changes|Needs attention/ }).click();
  const review = page.locator('.reel-review-dialog');
  await review.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(review.getByRole('alert')).toContainText('Since this prompt was written, the prompt or use guidance was edited');
  // The warning appears after Apply, so it sits in the footer above Apply anyway rather than below the comparison.
  await expect(review.locator('.mud-dialog-actions .assisted-apply-warning')).toBeInViewport();
  await expect(review.getByRole('button', { name: 'Apply anyway', exact: true })).toBeVisible();
  await review.getByRole('button', { name: 'Discard', exact: true }).click();
  await expect(review).not.toBeVisible();
  await dialog.getByRole('button', { name: 'Reel assistance' }).click();
  await assist.getByLabel('Instructions', { exact: true }).fill('INVALID_PAIR');
  await assist.getByRole('button', { name: 'Compose pair', exact: true }).click();
  await expect(assist).not.toBeVisible(); await close(dialog);
  await expect.poll(async () => (await jobs()).some(j => j.state === 'NeedsAttention'), { timeout: 20000 }).toBe(true);
  await showTools(page);
  await dialog.getByRole('button', { name: /Review changes|Needs attention/ }).click();
  await expect(review.getByRole('alert')).toContainText('JSON pair');
  await expect(review.getByRole('button', { name: 'Apply changes', exact: true })).toHaveCount(0);
  await review.getByText('Raw response', { exact: true }).click();
  await expect(review.locator('.submitted-input')).toContainText(/incomplete|unfinished/);
  expect((await library(request, id)).reelDrafts[0].useGuidance).toBe('My writing while AI works.');
});

for (const narrow of [false,true]) test(`valid JSON with an error is readable and recoverable (${narrow ? 'narrow' : 'desktop'})`, async ({page,request}) => {
  const {id,owner}=await fixture(page,request,narrow);
  const tools=await recipe(page,owner), assist=page.locator('.ai-assist-dialog');
  await tools.getByRole('button',{name:'Reel assistance',exact:true}).click();
  await assist.locator('.model-chip').click();
  await page.getByRole('combobox',{name:'Text model',exact:true}).selectOption({label:'OpenRouter · Alternate mock model'});
  await page.getByRole('button',{name:'Set as project default',exact:true}).click();
  await assist.getByLabel('Instructions',{exact:true}).fill(`${narrow ? 'EMPTY_REEL_MUSIC' : 'WRONG_REEL_DURATION'} Focus on hands`);
  await assist.getByRole('button',{name:'Compose pair',exact:true}).click();
  await expect(assist).not.toBeVisible();
  await expect(tools.getByRole('button', {name:'Needs attention',exact:true})).toBeVisible({timeout:25000});
  await tools.getByRole('textbox',{name:'H3 prompt',exact:true}).fill('My later writing.');
  await tools.getByLabel('Use guidance',{exact:true}).fill('Keep my later notes.');
  await tools.getByRole('button',{name:'Needs attention',exact:true}).click();
  const review=page.locator('.reel-review-dialog');
  await expect(review.getByLabel('Response generation prompt')).toHaveValue(narrow ? /non_diegetic_music:\s*$/ : /5 seconds/);
  await expect(review.getByLabel('Response use guidance')).toHaveValue(/hands/);
  await expect(review.getByRole('button',{name:'Copy response prompt',exact:true})).toBeVisible();
  await expect(review.getByRole('button',{name:'Apply changes',exact:true})).toHaveCount(0);
  const before = (await library(request,id)).reelDrafts.at(-1);
  expect(before.prompt).toBe('My later writing.');
  const failedJob = (await (await request.get('/fixtures/ai-jobs')).json()).find(j=>j.target.projectId===id && j.kind==='ReelComposition');
  await page.goto(`/projects/${id}/assets?assetId=${owner.id}&view=reels&jobId=${failedJob.id}`);
  await expect(review.getByLabel('Response generation prompt')).toBeVisible();
  await expect(review.getByRole('button',{name:'Edit response',exact:true})).toBeInViewport();
  await expect.poll(async () => (await review.boundingBox()).width).toBeGreaterThan(narrow ? 350 : 1050);
  await page.screenshot({path:`artifacts/reel-response-error-${narrow ? 'narrow' : 'desktop'}.png`});
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const response = await review.getByLabel('Response generation prompt').inputValue();
  await review.getByRole('button',{name:'Edit response',exact:true}).press('Enter');
  await expect(review).not.toBeVisible();
  await expect(page).not.toHaveURL(/jobId=/);
  await expect(tools.getByRole('textbox',{name:'H3 prompt',exact:true})).toBeVisible();
  const drafts = (await library(request,id)).reelDrafts;
  expect(drafts.find(d=>d.id===before.id)).toEqual(before);
  expect(drafts.at(-1).id).not.toBe(before.id);
  expect(drafts.at(-1).prompt).toBe(response);
  expect(drafts.at(-1).pendingJobId).toBeNull();
  const corrected = narrow ? response + 'No non-diegetic music.' : response.replaceAll('5 seconds','5.167 seconds');
  await tools.getByRole('textbox',{name:'H3 prompt',exact:true}).fill(corrected);
  await tools.getByLabel('Use guidance',{exact:true}).focus();
  await expect.poll(async () => (await library(request,id)).reelDrafts.at(-1).prompt).toBe(corrected);
  await page.reload(); await showTools(page);
  await expect(review).not.toBeVisible();
  await expect(tools.getByRole('textbox',{name:'H3 prompt',exact:true})).toContainText('No non-diegetic music.');
  const jobs = (await (await request.get('/fixtures/ai-jobs')).json()).filter(j=>j.target.projectId===id);
  expect(jobs.filter(j=>j.kind==='ReelComposition')).toHaveLength(1);
});

test('pre-submit failures explain their stage and link to the prompt from Setup', async ({page,request}) => {
  const {id,owner}=await fixture(page,request,true);
  const tools=await recipe(page,owner,'ContinuousTurn','Silent');
  await tools.getByLabel('Requested seconds').fill('6');
  await tools.getByLabel('Requested seconds').blur();

  await tools.getByRole('button',{name:'Generate reel',exact:true}).click();
  await expect(tools.getByRole('alert')).toContainText('Not submitted to ComfyUI');
  await expect(tools.getByRole('alert')).toContainText('generation prompt text');
  await tools.getByRole('button',{name:'Review prompt',exact:true}).click();
  await expect(tools.getByRole('textbox',{name:'H3 prompt',exact:true})).toBeVisible();
  await expect(tools.getByRole('textbox',{name:'H3 prompt',exact:true})).toBeVisible();
  await tools.getByLabel('Requested seconds').fill('5'); await tools.getByLabel('Requested seconds').blur();

  await tools.getByLabel('Reel name',{exact:true}).fill('MEMORY_FAILURE');
  await tools.getByRole('button',{name:'Generate reel',exact:true}).click();
  await expect(tools.getByRole('alert')).toContainText('Lumibelle ran out of system memory while preparing');
  await expect(tools.getByRole('button',{name:'Generate reel',exact:true})).toBeEnabled();
  expect((await (await request.get('/fixtures/ai-jobs')).json()).filter(j=>j.target.projectId===id && j.kind==='ReelVideo')).toHaveLength(0);
  expect((await library(request,id)).reelDrafts[0].prompt).toContain('5.167 seconds');
});

test('renaming preserves another tabs guidance correction', async ({page,request,browser}) => {
  test.setTimeout(90000);
  const {id,owner}=await fixture(page,request);
  await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`);
  const take=await (await request.post(`/fixtures/${id}/reference-video-take`)).json();
  await openImport(page);
  const importer=page.getByRole('dialog').filter({has:page.getByText('Import reference reel',{exact:true})});
  await importer.getByRole('combobox',{name:'Existing take',exact:true}).selectOption(take.id);
  await importer.getByRole('button',{name:'Copy selected take'}).click();
  await importer.getByLabel('Use guidance',{exact:true}).fill('Original guidance');
  await importer.getByRole('button',{name:'Save reel',exact:true}).click();
  await expect(importer).not.toBeVisible();
  const other=await browser.newPage();
  await other.goto(`${new URL(page.url()).origin}/projects/${id}/assets?assetId=${owner.id}&view=reels`);
  await expect(other.locator('.asset-media-card[data-media-kind=Reel]')).toHaveCount(1);
  await page.locator('.asset-media-card[data-media-kind=Reel] .media-preview').click();
  await other.locator('.asset-media-card[data-media-kind=Reel] .media-preview').click();
  await other.locator('.reel-details-dialog summary').getByText('Use guidance', { exact: true }).click();
  await other.locator('.reel-details-dialog').getByRole('textbox',{name:'Use guidance',exact:true}).fill('Corrected after watching');
  await other.getByRole('button',{name:'Save details'}).click();
  await expect.poll(async()=>(await library(request,id)).reels[0].useGuidance).toBe('Corrected after watching');
  await page.locator('.reel-details-dialog').getByRole('textbox',{name:'Name',exact:true}).fill('Renamed reel');
  await page.getByRole('button',{name:'Save details'}).click();
  await expect.poll(async()=>(await library(request,id)).reels[0].name).toBe('Renamed reel');
  const saved=(await library(request,id)).reels[0];
  await other.close();
  expect(saved.useGuidance).toBe('Corrected after watching');
});

for (const narrow of [false, true]) test(`framing belongs in Assist and composes from cropped pictures (${narrow ? 'narrow' : 'desktop'})`, async ({page,request}) => {
  const {id,owner}=await fixture(page,request,narrow);
  const tools=await recipe(page,owner,'SideRearFace','NewVoice','Use the blue gloves.',false,true);

  const picture=tools.getByRole('button',{name:'Edit Picture 1',exact:true});
  await expect(picture.locator('img')).toBeVisible();
  await expect(picture).toContainText('Auto · Cropped');
  await expect(picture).toContainText('Custom guidance · Use the blue gloves.');
  await expect(tools.getByRole('textbox',{name:/Use of Picture/})).toHaveCount(0);
  await picture.focus(); await page.keyboard.press('Enter');
  const pictures=page.locator('.reel-pictures-dialog');
  await expect(pictures.getByRole('textbox',{name:'Composition preservation override',exact:true})).toHaveValue('Use the blue gloves.');
  await pictures.getByRole('textbox',{name:'Composition preservation override',exact:true}).fill('Unapplied change');
  await pictures.getByRole('button',{name:'Cancel',exact:true}).click();
  await expect(pictures).not.toBeVisible();
  await expect(picture).toBeFocused();
  await expect(picture).toContainText('Use the blue gloves.');
  const voice='Warm, lightly raspy — posé.\nA relaxed pace.';
  await tools.getByLabel('Voice description (optional)',{exact:true}).fill(voice);
  await tools.getByLabel('Voice mode').selectOption('Silent');
  await expect(tools.getByLabel('Voice description (optional)',{exact:true})).not.toBeVisible();
  await tools.getByLabel('Voice mode').selectOption('NewVoice');
  await expect(tools.getByLabel('Voice description (optional)',{exact:true})).toHaveValue(voice);
  await page.screenshot({path:`artifacts/reel-picture-voice-${narrow ? 'narrow' : 'desktop'}.png`});
  await tools.getByLabel('Voice description (optional)',{exact:true}).scrollIntoViewIfNeeded();
  await expect(tools.getByLabel('Voice description (optional)',{exact:true})).toBeInViewport();
  await expect(tools.getByRole('button',{name:'Generate reel',exact:true})).toBeInViewport();
  await page.screenshot({path:`artifacts/reel-voice-description-${narrow ? 'narrow' : 'desktop'}.png`});

  const editor=tools.getByRole('textbox',{name:'H3 prompt',exact:true});
  await expect(tools.getByLabel('Framing preset')).toHaveCount(0);
  await expect(tools.getByRole('button',{name:/Build preset prompts|Review preset prompts/})).toHaveCount(0);
  await expect(tools.getByRole('button',{name:'Generate reel',exact:true})).toBeDisabled();
  expect((await library(request,id)).reelDrafts[0].prompt).toBe('');
  const captured=async()=>(await (await request.get('/fixtures/reel-compositions')).json()).filter(c=>c.context.request?.projectId===id);
  expect(await captured()).toHaveLength(0);
  await tools.getByRole('button',{name:'Reel assistance',exact:true}).click();
  const assist=page.locator('.ai-assist-dialog');
  await expect(assist.getByLabel('Framing preset')).toHaveValue('SideRearFace');
  await expect(assist.locator('.reel-framing-plan')).toContainText('shoulder height');
  await assist.getByLabel('Instructions',{exact:true}).fill('Focus on the hands and gloves.');
  await selectVisionModel(page,assist);
  await expect(assist.getByRole('button',{name:'Compose pair',exact:true})).toBeInViewport();
  await page.screenshot({path:`artifacts/reel-framing-assist-${narrow ? 'narrow' : 'desktop'}.png`});
  await assist.getByRole('button',{name:'Compose pair',exact:true}).click();
  await expect(assist).not.toBeVisible();
  await expect(editor).toContainText('[Shot 3]');
  await expect(tools.getByRole('button',{name:'Generate reel',exact:true})).toBeEnabled();
  const [submitted]=await captured();
  expect(submitted.model).toBe('mock/alternate');
  expect(submitted.context.request.draft.framing).toBe('SideRearFace');
  expect(submitted.context.presetViews).toContain('next 40%');
  expect(submitted.context.request.draft.instructions).toBe('Focus on the hands and gloves.');
  expect(submitted.context.request.draft.voiceDescription).toBe(voice);
  await expect(editor).toContainText('Warm, lightly raspy');
  expect(submitted.context.request.imageGuidance[0].effective).toContain('blue gloves');
  expect(submitted.images).toHaveLength(1);
  // The crop, not the full picture, is sent: a 2× zoom at the original aspect halves both sides.
  expect(submitted.images[0].width).toBeLessThan(owner.images[0].width);
  expect(submitted.images[0].height).toBeLessThan(owner.images[0].height);
  expect(submitted.images[0].width / submitted.images[0].height).toBeCloseTo(owner.images[0].width / owner.images[0].height, 1);
  const before=(await library(request,id)).reelDrafts[0];
  const rendered=await editor.textContent();

  await tools.getByLabel('Voice description (optional)',{exact:true}).fill('Bright and brisk.');

  await expect(editor).toHaveText(rendered);
  await expect(tools.getByText(/Check prompts/)).toBeVisible();
  await tools.getByRole('button',{name:'Reel assistance',exact:true}).click();
  await assist.getByLabel('Framing preset').selectOption('Custom');
  await expect(assist.locator('.reel-framing-plan')).toContainText('No prescribed framing');
  await assist.getByRole('button',{name:'Close',exact:true}).click();
  await expect(editor).toHaveText(rendered);
  expect((await library(request,id)).reelDrafts[0].prompt).toBe(before.prompt);
  await expect(tools.getByLabel('Use guidance',{exact:true})).toHaveValue(before.useGuidance);
  await expect(tools.getByText(/Check prompts/)).toBeVisible();
  await expect(page.locator('.reel-review-dialog')).not.toBeVisible();
  expect(await captured()).toHaveLength(1);
  await tools.getByRole('button',{name:'Reel assistance',exact:true}).click();
  await expect(assist.getByLabel('Framing preset')).toHaveValue('Custom');
  await assist.getByRole('button',{name:'Revise pair',exact:true}).click();
  await expect(assist).not.toBeVisible();
  await tools.getByRole('button',{name:'Review changes',exact:true}).click();
  const review=page.locator('.reel-review-dialog');
  await expect(review.getByRole('button',{name:'Apply changes',exact:true})).toBeEnabled();
  expect((await library(request,id)).reelDrafts[0].prompt).toBe(before.prompt);
  await review.getByRole('button',{name:'Discard',exact:true}).click();
  await expect(review).not.toBeVisible();
  await page.reload(); await showTools(page);

  await expect(tools.getByLabel('Voice description (optional)',{exact:true})).toHaveValue('Bright and brisk.');
  await expect(picture).toContainText('Use the blue gloves.');
  expect((await library(request,id)).reelDrafts[0].prompt).toBe(before.prompt);
});

test('prompt editor stays mounted with Undo across tool tabs and asset navigation flushes current typing', async ({page,request}) => {
  const {id,owner}=await fixture(page,request);
  const tools=await recipe(page,owner);
  const editor=tools.getByRole('textbox',{name:'H3 prompt',exact:true});
  await editor.fill('A manually authored draft.');
  await expect.poll(async()=>(await library(request,id)).reelDrafts[0].prompt).toBe('A manually authored draft.');
  await editor.evaluate(el => { window.reelEditorIdentity=el; });

  await tools.getByLabel('Reel name',{exact:true}).fill('Still my draft');
  await page.getByLabel('Create media type').selectOption('Image');
  await page.getByLabel('Create media type').selectOption('Reel');
  expect(await editor.evaluate(el => el===window.reelEditorIdentity)).toBe(true);
  await tools.getByRole('button',{name:'Undo prompt edit',exact:true}).click();
  await expect(editor).toBeEmpty();
  await tools.getByRole('button',{name:'Redo prompt edit',exact:true}).click();
  await expect(editor).toContainText('A manually authored draft.');
  await editor.fill('Latest writing — café 👋');
  const another=(await library(request,id)).assets.find(a=>a.id!==owner.id);
  await page.locator('.asset-choice').filter({hasText:another.name}).click();
  expect((await library(request,id)).reelDrafts[0].prompt).toBe('Latest writing — café 👋');
  await page.locator('.asset-choice').filter({hasText:owner.name}).click();
  await showTools(page);
  await expect(editor).toContainText('Latest writing — café 👋');
});

for (const narrow of [false, true]) test(`existing look drafts retain their destination until explicitly cleared (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  const {id}=await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/planning-looks`);
  const saved=await (await request.post(`/fixtures/${id}/reel-look-draft`)).json();
  await page.setViewportSize({width:narrow?390:1173,height:narrow?844:1000});
  await page.goto(`/projects/${id}/assets?assetId=${saved.assetId}&view=reels`); await showTools(page);
  const tools=page.locator('.reel-tools'), editor=tools.getByRole('textbox',{name:'H3 prompt',exact:true});
  await expect(editor).toContainText(saved.prompt);
  await expect(tools.getByText(/Saved destination/)).toBeVisible();
  await tools.getByRole('button',{name:'Use unassigned',exact:true}).click();
  await expect(tools.getByText(/Saved destination/)).toHaveCount(0);
  await expect.poll(async()=>(await library(request,id)).reelDrafts.length).toBe(2);
  await editor.fill('General appearance prompt.');
  await page.getByLabel('Create media type').selectOption('Image');
  await page.getByLabel('Create media type').selectOption('Reel');
  await expect(editor).toContainText('General appearance prompt.');
  const drafts=(await library(request,id)).reelDrafts;
  expect(drafts.find(d=>d.id===saved.id).prompt).toBe(saved.prompt);
  expect(drafts.find(d=>d.id!==saved.id).lookId).toBeNull();
  await tools.getByRole('button',{name:'Undo prompt edit',exact:true}).click();
  await expect(editor).toContainText(saved.prompt);
});

test('slow preparation stays dismissible and cancellable in the narrow tools drawer', async ({page,request}) => {
  const {id,owner}=await fixture(page,request,true);
  const tools=await recipe(page,owner,'ContinuousTurn','Silent');

  await tools.getByLabel('Reel name',{exact:true}).fill('SLOW_PREPARATION reference');
  await tools.getByRole('button',{name:'Generate reel',exact:true}).click();
  await expect(tools.getByRole('button',{name:'Cancel preparation',exact:true})).toBeVisible();
  await close(tools);
  await page.getByRole('button',{name:'Show Asset tools',exact:true}).click();
  await tools.getByRole('button',{name:'Cancel preparation',exact:true}).click();
  await expect(tools.getByRole('button',{name:'Generate reel',exact:true})).toBeEnabled();
  await expect(tools.locator('.workspace-pane-footer')).toContainText('Preparation cancelled. Your recipe is saved.');
  expect((await library(request,id)).reelDrafts[0].prompt).toContain('subject_definitions:');
  const jobs=(await (await request.get('/fixtures/ai-jobs')).json()).filter(j=>j.target.projectId===id && j.kind==='ReelVideo');
  expect(jobs).toHaveLength(0);
  await tools.getByLabel('Reel name',{exact:true}).fill('Responsive after cancellation');
  await close(tools); await page.reload();
  await showTools(page);
  await expect(tools.getByLabel('Reel name',{exact:true})).toHaveValue('Responsive after cancellation');
});

for (const narrow of [false, true]) {
  test(`reel H3 LoRAs persist, validate and stay captured in variations and regeneration (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
    test.setTimeout(150000);
    const { id, owner } = await fixture(page, request, narrow);
    await page.goto(`/settings/ai?tab=loras&projectId=${id}&returnTo=assets`);
    await page.getByRole('button', { name: 'Refresh installed LoRAs', exact: true }).click();
    const file = narrow ? 'h3/styles/film.safetensors' : 'h3/character.safetensors';
    const name = `Reel identity ${narrow ? 'narrow' : 'desktop'}`;
    await page.getByRole('button', { name: 'Add registration', exact: true }).click();
    await page.locator('#lora-file').selectOption(file);
    await page.locator('#lora-name').fill(name);
    await page.locator('#lora-workflow').selectOption('MiniMaxH3Ref2VA');
    await page.locator('#lora-default-strength').fill('0.75');
    await page.locator('#lora-trigger').fill('reference character');
    await page.getByRole('button', { name: 'Save LoRA library', exact: true }).click();
    await expect(page.locator('.lora-registration')).toHaveCount(0);
    await page.goto(`/projects/${id}/assets?assetId=${owner.id}&view=reels`);
    const tools = await recipe(page, owner, 'ContinuousTurn', 'Silent');
    const prompt = await tools.getByRole('textbox', { name: 'H3 prompt' }).innerText();

    await tools.locator('.reel-loras > summary').click();
    const picker = tools.locator('#reel-lora-add');
    await expect(page.locator('#reel-lora-add')).toHaveCount(1);
    await picker.fill(name);
    await expect(page.getByRole('option').filter({ hasText: name })).toBeVisible();
    await picker.press('ArrowDown'); await picker.press('Enter');
    await expect(tools.getByRole('checkbox', { name, exact: true })).toBeChecked();
    const strength = tools.getByRole('spinbutton', { name: `Strength for ${name}` });
    await strength.fill('101'); await strength.blur();
    await expect(tools.getByRole('button', { name: 'Generate reel', exact: true })).toBeDisabled();
    await strength.fill('0.55'); await strength.blur();
    await expect.poll(async () => (await library(request, id)).reelDrafts[0].loras?.[0].strength).toBe(.55);

    await expect(tools.getByRole('textbox', { name: 'H3 prompt' })).toHaveText(prompt, { useInnerText: true });
    await page.reload(); await showTools(page);

    await tools.locator('.reel-loras > summary').click();
    await expect(strength).toHaveValue('0.55');
    // Missing active weights fail before enqueue. Restoring them needs no recipe rewrite.
    await request.post(`/fixtures/lora-file?file=${encodeURIComponent(file)}&missing=true`);
    try {
      await tools.getByRole('button', { name: 'Generate reel', exact: true }).click();
      await expect(tools.locator('[role=alert]')).toContainText('Not submitted to ComfyUI');
      await expect(tools.locator('[role=alert]')).toContainText('LoRA file is missing');
      expect((await library(request, id)).reels).toHaveLength(0);
    } finally { await request.post(`/fixtures/lora-file?file=${encodeURIComponent(file)}&missing=false`); }
    await tools.getByRole('button', { name: 'Generate reel', exact: true }).click();
    await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(1);
    const original = (await library(request, id)).reels[0];
    expect(original.generation.snapshot.appliedLoras[0].strength).toBe(.55);
    expect(original.generation.snapshot.appliedLoras[0].reference.fileName).toBe(file);
    // Completed generation starts a fresh draft; Regeneration still uses the saved LoRAs.
    await expect.poll(async () => (await library(request, id)).reelDrafts.at(-1).loras ?? []).toEqual([]);
    await close(tools);
    await page.locator(`[data-media-id="${original.id}"] .media-select`).click();
    await page.locator('.reel-details-tools').getByRole('button', { name: 'Regenerate…', exact: true }).click();
    await page.getByRole('button', { name: 'Queue 1 reel', exact: true }).click();
    await expect.poll(async () => (await library(request, id)).reels.length, { timeout: 45000 }).toBe(2);
    expect((await library(request, id)).reels.every(r => JSON.stringify(r.generation.snapshot.appliedLoras) === JSON.stringify(original.generation.snapshot.appliedLoras))).toBe(true);
    const card = page.locator(`[data-media-id="${original.id}"]`);
    await page.locator('.reel-details-tools').getByRole('button',{name:'Edit details',exact:true}).click();
    const detail = page.locator('.reel-details-dialog');
    await detail.getByText('Applied LoRAs · 1', { exact: true }).click();
    await expect(detail.locator('.video-lora-details')).toContainText(name);
    await detail.getByRole('button', { name: 'Close', exact: true }).click();
    await page.locator('.reel-details-tools').getByRole('button', { name: 'Create similar', exact: true }).click();

    if (await tools.locator('.reel-loras').getAttribute('open') === null) await tools.locator('.reel-loras > summary').click();
    await expect(tools.getByRole('checkbox', { name, exact: true })).toBeChecked();
    await expect(strength).toHaveValue('0.55');
    await tools.getByRole('button', { name: 'Refresh LoRAs', exact: true }).click();
    await expect(tools.locator('.lora-row')).not.toContainText('Refresh LoRAs to check availability.');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: `artifacts/reel-loras-${narrow ? 'narrow' : 'desktop'}.png` });
  });
}

async function openImport(page) {
  const close = page.locator('.workspace-right .workspace-drawer-heading [data-close-pane]');
  if (await close.isVisible()) await close.click();
  const menu = page.locator('.asset-import-menu'); if (!await menu.evaluate(d => d.open)) await menu.locator('summary').click();
  await menu.getByRole('button', {name:'Import reel',exact:true}).click();
}
