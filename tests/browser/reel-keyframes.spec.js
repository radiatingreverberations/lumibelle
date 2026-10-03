import { generateTakes, openShotSetup, toolsTab } from './workspace-tools.js';
import { test, expect } from './fixtures.js';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
const library = async (request, id) => (await (await request.get(`/fixtures/${id}`)).json()).assets;
const setup = async (request, id) => (await (await request.get(`/fixtures/${id}/production`)).json()).compositions[0];
async function fixture(page, request, narrow) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`); await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`);
  await page.goto(`/projects/${id}/shots`); await expect(page.locator('.shot-setup-summary select.generation-preset-select')).toBeVisible();
  const take = await (await request.post(`/fixtures/${id}/reference-video-take`)).json();
  const saved = await (await request.post(`/fixtures/${id}/reference-reels?takeId=${take.id}`)).json();
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  return { id, reel: saved.reels[0], owner: saved.assets[0] };
}
async function manager(page, reel) {
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await page.getByRole('button', { name: `Actions for ${reel.name}`, exact: true }).click();
  await page.getByRole('menuitem', { name: 'Manage keyframes…', exact: true }).click();
  return page.locator('.keyframe-editor');
}
async function manual(page, editor, frame) {
  await editor.getByLabel('Frame timeline', { exact: true }).fill(String(frame));
  await expect.poll(() => editor.locator('video').evaluate(v => v.currentTime)).toBeGreaterThanOrEqual(frame / 24);
  expect(await editor.locator('video').evaluate(v => v.currentTime)).toBeLessThan((frame + 1) / 24);
  await editor.getByRole('button', { name: 'Add current frame', exact: true }).click();
}

for (const lossless of [true, false]) test(`manual frame selection follows exact indices (${lossless ? 'lossless 24 fps' : 'offset VFR import'})`, async ({ page, request }, testInfo) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  const source = testInfo.outputPath('timeline.mp4');
  execFileSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i',
    `testsrc2=size=96x64:rate=${lossless ? 24 : 30}:duration=3`,
    ...(lossless ? [] : ['-vf', "select='not(eq(mod(n,5),0))'", '-fps_mode', 'vfr']),
    '-c:v', 'libx264', '-threads', '1', source]);
  const pts = JSON.parse(execFileSync('ffprobe', ['-v', 'error', '-select_streams', 'v:0', '-show_frames', '-show_entries', 'frame=best_effort_timestamp_time', '-of', 'json', source], { encoding: 'utf8' })).frames.map(f => Number(f.best_effort_timestamp_time));
  const response = await request.post(`/fixtures/${id}/timeline-reel?lossless=${lossless}`, { data: readFileSync(source), headers: { 'Content-Type': 'video/mp4' } });
  expect(response.ok()).toBe(true);
  const { reel, catalog } = await response.json();
  if (!lossless) expect(catalog.playbackOrigin).toBeGreaterThan(.03);
  await page.goto(`/projects/${id}/assets`);
  const editor = await manager(page, reel);
  await expect(editor.getByRole('button', { name: 'Auto-pick', exact: true })).toBeEnabled();
  for (let remaining = await editor.locator('.keyframe-list > li').count(); remaining > 0; remaining--) {
    await editor.getByRole('button', { name: 'Remove', exact: true }).first().click();
    await expect(editor.locator('.keyframe-list > li')).toHaveCount(remaining - 1);
  }
  const player = editor.locator('video');
  await player.evaluate(v => { window.timelinePresented = null; const frame = (_, m) => { window.timelinePresented = m.mediaTime; v.requestVideoFrameCallback(frame); }; v.requestVideoFrameCallback(frame); });
  async function selected(index) {
    await expect(editor.getByLabel('Frame timeline', { exact: true })).toHaveValue(String(index));
    await expect.poll(() => page.evaluate(() => window.timelinePresented)).toBeCloseTo(pts[index], 5);
    await expect(editor.locator('.exact-frame')).toHaveAttribute('src', new RegExp(`/frames/${index}\\?`));
  }
  for (const index of [1, 2, 3, 4, 5]) { await editor.getByRole('button', { name: 'Next frame', exact: true }).click(); await selected(index); }
  await editor.getByRole('button', { name: 'Previous frame', exact: true }).click(); await selected(4);
  await editor.getByRole('button', { name: 'Add current frame', exact: true }).click();
  await expect(editor.locator('.keyframe-pick img')).toHaveAttribute('src', /\/frames\/4\?/);
  await editor.getByLabel('Frame timeline', { exact: true }).fill('10'); await selected(10);
  await editor.getByRole('button', { name: 'Add current frame', exact: true }).click();
  await expect(editor.locator('.keyframe-pick img').last()).toHaveAttribute('src', /\/frames\/10\?/);
  // Native scrubbing must release the explicit selection and use the original browser clock.
  await player.evaluate((v, time) => { v.currentTime = time; }, (pts[20] + pts[21]) / 2);
  await expect.poll(() => page.evaluate(() => window.timelinePresented)).toBeCloseTo(pts[20], 5);
  await editor.getByRole('button', { name: 'Add current frame', exact: true }).click();
  await expect(editor.locator('.keyframe-pick img').last()).toHaveAttribute('src', /\/frames\/20\?/);
  await editor.getByLabel('Frame timeline', { exact: true }).fill('30'); await selected(30);
  await player.evaluate(async v => { v.muted = true; await v.play(); });
  await expect.poll(() => page.evaluate(() => window.timelinePresented)).toBeGreaterThan(pts[32]);
  await player.evaluate(v => v.pause());
  await editor.getByRole('button', { name: 'Add current frame', exact: true }).click();
  await expect(editor.locator('.keyframe-pick img')).toHaveCount(4);
  // Pausing can deliver one final presentation callback; compare after it settles.
  const playedTime = await page.evaluate(() => window.timelinePresented);
  const playedIndex = pts.findLastIndex(t => t <= playedTime + .00001);
  await expect(editor.locator('.keyframe-pick img').last()).toHaveAttribute('src', new RegExp(`/frames/${playedIndex}\\?`));
  await editor.getByRole('button', { name: 'Save keyframes', exact: true }).click();
  await expect(editor).not.toBeVisible();
  expect((await library(request, id)).reels.find(r => r.id === reel.id).keyframes.frames.map(f => f.frame.index)).toEqual([4, 10, 20, playedIndex]);
});
for (const narrow of [false, true]) test(`keyframe defaults and setup overrides (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(100000);
  const { id, reel, owner } = await fixture(page, request, narrow);
  await page.goto(`/projects/${id}/assets?assetId=${owner.id}`);
  let editor = await manager(page, reel);
  await expect(editor.locator('.keyframe-list > li')).toHaveCount(1);
  await expect(editor.getByText(/Found 1 distinct usable views/)).toBeVisible();
  const suggested = Number((await editor.locator('.keyframe-pick img').getAttribute('src')).match(/frames\/(\d+)/)[1]);
  await expect.poll(() => editor.locator('.exact-frame').evaluate(img => img.naturalWidth)).toBeGreaterThan(0);
  await manual(page, editor, 48);
  await expect(editor.locator('.keyframe-list > li')).toHaveCount(2);
  await editor.getByLabel('Auto-pick up to', { exact: true }).fill('9');
  await expect(editor.locator('.keyframe-list > li')).toHaveCount(2); // N alone never replaces picks.
  await editor.getByRole('button', { name: 'Auto-pick', exact: true }).click();
  await expect(editor.locator('.keyframe-list > li')).toHaveCount(1);
  await editor.getByRole('button', { name: 'Undo picks', exact: true }).click();
  await expect(editor.locator('.keyframe-list > li')).toHaveCount(2);
  const picked = editor.locator('.keyframe-list > li').last();
  await picked.getByLabel('Frame notes (optional)').fill('Preserve the doorway');
  await picked.getByText('Crop keyframe', { exact: true }).click();
  await picked.getByLabel('Width', { exact: true }).fill('0.5');
  await editor.getByRole('button', { name: 'Move keyframe 2 up', exact: true }).click();
  await expect(editor.getByRole('button', { name: 'Save keyframes', exact: true })).toBeInViewport();
  await page.screenshot({ path: `artifacts/reel-keyframes-${narrow ? 'narrow' : 'desktop'}.png` });
  await editor.getByRole('button', { name: 'Save keyframes', exact: true }).click();
  await expect(editor).not.toBeVisible();
  const defaults = (await library(request, id)).reels.find(r => r.id === reel.id).keyframes;
  expect(defaults.frames.map(f => f.frame.index)).toEqual([48, suggested]);
  expect(defaults.frames[0].crop.width).toBe(.5);
  editor = await manager(page, reel);
  await editor.getByRole('button', { name: 'Remove', exact: true }).first().click();
  await editor.getByRole('button', { name: 'Cancel', exact: true }).click();
  expect((await library(request, id)).reels.find(r => r.id === reel.id).keyframes).toEqual(defaults);
  await expect(page.getByRole('button', { name: `Actions for ${reel.name}`, exact: true })).toBeFocused();

  await page.goto(`/projects/${id}/shots`); await toolsTab(page, 'References');
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  const refs = page.locator('.manual-reference-dialog');
  await refs.locator(`[data-reel-id="${reel.id}"] .add-reel`).click();
  if (narrow) await refs.getByRole('tab', { name: /Selected references/ }).click();
  await refs.getByLabel('Voice for Juniper', { exact: true }).selectOption({ label: reel.name });
  await expect(refs.getByLabel('Visuals', { exact: true })).toHaveValue('Keyframes');
  await expect(refs.locator('.selected-keyframes > div')).toHaveCount(2);
  await refs.getByRole('button', { name: 'Manage keyframes…', exact: true }).click();
  editor = page.locator('.keyframe-editor');
  await manual(page, editor, 72);
  await editor.getByRole('button', { name: 'Apply keyframes', exact: true }).click();
  await expect(editor).not.toBeVisible();
  await expect(refs.getByRole('button', { name: 'Manage keyframes…', exact: true })).toBeFocused();
  await refs.getByLabel('Audio start for Juniper', { exact: true }).fill('.5');
  await refs.getByLabel('Audio duration for Juniper', { exact: true }).fill('2');
  await refs.getByRole('button', { name: 'Play audio excerpt', exact: true }).click();
  await expect.poll(() => refs.locator('audio').evaluate(v => v.currentTime)).toBeGreaterThanOrEqual(.5);
  await request.post(`/fixtures/${id}/reference-save-failure?fail=true`);
  await refs.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(refs.getByRole('alert')).toContainText('Fixture storage unavailable');
  await request.post(`/fixtures/${id}/reference-save-failure?fail=false`);
  await refs.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(refs).not.toBeVisible();
  const attached = (await setup(request, id)).inputs.videos[0];
  expect(attached.keyframes.frames.map(f => f.frame.index)).toEqual([48, suggested, 72]);
  expect(attached.audioExcerpt).toEqual({ start: .5, duration: 2 });
  expect((await library(request, id)).reels.find(r => r.id === reel.id).keyframes).toEqual(defaults);
  await page.reload(); await toolsTab(page, 'References');
  const reelTile = page.locator('.shot-reference-contact-sheet').getByRole('button', { name: `Preview reel ${reel.name}`, exact: true });
  await expect(reelTile).toBeVisible();
  await expect.poll(() => reelTile.locator('img').evaluate(img => img.naturalWidth)).toBeGreaterThan(0);
  await reelTile.click();
  await expect(page.getByLabel('Reference video playback', { exact: true })).toHaveAttribute('src', new RegExp(`/reference-videos/${reel.media.id}$`));
  await page.getByRole('button', { name: 'Close video preview', exact: true }).click();
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  if (narrow) await refs.getByRole('tab', { name: /Selected references/ }).click();
  await refs.locator('.video-reference-list .reference-settings-button').click();
  await refs.getByLabel('Visuals', { exact: true }).selectOption('None');
  await refs.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(refs).not.toBeVisible();
  expect((await setup(request, id)).inputs.videos[0].visuals).toBe(2);
  await expect(reelTile).toBeVisible();
  if (narrow) await page.getByRole('button', { name: 'Close Shot tools', exact: true }).click();
  await page.locator('.shots-heading').getByRole('button', { name: 'Undo', exact: true }).click();
  await expect.poll(async () => (await setup(request, id)).inputs.videos[0].visuals).toBe(1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('Compose sees exact keyframes and generation captures no video input', async ({ page, request }) => {
  test.setTimeout(90000);
  const { id, reel } = await fixture(page, request, false);
  await page.reload(); await toolsTab(page, 'References');
  await page.getByRole('button', { name: 'Manage references', exact: true }).click();
  const refs = page.locator('.manual-reference-dialog');
  await refs.locator('[data-reference]').first().click();
  await refs.locator(`[data-reel-id="${reel.id}"] .add-reel`).click();
  await expect(refs.locator('.selected-keyframes > div')).toHaveCount(1);
  await refs.getByLabel('Voice for Juniper', { exact: true }).selectOption({ label: reel.name });
  await refs.getByLabel('Speaker for Juniper', { exact: true }).selectOption('JUNIPER');
  await refs.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(refs).not.toBeVisible();
  await page.locator('.shot-reference-contact-sheet').getByRole('button', { name: /^Preview Picture 2 ·/ }).click();
  await expect(page.locator('.prompt-picture-dialog')).toContainText('Picture 2');
  await page.locator('.prompt-picture-dialog').getByRole('button', { name: 'Close', exact: true }).click();
  const before = (await (await request.get('/fixtures/compositions')).json()).length;
  await openShotSetup(page);
  await page.getByRole('button', { name: 'Prompt assistance', exact: true }).click();
  const assist = page.locator('.ai-assist-dialog');
  await assist.locator('.model-chip').click();
  await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await page.getByRole('button', { name: 'Set as project default', exact: true }).click();
  await assist.getByRole('button', { name: 'Compose prompt', exact: true }).click();
  await expect.poll(async () => (await setup(request, id)).prompt).toContain('<Picture 2>');
  const calls = await (await request.get('/fixtures/compositions')).json();
  expect(calls).toHaveLength(before + 1);
  const context = calls.at(-1).context;
  expect(context.references).toHaveLength(1); expect(context.reelKeyframes).toHaveLength(1); expect(context.videos).toHaveLength(0);
  expect(context.reelKeyframes[0].authorProvidedUseGuidance).toBe(reel.useGuidance);
  expect(context.reelAudio[0].speaker).toBe('JUNIPER');
  await generateTakes(page);
  await expect.poll(async () => (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.target.projectId === id && j.kind === 'Video')?.state, { timeout: 45000 }).toBe('Completed');
  const job = (await (await request.get('/fixtures/ai-jobs')).json()).find(j => j.target.projectId === id && j.kind === 'Video');
  const capture = await (await request.get(`/fixtures/ai-jobs/${job.id}/video-inputs`)).json();
  expect(capture.inputs.map(i => i.kind)).toEqual(['Image', 'Image', 'Audio']);
  expect(capture.inputs.some(i => i.fileName.endsWith('.mp4'))).toBe(false);
  expect(JSON.stringify(capture.graph)).not.toContain('LoadVideo');
  expect(JSON.stringify(capture.graph)).not.toContain('ref_videos.ref_video_');
});
