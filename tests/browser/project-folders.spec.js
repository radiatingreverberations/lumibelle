import { test, expect } from './fixtures.js';
import { realpathSync } from 'node:fs';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

test('a project moves to a folder, is edited there, and opens again after removal', async ({ page, request }) => {
  // The app shows full paths, and a CI runner's temp folder is a short 8.3 path (RUNNER~1).
  const outside = realpathSync.native(await mkdtemp(path.join(tmpdir(), 'lumibelle-outside-')));
  try {
    const { id } = await (await request.get('/fixtures/new')).json();
    await page.goto(`/projects/${id}/settings`);
    const storage = page.getByRole('region', { name: 'Storage', exact: true });
    await expect(storage).toContainText('saved in the library folder');
    await storage.getByRole('button', { name: 'Move to folder…', exact: true }).click();
    const move = page.getByRole('dialog').filter({ hasText: 'Destination folder' });
    await expect(move).toContainText('“Juniper finds a key”');
    await move.getByLabel('Destination folder', { exact: true }).fill(outside);
    await move.getByRole('button', { name: 'Move project', exact: true }).click();
    await expect(move).not.toBeVisible();
    const folder = path.join(outside, 'Juniper finds a key');
    await expect(storage).toContainText(folder);
    expect(JSON.parse(await readFile(path.join(folder, 'project.json'), 'utf8')).id).toBe(id);

    // Every edit is saved in the outside folder.
    await page.getByRole('button', { name: 'Edit project', exact: true }).click();
    await page.getByLabel('Project name').fill('Juniper, on the road');
    await page.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(page.locator('.project-settings-name')).toHaveText('Juniper, on the road');
    expect(JSON.parse(await readFile(path.join(folder, 'project.json'), 'utf8')).name).toBe('Juniper, on the road');

    await page.goto('/');
    const card = page.getByRole('link', { name: 'Open Juniper, on the road', exact: true });
    await expect(card.locator('.project-location')).toHaveText(folder);

    await card.click();
    await page.getByRole('link', { name: 'Settings', exact: true }).click();
    await storage.getByRole('button', { name: 'Remove from library…', exact: true }).click();
    await page.getByRole('dialog').filter({ hasText: 'stay where they are' }).getByRole('button', { name: 'Remove from library', exact: true }).click();
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole('link', { name: 'Open Juniper, on the road', exact: true })).toHaveCount(0);
    expect(JSON.parse(await readFile(path.join(folder, 'project.json'), 'utf8')).id).toBe(id);

    await page.getByRole('button', { name: 'Open project folder…', exact: true }).click();
    const open = page.getByRole('dialog').filter({ hasText: 'Project folder' });
    await open.getByLabel('Project folder', { exact: true }).fill(path.join(outside, 'missing'));
    await open.getByRole('button', { name: 'Open project', exact: true }).click();
    await expect(open.getByRole('alert')).toContainText('doesn’t exist');
    await open.getByLabel('Project folder', { exact: true }).fill(folder);
    await open.getByRole('button', { name: 'Open project', exact: true }).click();
    await expect(page).toHaveURL(/\/projects\/juniper-on-the-road/);
    await page.goto('/');
    await expect(page.getByRole('link', { name: 'Open Juniper, on the road', exact: true }).locator('.project-location')).toHaveText(folder);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  } finally {
    await rm(outside, { recursive: true, force: true });
  }
});
