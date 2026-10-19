import { test, expect } from './fixtures.js';

for (const narrow of [false, true]) test(`Reload reveals the remembered shot in the outline (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const response = await request.post(`/fixtures/${id}/long-shot-list`);
  expect(response.ok()).toBe(true);
  const selectedId = (await response.json()).shots[55].id;
  await page.setViewportSize({ width: 1152, height: 950 });
  await page.goto(`/projects/${id}/shots?shotId=${selectedId}`);
  const root = page.locator('.studio-workspace[data-studio=Shots]');
  const selected = page.locator(`.shot-outline-item[data-shot-row='${selectedId}']`);
  const outline = page.locator('.workspace-left .workspace-pane-body');
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await expect(selected).toHaveClass(/selected/);
  await expect.poll(() => page.evaluate(id => JSON.parse(localStorage.getItem(`lumibelle.position.${id}.shots.v1.data`))?.shot, id)).toBe(selectedId);
  // Reproduce remembered selection with a missing or inconsistent scroll record.
  // Remove the query to exercise the saved shot rather than a deep link.
  await page.addInitScript(({ id, narrow }) => {
    const key = `lumibelle.position.${id}.shots.v1.view`;
    const value = JSON.parse(localStorage.getItem(key)) ?? {};
    value.scroll ??= {};
    if (narrow) delete value.scroll['left:'];
    else value.scroll['left:'] = { top: 0, left: 0 };
    localStorage.setItem(key, JSON.stringify(value));
  }, { id, narrow });
  await page.evaluate(() => history.replaceState(null, '', location.pathname));
  if (narrow) await page.setViewportSize({ width: 390, height: 844 });
  await page.reload();
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await expect(root).toHaveAttribute('aria-busy', 'false');
  await expect(selected).toHaveClass(/selected/);
  if (narrow) await page.locator('[data-toggle-pane=left]').click();
  await expect(selected).toBeInViewport();
  await expect.poll(() => outline.evaluate(el => el.scrollTop)).toBeGreaterThan(1000);
  expect(await page.evaluate(() => window.scrollY)).toBe(0);
  await page.screenshot({ path: `artifacts/remembered-shot-${narrow ? 'narrow' : 'desktop'}.png` });
  // Once restored, browsing away from the selection must not snap back on rerenders.
  await outline.hover(); await page.mouse.wheel(0, -20000);
  await expect.poll(() => outline.evaluate(el => el.scrollTop)).toBe(0);
  await page.getByRole('searchbox', { name: 'Search shots', exact: true }).fill('Shot');
  await expect.poll(() => outline.evaluate(el => el.scrollTop)).toBe(0);
});
