import { test, expect } from './fixtures.js';

test('a shot notes when its own script lines change, compares them and can be marked checked', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/coverage-script`);
  await page.goto(`/projects/${id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: '+ Shot', exact: true }).click();
  await expect(page.getByLabel('Action and camera')).toBeVisible();
  const note = page.locator('.shot-source-changed');
  await expect(note).toHaveCount(0);
  await expect.poll(async () => (await (await request.get(`/fixtures/${id}/shots`)).json()).shots.length).toBe(1);

  // A new saved script whose lines for this shot changed.
  await request.post(`/fixtures/${id}/coverage-script?mode=change`);
  await page.reload();
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await expect(note).toContainText('The script lines this shot was made from have changed.');
  await note.getByText('Compare script lines', { exact: true }).click();
  await expect(note.locator('.shot-source-compare section').last()).toContainText('Mira enters and waves.');
  await expect(note.locator('.shot-source-compare section').first()).not.toContainText('Mira enters and waves.');

  // Marking it checked takes the current lines as the shot's source, so the note stays gone.
  await note.getByRole('button', { name: 'Mark checked', exact: true }).click();
  await expect(note).toHaveCount(0);
  await expect.poll(async () => (await (await request.get(`/fixtures/${id}/shots`)).json()).shots[0].sourceExcerpt).toContain('Mira enters and waves.');
  await page.reload();
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await expect(page.getByLabel('Action and camera')).toBeVisible();
  await expect(note).toHaveCount(0);
});
