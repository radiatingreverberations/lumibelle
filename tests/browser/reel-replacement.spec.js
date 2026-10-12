import { toolsTab } from './workspace-tools.js';
import { test, expect } from './fixtures.js';

const production = async (request, id) => (await (await request.get(`/fixtures/${id}/production`)).json());

for (const narrow of [false, true]) test(`a reel replaces another in the shots that use it (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  for (const step of ['images', 'approved', 'production-shot']) expect((await request.post(`/fixtures/${id}/${step}`)).ok()).toBe(true);
  const take = await (await request.post(`/fixtures/${id}/reference-video-take`)).json();
  const { reels: [old, next] } = await (await request.post(`/fixtures/${id}/reference-reels?takeId=${take.id}`)).json();
  expect((await request.post(`/fixtures/${id}/shot-reel?reelId=${old.id}`)).ok()).toBe(true);
  const { title } = (await (await request.get(`/fixtures/${id}/shots`)).json()).shots[0];
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await page.locator(`[data-media-id="${next.id}"]`).getByRole('button', { name: `Actions for ${next.name}`, exact: true }).click();
  await page.getByRole('menuitem', { name: 'Replace in shots…', exact: true }).click();
  const dialog = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Replace a reel in shots', exact: true }) });
  await expect(dialog.getByLabel('Reel to replace', { exact: true })).toHaveValue(old.id);
  await expect(dialog.locator('.reel-replacement-shots li')).toHaveText([title]);
  const replace = dialog.getByRole('button', { name: 'Replace in 1 shot', exact: true });
  await expect(replace).toBeEnabled();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: `test-results/reel-replacement-${narrow ? 'narrow' : 'desktop'}.png` });
  await replace.click();
  await expect(dialog.getByRole('status').filter({ hasText: 'Replaced in 1 shot.' })).toBeVisible();
  await expect(dialog.getByText('No shots use this reel.', { exact: true })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Replace in 0 shots', exact: true })).toBeDisabled();
  const [binding] = (await production(request, id)).shotContent[0].videos;
  expect(binding.media.id).toBe(next.media.id);
  // The shot keeps its own name and guidance for the reel.
  expect([binding.name, binding.description]).toEqual([old.name, 'Shot guidance']);
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).toBeHidden();
  if (narrow) return;
  // Reel details offers the same action, above the details dialog.
  await page.locator(`[data-media-id="${next.id}"]`).getByRole('button', { name: `Actions for ${next.name}`, exact: true }).click();
  await page.getByRole('menuitem', { name: 'Edit details', exact: true }).click();
  await page.getByRole('button', { name: 'Replace in shots…', exact: true }).click();
  await expect(dialog.getByText('No shots use this reel.', { exact: true })).toBeVisible();
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'Reel details', exact: true }) })).toBeVisible();
});

for (const narrow of [false, true]) test(`a shot replaces its reel, also in other shots, and keeps reviewed prompts (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  for (const step of ['images', 'approved', 'production-shot', 'production-shot']) expect((await request.post(`/fixtures/${id}/${step}`)).ok()).toBe(true);
  const take = await (await request.post(`/fixtures/${id}/reference-video-take`)).json();
  const { reels: [old, next] } = await (await request.post(`/fixtures/${id}/reference-reels?takeId=${take.id}`)).json();
  expect((await request.post(`/fixtures/${id}/shot-reel?reelId=${old.id}&reviewed=true`)).ok()).toBe(true);
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/shots`); await toolsTab(page, 'References');
  const attention = page.locator('.shot-prompt-review');
  await expect(page.locator('.shots-outline')).toContainText('Production test');
  await expect(attention).toHaveCount(0);
  await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
  const refs = page.locator('.manual-reference-dialog');
  if (narrow) await refs.getByRole('tab', { name: /Selected references/ }).click();
  await expect(refs.locator('[data-video-reference-id]')).toContainText(' MP · ');
  await refs.getByRole('button', { name: 'Video 1: Replace reel', exact: true }).click();
  const choice = refs.locator('.reel-swap-choices li').filter({ hasText: next.name });
  await expect(refs.locator('.reel-swap-choices li')).toHaveCount(1);
  // The short list and the reference list both replace the reel.
  if (narrow) await choice.getByRole('button', { name: /^Use / }).click();
  else await refs.locator(`[data-reel-id="${next.id}"]`).getByRole('button', { name: `Replace Video 1 with ${next.name}`, exact: true }).click();
  await expect(refs.locator('.reel-swap-result')).toContainText(`Uses ${next.name} · `); await expect(refs.locator('.reel-swap-result')).toContainText(`instead of ${old.name} · `);
  await refs.getByLabel('Also replace it in 1 other shot', { exact: true }).check();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: `test-results/reel-replacement-shot-${narrow ? 'narrow' : 'desktop'}.png` });
  await refs.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(refs).toBeHidden();
  await expect(page.getByText('Reel replaced in this shot and 1 other shot.', { exact: true })).toBeVisible();
  const content = (await production(request, id)).shotContent;
  expect(content.map(c => c.videos[0].media.id)).toEqual([next.media.id, next.media.id]);
  // Each shot keeps its own attachment settings, and its reviewed prompt stays reviewed.
  expect(content.map(c => [c.videos[0].name, c.videos[0].description])).toEqual([[old.name, 'Shot guidance'], [old.name, 'Shot guidance']]);
  expect(content.map(c => c.history.length)).toEqual([2, 2]);
  await expect(attention).toHaveCount(0);
});
