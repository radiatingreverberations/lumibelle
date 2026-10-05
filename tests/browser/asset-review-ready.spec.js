import { test, expect } from './fixtures.js';
import { toolsTab } from './workspace-tools.js';

const review = page => page.locator('.prompt-enhancement-dialog');
async function enhance(page, prompt) {
  await page.getByLabel('Image prompt', { exact: true }).fill(prompt);
  const start = page.getByRole('button', { name: 'Improve prompt', exact: true });
  if (await start.isVisible()) await start.click();
  else {
    await page.locator('.prompt-enhancement .request-action-button').click();
    await page.locator('.prompt-enhancement-dialog').getByRole('button', { name: 'New request', exact: true }).click();
  }
  const composer = page.locator('.ai-assist-dialog').last();
  await composer.getByRole('button', { name: 'Enhance', exact: true }).click();
  await expect(review(page)).toBeVisible();
  await expect(review(page).getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
}

test('the asset list flags an enhanced prompt until it is applied or discarded', async ({ page, request }) => {
  const project = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${project.id}/images`)).json();
  const [person, outfit] = library.assets;
  await page.goto(`/projects/${project.id}/assets?assetId=${person.id}`);
  await toolsTab(page, 'Prompt');
  const row = page.locator(`.asset-list-row[data-asset-id="${person.id}"]`);
  const badge = row.getByRole('button', { name: `Review the enhanced prompt for ${person.name}`, exact: true });
  await expect(badge).toHaveCount(0);

  await enhance(page, 'A mouse under a chair.');
  await review(page).getByRole('button', { name: 'Close', exact: true }).click();
  await expect(review(page)).toBeHidden();
  await expect(badge).toHaveText(/Review ready/);
  await expect(page.locator('.asset-prompt-review')).toHaveCount(1);

  // From another asset, the badge selects its asset and opens the suggestion itself.
  await page.locator(`.asset-list-row[data-asset-id="${outfit.id}"] .asset-choice`).click();
  await expect(page.locator(`.asset-list-row[data-asset-id="${outfit.id}"]`)).toHaveClass(/selected/);
  await badge.click();
  await expect(review(page)).toBeVisible();
  await expect(row).toHaveClass(/selected/);
  await expect(review(page).locator('#enhancement-original')).toHaveValue('A mouse under a chair.');
  await review(page).getByRole('button', { name: 'Discard', exact: true }).click();
  await expect(review(page)).toBeHidden();
  await expect(badge).toHaveCount(0);
  await expect(page.locator('#image-prompt')).toHaveValue('A mouse under a chair.');
  // The review link is consumed, so a reload does not replay it.
  await expect(page).not.toHaveURL(/jobId=/);
  await page.reload();
  await expect(page.locator('.asset-list-row').first()).toBeVisible();
  await expect(page.locator('.asset-prompt-review')).toHaveCount(0);

  await toolsTab(page, 'Prompt');
  await enhance(page, 'A mouse beside a lamp.');
  await expect(badge).toBeVisible();
  await review(page).getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(review(page)).toBeHidden();
  await expect(badge).toHaveCount(0);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});
