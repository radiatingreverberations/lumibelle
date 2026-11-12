import { test, expect } from './fixtures.js';

for (const section of ['', '/assets']) {
  test(`an old project upgrades before opening ${section || 'overview'}`, async ({ page, request }) => {
    const { id } = await (await request.get('/fixtures/new')).json();
    expect((await request.post(`/fixtures/${id}/format/1`)).ok()).toBe(true);
    await page.goto(`/projects/${id}${section}?view=library`);
    await expect(page.getByRole('heading', { name: 'This project needs upgrading' })).toBeVisible();
    await expect(page.getByText('legacy-json', { exact: false }).first()).toBeVisible();
    expect(await (await request.get(`/fixtures/${id}/format`)).json()).toBe(1);
    await page.getByRole('button', { name: 'Upgrade project', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'This project needs upgrading' })).toBeHidden();
    await expect.poll(async () => (await request.get(`/fixtures/${id}/format`)).json()).toBe(2);
    await expect(page).toHaveURL(new RegExp(`${section || '/juniper-finds-a-key'}\\?view=library$`));
    await expect(page.getByRole('link', { name: 'Settings', exact: true })).toBeVisible();
  });
}

test('settling queued work enables upgrade without reloading', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const job = await (await request.post(`/fixtures/${id}/upgrade-blocker`)).json();
  await request.post(`/fixtures/${id}/format/1`);
  await page.goto(`/projects/${id}/assets`);
  const upgrade = page.getByRole('button', { name: 'Upgrade project', exact: true });
  await expect(upgrade).toBeDisabled();
  await expect(page.getByRole('alert').filter({ hasText: 'queued or working AI requests' })).toBeVisible();
  await request.post(`/fixtures/upgrade-blocker/${job}/settle`);
  await expect(upgrade).toBeEnabled();
  await upgrade.click();
  await expect(page.getByRole('heading', { name: 'This project needs upgrading' })).toBeHidden();
  await expect.poll(async () => (await request.get(`/fixtures/${id}/format`)).json()).toBe(2);
});
