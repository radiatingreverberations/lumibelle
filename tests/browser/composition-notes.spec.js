import { test, expect } from './fixtures.js';
import { openShotSetup } from './workspace-tools.js';

// A first composition applies itself as reviewed; its review notes stay beside the prompt until dismissed.
test('notes from an automatically applied composition stay visible until dismissed', async ({ page, request }) => {
  test.setTimeout(60000);
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await openShotSetup(page);
  const prompt = page.getByLabel('H3 prompt', { exact: true });
  await expect(prompt).toBeEmpty();
  await page.getByRole('button', { name: 'Prompt assistance', exact: true }).click();
  const composer = page.locator('.ai-assist-dialog').filter({ has: page.getByLabel('Direction for AI', { exact: true }) });
  await composer.getByLabel('Direction for AI', { exact: true }).fill('SECTIONS_WITHOUT_SUMMARY');
  await composer.locator('.assist-composer-model .model-chip').click();
  const models = page.locator('.ai-assist-dialog').filter({ has: page.getByRole('heading', { name: 'Text model', exact: true }) });
  await models.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
  await models.getByRole('button', { name: 'Done', exact: true }).click();
  await composer.getByRole('button', { name: 'Compose prompt', exact: true }).click();
  await expect(prompt).toContainText('The camera holds as Juniper looks up.');
  const notes = page.locator('#shot-setup-prompt-panel .composed-prompt-notes');
  await expect(notes).toContainText('The response left out summary. Add it if this shot needs it.');
  await page.screenshot({ path: 'artifacts/composition-notes.png' });
  await notes.getByRole('button', { name: 'Dismiss', exact: true }).click();
  await expect(notes).toHaveCount(0);
  await page.reload();
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await openShotSetup(page);
  await expect(prompt).toContainText('The camera holds as Juniper looks up.');
  await expect(notes).toHaveCount(0);
});
