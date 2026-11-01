import { extractionComposer, submitExtraction } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

async function expectCoverage(page, summary) {
  const composer = extractionComposer(page);
  const wasOpen = await composer.isVisible();
  if (!wasOpen) await page.getByRole('button', { name: 'Extract assets from script', exact: true }).click();
  await expect(composer.locator('.coverage-summary')).toContainText(summary);
  if (!wasOpen) {
    await composer.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(composer).toBeHidden();
  }
}

async function fixture(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/coverage-script`);
  await page.goto(`/projects/${id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('h1')).toBeFocused();
  await expect(page.locator('.asset-library .request-notice-badge')).toBeVisible();
  await expectCoverage(page, '0 / 2');
  return { id, state: async () => (await (await request.get(`/fixtures/${id}`)).json()) };
}

test('extraction reminders can be ignored without changing coverage or starting a request', async ({ page, request }) => {
  const { state } = await fixture(page, request);
  const open = page.getByRole('button', { name: 'Extract assets from script', exact: true });
  const composer = extractionComposer(page);
  await open.click();
  await composer.getByRole('checkbox', { name: 'Ignore extraction reminders for this project', exact: true }).check();
  await composer.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.locator('.asset-library .request-notice-badge')).toHaveCount(0);
  await page.reload();
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.asset-library .request-notice-badge')).toHaveCount(0);
  await open.click();
  const ignored = composer.getByRole('checkbox', { name: 'Ignore extraction reminders for this project', exact: true });
  await expect(ignored).toBeChecked();
  await expect(composer.locator('.coverage-summary')).toContainText('0 / 2');
  await ignored.uncheck();
  await composer.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.locator('.asset-library .request-notice-badge')).toBeVisible();
  expect((await state()).assets.extractionReviews).toHaveLength(0);
});

test('partial and empty extraction track saved scene content across revisions', async ({ page, request }) => {
  const { id, state } = await fixture(page, request);
  await page.getByRole('button', { name: /^(Extract assets from script|Review changes)$/ }).click();
  const dialog = page.locator('.extraction-dialog');
  await extractionComposer(page).getByRole('button', { name: 'Close', exact: true }).focus();
  await page.keyboard.press('Tab');
  expect(await extractionComposer(page).evaluate(el => el.contains(document.activeElement))).toBe(true);
  await page.keyboard.press('Escape'); await expect(extractionComposer(page)).toBeHidden();
  await expect(page.getByRole('button', { name: /^(Extract assets from script|Review changes)$/ })).toBeFocused();
  await page.getByRole('button', { name: /^(Extract assets from script|Review changes)$/ }).click();
  await expect(extractionComposer(page).getByRole('heading', { name: 'ACT 1', exact: true })).toBeVisible();
  await extractionComposer(page).locator('.coverage-scene').filter({ hasText: 'EMPTY FIELD' }).getByRole('checkbox').uncheck();
  await submitExtraction(page);
  await dialog.getByLabel('Decision for Juniper', { exact: true }).selectOption('Skip');
  await dialog.locator('.apply-extraction').click();
  await expect(dialog).toBeHidden(); // Applying closes the review.
  await expectCoverage(page, '1 / 2');
  let saved = (await state()).assets;
  expect(saved.assets).toHaveLength(0); expect(saved.extractionReviews).toHaveLength(1);
  expect(saved.extractionReviews[0].decisions.skipped).toBe(1);
  await page.getByRole('button', { name: 'Extract assets from script', exact: true }).click();
  await expect(extractionComposer(page).locator('.coverage-scene').filter({ hasText: 'ROOM' }).getByRole('checkbox')).not.toBeChecked();
  await expect(extractionComposer(page).locator('.coverage-scene').filter({ hasText: 'EMPTY FIELD' }).getByRole('checkbox')).toBeChecked();
  await submitExtraction(page);
  await expect(dialog).toContainText('No reusable assets found');
  await dialog.locator('.apply-extraction').click();
  await expect(dialog).toBeHidden();
  await expectCoverage(page, '2 / 2');
  await request.post(`/fixtures/${id}/coverage-script?mode=reformat`); await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expectCoverage(page, '2 / 2');
  await request.post(`/fixtures/${id}/coverage-script?mode=draft`); await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await page.getByRole('button', { name: 'Extract assets from script', exact: true }).click();
  await expect(extractionComposer(page)).toBeVisible();
  await expectCoverage(page, '1 / 2');
  await expect(extractionComposer(page).locator('.coverage-scene').filter({ hasText: 'Changed since review' }).getByRole('checkbox')).toBeChecked();
  await extractionComposer(page).getByRole('button', { name: 'Close', exact: true }).click();
  await request.post(`/fixtures/${id}/coverage-script?mode=change`); await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expectCoverage(page, '1 / 2');
  await page.getByRole('button', { name: 'Extract assets from script', exact: true }).click();
  const changed = extractionComposer(page).locator('.coverage-scene').filter({ hasText: 'Changed since review' });
  await expect(changed).toHaveCount(1); await expect(changed.getByRole('checkbox')).toBeChecked();
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(extractionComposer(page).getByRole('button', { name: 'Find assets', exact: true })).toBeVisible();
  await page.screenshot({ path: 'test-results/assets-coverage-mobile.png' });
});


test('a finished review uses the captured script source and conflicts retain editable proposals', async ({ page, request }) => {
  const { id, state } = await fixture(page, request);
  const captured = (await (await request.get(`/fixtures/${id}/script-source`)).json()).id;
  await page.getByRole('button', { name: /^(Extract assets from script|Review changes)$/ }).click();
  const dialog = page.locator('.extraction-dialog');
  await submitExtraction(page);
  await dialog.getByLabel('Asset name', { exact: true }).fill('Reviewed character');
  await request.post(`/fixtures/${id}/coverage-script?mode=change`);
  await request.post(`/fixtures/${id}/touch-assets`);
  await dialog.locator('.apply-extraction').click();
  await expect(dialog.getByRole('alert')).toBeVisible();
  expect((await state()).assets.extractionReviews).toHaveLength(0);
  await dialog.locator('.dialog-actions').getByRole('button', { name: 'Close', exact: true }).click();
  await page.getByRole('button', { name: 'Reload saved version', exact: true }).click();
  await page.getByRole('button', { name: /^(Extract assets from script|Review changes)$/ }).click();
  await expect(dialog.getByLabel('Asset name', { exact: true })).toHaveValue('Reviewed character');
  await dialog.locator('.apply-extraction').click();
  await expect(dialog).not.toBeVisible();
  const saved = (await state()).assets;
  expect(saved.extractionReviews).toHaveLength(1); expect(saved.extractionReviews[0].approvedScriptId).toBe(captured);
  expect(saved.assets[0].name).toBe('Reviewed character');
  await expectCoverage(page, '1 / 2');
});
