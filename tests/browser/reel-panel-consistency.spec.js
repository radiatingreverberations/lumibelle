import { test, expect } from './fixtures.js';

const library = async (request, id) => (await (await request.get(`/fixtures/${id}`)).json()).assets;

for (const narrow of [false, true]) test(`reel tools share the compact shot layout and preserve edits across dialogs (${narrow ? 'mobile' : 'desktop'})`, async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  const owner = (await library(request, id)).assets[0];
  await page.setViewportSize({ width: narrow ? 390 : 1152, height: narrow ? 844 : 1244 });
  await page.goto(`/projects/${id}/assets?assetId=${owner.id}&view=reels`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  if (narrow) await page.locator('[data-toggle-pane=right]').click();
  const tools = page.locator('.reel-tools');
  const editor = page.locator('.reel-setup-dialog');
  await expect(tools.getByRole('button', { name: 'Prompt', exact: true })).toBeInViewport();
  await expect(tools.getByRole('button', { name: 'Edit reel preset', exact: true })).toBeInViewport();
  await expect(tools.getByLabel('Reel resolution', { exact: true })).toBeInViewport();
  await expect(tools.getByLabel('Reel takes', { exact: true })).toBeInViewport();
  await expect(tools.getByRole('button', { name: 'Generation settings', exact: true })).toHaveCount(0);
  await expect(tools.getByLabel('Reel name', { exact: true })).toHaveCount(0);
  await expect(tools.getByRole('button', { name: 'Generate reel', exact: true })).toBeInViewport();
  await expect(tools.getByRole('button', { name: 'Open Prompt', exact: true })).toHaveCount(0);
  // Prompt sits right above Generate and says what is missing; the unavailable button explains itself to assistive technology.
  const promptButton = tools.getByRole('button', { name: 'Prompt', exact: true });
  await expect(promptButton).toContainText('Missing');
  await expect(promptButton).toHaveClass(/next-step/);
  const below = await tools.locator('.reel-generate-target').boundingBox(), above = await promptButton.boundingBox();
  expect(above.y + above.height).toBeLessThanOrEqual(below.y);
  await expect(tools.getByRole('group', { name: 'Generate reel unavailable: add a prompt and use guidance in Prompt.' })).toBeVisible();
  expect(await tools.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);

  await tools.getByRole('button', { name: 'Manage references', exact: true }).click();
  const references = page.locator('.reel-pictures-dialog');
  await references.locator(`[data-reference="${owner.id}/${owner.images[0].id}"]`).click();
  await references.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(references).not.toBeVisible();
  await expect(tools.locator('.shot-reference-tile')).toHaveCount(1);
  await expect(tools.locator('.shot-reference-counts')).toContainText('1/9');
  await page.screenshot({ path: `artifacts/reel-consistency-${narrow ? 'mobile' : 'desktop'}.png` });

  await tools.getByRole('button', { name: 'Prompt', exact: true }).click();
  await expect(editor).toBeVisible();
  await editor.getByLabel('Reel name', { exact: true }).fill('Consistent reel');
  await editor.locator('.reel-voice-options > summary').click();
  await editor.getByLabel('Voice mode', { exact: true }).selectOption('Silent');
  await editor.locator('.reel-direction-options > summary').click();
  await editor.getByLabel('Framing preset', { exact: true }).selectOption('Custom');
  await editor.getByLabel('Instructions', { exact: true }).fill('Show the same outfit from both sides.');
  const prompt = editor.getByRole('textbox', { name: 'H3 prompt', exact: true });
  await expect(editor.locator('[data-prompt-ready]')).toHaveAttribute('data-prompt-ready', 'true');
  await prompt.fill('Manually authored prompt — café 👋');
  await editor.getByLabel('Use guidance', { exact: true }).fill('Appearance and clothing reference.');
  await editor.getByRole('tab', { name: 'Preset', exact: true }).click();
  await expect(editor.getByLabel('Generation preset', { exact: true })).toBeVisible();
  // Keyboard tab navigation must return to the same editor without losing its history.
  await editor.getByRole('tab', { name: 'Preset', exact: true }).press('Home');
  await expect(editor.getByRole('tab', { name: 'Prompt', exact: true })).toBeFocused();
  await expect(prompt).toHaveText('Manually authored prompt — café 👋');
  await editor.getByRole('button', { name: 'Done', exact: true }).click();
  await expect(editor).not.toBeVisible();
  await expect(tools.getByRole('button', { name: 'Prompt', exact: true })).toBeFocused();
  await expect.poll(async () => (await library(request, id)).reelDrafts[0].prompt).toBe('Manually authored prompt — café 👋');
  // With a prompt pair, Generate is available and no longer explains itself.
  await expect(tools.getByRole('group', { name: /Generate reel unavailable/ })).toHaveCount(0);

  await tools.getByRole('button', { name: 'Edit reel preset', exact: true }).click();
  await expect(editor.getByRole('tab', { name: 'Preset', exact: true })).toHaveAttribute('aria-selected', 'true');
  await editor.getByRole('tab', { name: 'Prompt', exact: true }).click();
  await expect(prompt).toHaveText('Manually authored prompt — café 👋');
  await expect(editor.getByRole('button', { name: 'Undo prompt edit', exact: true })).toBeEnabled();
  await editor.getByRole('button', { name: 'Undo prompt edit', exact: true }).click();
  await expect(prompt).toBeEmpty();
  await editor.getByRole('button', { name: 'Redo prompt edit', exact: true }).click();
  await expect(prompt).toHaveText('Manually authored prompt — café 👋');
  expect(await editor.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
  await page.screenshot({ path: `artifacts/reel-prompt-${narrow ? 'mobile' : 'desktop'}.png` });
  // Closing with Escape must flush browser typing, just as Done does.
  await prompt.fill('Latest edit before Escape');
  await prompt.press('Escape');
  await expect(editor).not.toBeVisible();
  await expect.poll(async () => (await library(request, id)).reelDrafts[0].prompt).toBe('Latest edit before Escape');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
