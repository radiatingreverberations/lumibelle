import { test, expect } from './fixtures.js';

test('exporting from the selected clip renders only that part and names the file for it', async ({ page, request }) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  const shots = await (await request.post(`/fixtures/${id}/cut-takes`)).json();
  await page.goto(`/projects/${id}/cut`);
  await expect(page.locator('.cut-heading')).toHaveAttribute('data-interactive', 'true');
  await page.locator('.cut-workspace-toolbar').getByRole('button', { name: 'Choose takes', exact: true }).click();
  const chooser = page.locator('.cut-chooser');
  await chooser.getByLabel(`Take for ${shots.shots[1].title}`, { exact: true }).selectOption(shots.takes[2].id);
  await chooser.getByRole('button', { name: /Apply changes/ }).click();
  await expect(chooser).not.toBeVisible();
  await page.locator('.timeline-select').nth(1).focus(); await page.keyboard.press('Enter');

  await page.getByRole('button', { name: 'Export MP4', exact: true }).click();
  const dialog = page.locator('.cut-export-dialog');
  // The whole cut by default; a shortcut starts from the selected clip.
  await expect(dialog.getByLabel('From clip', { exact: true })).toHaveValue('1');
  await expect(dialog.getByLabel('To clip', { exact: true })).toHaveValue('2');
  await expect(dialog.locator('.cut-export-summary')).toContainText('The whole cut · 2 clips');
  await dialog.getByRole('button', { name: 'From the selected clip (2) to the end', exact: true }).click();
  await expect(dialog.getByLabel('From clip', { exact: true })).toHaveValue('2');
  await expect(dialog.locator('.cut-export-summary')).toContainText('Clip 2 · 1 clip');
  const download = page.waitForEvent('download', { timeout: 60000 });
  await dialog.getByRole('button', { name: 'Export', exact: true }).click();
  expect((await download).suggestedFilename()).toBe('cut-clips-2-2.mp4');
  await expect(page.locator('.cut-export-status')).toContainText('clip 2');
  await expect(page.getByRole('button', { name: /^Download revision \d+, clip 2 again$/ })).toBeVisible();

  // The choice is kept for the next export.
  await page.getByRole('button', { name: 'Export MP4', exact: true }).click();
  await expect(dialog.getByLabel('From clip', { exact: true })).toHaveValue('2');
});
