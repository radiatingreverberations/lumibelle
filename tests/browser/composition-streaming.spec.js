import { openShotSetup } from './workspace-tools.js';
import { test, expect } from './fixtures.js';
import { chooseReelReferences, reelFraming, reelSetup } from './reel-tools.js';

for (const narrow of [false, true]) test(`a composition shows its prompt as it arrives (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  const response = await request.post(`/fixtures/${id}/streaming-composition`);
  expect(response.ok()).toBeTruthy();
  const job = await response.json();
  await page.goto(`/projects/${id}/shots?view=Prompt`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await openShotSetup(page);
  const pane = page.locator('#shot-setup-prompt-panel');
  const preview = pane.getByRole('region', { name: 'Live response preview', exact: true });
  await expect(preview).toContainText('Prompt arriving');
  await expect(preview).toContainText('Mira, a courier in a yellow rain');
  await expect(preview).toContainText('Still writing');
  expect((await request.post(`/fixtures/streaming-composition-advance?jobId=${job.id}`)).ok()).toBeTruthy();
  await expect(preview).toContainText('Mira checks the address and knocks');
  // Each prompt section arrives as its own card.
  await expect(preview.locator('.streaming-preview-card')).toHaveCount(2);
  await expect(preview.locator('.streaming-preview-card').first()).toContainText('Subject definitions');
  await expect(preview.locator('.streaming-preview-card').first()).toContainText('Received');
  await expect(preview.locator('.streaming-preview-card').last()).toContainText('Summary');
  await expect(preview.locator('.streaming-preview-card').last()).toContainText('Still writing');
  // The arriving text is only a preview; the editor stays empty until the result is checked.
  await expect(pane.locator('.arriving-response')).toBeVisible();
  // A compact progress view: its own scroll box, not a dialog that grows with the response.
  expect((await pane.locator('.arriving-response').boundingBox()).height).toBeLessThanOrEqual(13 * 16 + 4);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: `artifacts/composition-streaming-${narrow ? 'narrow' : 'desktop'}.png` });
});

test('a reel prompt pair shows as it arrives', async ({ page, request }) => {
  test.setTimeout(120000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  const owner = (await (await request.get(`/fixtures/${id}`)).json()).assets.assets[0];
  await page.setViewportSize({ width: 1152, height: 1100 });
  await page.goto(`/projects/${id}/assets?assetId=${owner.id}&view=reels`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  const pictures = await chooseReelReferences(page, [`${owner.id}/${owner.images[0].id}`]);
  await pictures.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(pictures).toBeHidden();
  const setup = await reelFraming(page);
  await setup.getByLabel('Framing preset', { exact: true }).selectOption('Custom');
  // The mock streams slowly when the request contains SLOW.
  await setup.getByLabel('Instructions', { exact: true }).fill('SLOW: show the same outfit from both sides.');
  await page.locator('#reel-setup-prompt-panel .request-action-button').click();
  const composer = page.locator('.ai-assist-dialog').last();
  await composer.locator('.model-chip').click();
  await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await page.getByRole('dialog').filter({ has: page.getByRole('combobox', { name: 'Text model', exact: true }) }).getByRole('button', { name: 'Done', exact: true }).click();
  await composer.getByRole('button', { name: 'Compose pair', exact: true }).click();
  const preview = reelSetup(page).getByRole('region', { name: 'Live response preview', exact: true });
  await expect(preview.locator('.streaming-preview-card').first()).toBeVisible({ timeout: 30000 });
  await expect(preview).toContainText('Prompt pair arriving');
  await expect(preview).toContainText('Still writing');
  expect(await reelSetup(page).evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
  await page.screenshot({ path: 'artifacts/reel-pair-streaming.png' });
  await expect(preview).toHaveCount(0, { timeout: 90000 });
});
