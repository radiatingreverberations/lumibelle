import { test, expect } from './fixtures.js';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
test.use({ actionTimeout: 15000 });

const library = async (request, id) => (await (await request.get(`/fixtures/${id}`)).json()).assets;
async function visionModel(page, assist) {
  await assist.locator('.model-chip').click();
  await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await page.getByRole('button', { name: 'Set as project default', exact: true }).click();
  await expect(assist.getByRole('button', { name: 'Suggest views', exact: true })).toBeEnabled();
}
async function manager(page, reel) {
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await page.getByRole('button', { name: `Actions for ${reel.name}`, exact: true }).click();
  await page.getByRole('menuitem', { name: 'Manage keyframes…', exact: true }).click();
  return page.locator('.keyframe-editor');
}
async function fixture(page, request, testInfo, narrow) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  const source = testInfo.outputPath('views.mp4');
  execFileSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=size=192x128:rate=24:duration=4', '-c:v', 'libx264', '-threads', '1', source]);
  const response = await request.post(`/fixtures/${id}/timeline-reel?lossless=false`, { data: readFileSync(source), headers: { 'Content-Type': 'video/mp4' } });
  expect(response.ok()).toBe(true);
  const { reel } = await response.json();
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/assets`);
  return { id, reel, editor: await manager(page, reel) };
}
for (const narrow of [false, true]) test(`AI reel views are reviewed, editable and saved explicitly (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }, testInfo) => {
  test.setTimeout(100000);
  const { id, reel, editor } = await fixture(page, request, testInfo, narrow);
  await expect(editor.locator('.keyframe-list > li')).not.toHaveCount(0);
  await expect(editor.getByLabel('View limit', { exact: true })).toHaveValue('auto');
  await expect(editor.locator('.exact-frame')).not.toBeVisible();
  await editor.getByText('Exact extracted frame · MP4', { exact: true }).click();
  await expect(editor.locator('.exact-frame')).toBeVisible();
  await editor.getByText('Exact extracted frame · MP4', { exact: true }).click();
  await page.screenshot({ path: `artifacts/keyframe-layout-${narrow ? 'narrow' : 'desktop'}.png` });
  await editor.getByLabel('View name (optional)').first().fill('My authored view');
  await editor.getByLabel('Frame notes (optional)').first().fill('Keep my authored notes');
  const before = await editor.locator('.keyframe-list > li').count();
  await editor.getByRole('button', { name: 'Save keyframes', exact: true }).click();
  await manager(page, reel);
  if (!narrow) await editor.getByLabel('View limit', { exact: true }).selectOption('3');
  await editor.getByRole('button', { name: 'Suggest reel views', exact: true }).click();
  const assist = page.getByRole('dialog', { name: 'Suggest reel views', exact: true });
  await visionModel(page, assist);
  await expect(assist.getByText(`Choose enough complementary views for useful coverage, up to ${narrow ? 9 : 3}. Review them before adding them or replacing your picks.`, { exact: true })).toBeVisible();
  await assist.getByLabel('Selection instructions (optional)').fill('Include the podium and a reverse view.');
  await assist.getByRole('button', { name: 'Suggest views', exact: true }).click();
  await expect(assist).not.toBeVisible();
  const review = editor.getByRole('region', { name: 'Review suggested views', exact: true });
  await expect(review.locator('.suggested-view-list article').first()).toBeVisible();
  await expect(review.getByText('Complementary views of the set', { exact: true })).not.toBeVisible();
  await review.locator('.view-analysis > summary').click();
  await expect(review.getByText('Complementary views of the set', { exact: true })).toBeVisible();
  await expect(review.getByRole('img', { name: /^Candidate 1 at / })).not.toBeVisible();
  await review.locator('.view-analysis > summary').click();
  await review.locator('.view-request-details > summary').click();
  await expect(review.getByRole('img', { name: /^Candidate 1 at / })).toBeVisible();
  await review.locator('.view-request-details > summary').click();
  await expect(editor.getByLabel('View name (optional)').first()).toHaveValue('My authored view');
  expect((await library(request, id)).reels.find(r => r.id === reel.id).keyframes.frames[0].notes).toBe('Keep my authored notes');
  await review.getByLabel('View name', { exact: true }).first().fill('Wide toward judge platform');
  await review.evaluate(el => el.scrollIntoView({ block: 'start' }));
  await page.screenshot({ path: `artifacts/reel-view-assist-${narrow ? 'narrow' : 'desktop'}.png` });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await review.getByRole('button', { name: 'Replace picks with selected views', exact: true }).click();
  await expect(editor.getByLabel('View name (optional)').first()).toHaveValue('Wide toward judge platform');
  await editor.getByRole('button', { name: 'Undo picks', exact: true }).click();
  await expect(editor.getByLabel('View name (optional)').first()).toHaveValue('My authored view');
  await expect(editor.locator('.keyframe-list > li')).toHaveCount(before);
  await editor.getByRole('button', { name: 'Cancel', exact: true }).click();
  const jobs = await (await request.get('/fixtures/ai-jobs')).json();
  const viewJobs = jobs.filter(j => j.kind === 'ReelViewSuggestion' && j.target.projectId === id);
  expect(viewJobs).toHaveLength(1);
  const job = viewJobs[0];
  expect(job.state).toBe('Completed');
  await page.goto(`/projects/${id}/assets?assetId=${reel.assetId}&view=reels&jobId=${job.id}`);
  await expect(review.locator('.suggested-view-list article').first()).toBeVisible();
  await review.getByRole('button', { name: 'Replace picks with selected views', exact: true }).click();
  await expect(editor.getByLabel('View name (optional)').first()).toHaveValue('Toward the judge platform');
  await editor.getByRole('button', { name: 'Save keyframes', exact: true }).click();
  await expect(editor).not.toBeVisible();
  await expect.poll(async () => (await library(request, id)).reels.find(r => r.id === reel.id).keyframes.frames[0].name).toBe('Toward the judge platform');
  const saved = (await library(request, id)).reels.find(r => r.id === reel.id).keyframes;
  expect(saved.frames[0].name).toBe('Toward the judge platform');
  expect(saved.frames[0].notes).toBe('Podium in foreground; raised platform beyond.');
  if (!narrow) {
    await manager(page, reel);
    await editor.getByRole('button', { name: 'Inspect keyframe 1', exact: true }).click();
    await editor.getByRole('button', { name: 'Save frame to Assets…', exact: true }).click();
    const image = page.locator('.reel-frame-export-dialog');
    await expect(image.getByLabel('Image name', { exact: true })).toHaveValue('Timeline reel · Toward the judge platform');
    await expect(image.getByLabel('What to keep consistent (optional)', { exact: true })).toHaveValue('Podium in foreground; raised platform beyond.');
    await image.getByRole('button', { name: 'Cancel', exact: true }).click();
    await editor.getByRole('button', { name: 'Cancel', exact: true }).click();
  }
});

test('Failed view response is inspectable and retry exposes model choice; edits invalidate an earlier proposal', async ({ page, request }, testInfo) => {
  test.setTimeout(100000);
  const { editor } = await fixture(page, request, testInfo, false);
  await editor.getByRole('button', { name: 'Suggest reel views', exact: true }).click();
  const assist = page.getByRole('dialog', { name: 'Suggest reel views', exact: true });
  await visionModel(page, assist);
  await assist.getByLabel('Selection instructions (optional)').fill('INVALID_VIEWS');
  await assist.getByRole('button', { name: 'Suggest views', exact: true }).click();
  const review = editor.getByRole('region', { name: 'Review suggested views', exact: true });
  await expect(review.getByRole('alert').filter({ hasText: 'not a complete view selection' })).toBeVisible();
  await expect(editor.getByRole('button', { name: 'Needs attention', exact: true })).toBeVisible();
  await review.getByRole('button', { name: 'Try again / change model', exact: true }).click();
  await assist.getByLabel('Selection instructions (optional)').fill('Include a reverse view');
  await assist.locator('.model-chip').click();
  await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await page.getByRole('button', { name: 'Done', exact: true }).click();
  await assist.getByRole('button', { name: 'Suggest views', exact: true }).click();
  await expect(review.locator('.suggested-view-list article').first()).toBeVisible();
  await editor.getByLabel('Frame notes (optional)').first().fill('Changed after the request');
  await expect(review.getByText('Your picks, crops or notes changed. Request a new suggestion before applying this one.', { exact: true })).toBeVisible();
  await expect(review.getByRole('button', { name: 'Replace picks with selected views', exact: true })).toBeDisabled();
});
