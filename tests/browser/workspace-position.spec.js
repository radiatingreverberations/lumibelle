import { openShotSetup, closeShotSetup } from './workspace-tools.js';
import { test, expect } from './fixtures.js';
const ready = page => expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
async function nav(page, name) {
  await page.getByRole('navigation', { name: 'Project navigation' }).getByRole('link', { name, exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/${name.toLowerCase()}(?:[?#]|$)`));
  if (['Script', 'Assets', 'Shots'].includes(name)) {
    await expect(page.locator('.studio-workspace')).toHaveAttribute('data-studio', name);
    await ready(page);
  }
}
const center = page => page.locator('.workspace-center .workspace-pane-body');
async function fixture(request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  await request.post(`/fixtures/${id}/approved`);
  await request.post(`/fixtures/${id}/production-shot`);
  await request.post(`/fixtures/${id}/production-shot`);
  await request.post(`/fixtures/${id}/large`);
  const route = await (await request.get(`/fixtures/${id}/project-route`)).json();
  return { id, slug: route.slug };
}
test('Readable routes keep UUID links, renamed aliases, deep links and settings returns working', async ({ page, request }) => {
  const { id, slug } = await fixture(request);
  const shots = await (await request.get(`/fixtures/${id}/shots`)).json();
  const query = `?shotId=${shots.shots[1].id}&view=Prompt`;
  await page.goto(`/projects/${id}/shots${query}#prompt`); await ready(page);
  await expect(page).toHaveURL(new RegExp(`/projects/${slug}/shots\\?shotId=.*&view=Prompt#prompt$`));
  await expect(page.locator('.shot-list-row').nth(1)).toHaveAttribute('aria-pressed', 'true');
  await request.post(`/fixtures/${id}/rename-project?name=Renamed%20Project`);
  await page.goto(`/projects/${slug}/shots${query}#prompt`); await ready(page);
  await expect(page).toHaveURL(/\/projects\/renamed-project\/shots\?shotId=.*&view=Prompt#prompt$/);
  await expect(page.locator('.shot-setup-dialog')).toBeVisible();
  await closeShotSetup(page);
  await page.getByRole('link', { name: 'AI settings', exact: true }).click();
  await expect(page.getByRole('link', { name: /Back to shots/ })).toHaveAttribute('href', /\/projects\/renamed-project\/shots/);
  await page.getByRole('link', { name: /Back to shots/ }).click(); await ready(page);
  await expect(page.locator('.shot-list-row').nth(1)).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

for (const narrow of [false, true]) test(`Assets and Shots return to their selection and filters (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  await page.setViewportSize({ width: narrow ? 390 : 1440, height: 950 });
  const { id } = await fixture(request);
  await page.goto(`/projects/${id}/assets`); await ready(page);
  await expect(page.locator('.asset-list-row.selected')).toHaveCount(1);
  if (narrow) await page.locator('[data-toggle-pane=left]').click();
  const asset = page.locator('.asset-list-row').nth(1); const assetId = await asset.getAttribute('data-asset-id');
  await asset.locator('.asset-choice').click();
  await expect(page.locator('.asset-list-row.selected')).toHaveAttribute('data-asset-id', assetId);
  await page.getByLabel('Filter media', { exact: true }).selectOption('Images');
  await page.getByRole('searchbox', { name: 'Search references' }).fill('raincoat');
  await expect.poll(() => page.evaluate(({ id, assetId }) => JSON.parse(localStorage.getItem(`lumibelle.position.${id}.assets.v1.data`))?.presentations?.[assetId]?.Filter, { id, assetId })).toBe('Images');
  await nav(page, 'Shots'); await ready(page);
  await expect(page.locator('.shot-list-row.selected')).toHaveCount(1);
  if (narrow) await page.locator('[data-toggle-pane=left]').click();
  await page.locator('.shot-list-row').nth(1).click();
  await expect(page.locator('.shot-list-row').nth(1)).toHaveAttribute('aria-pressed', 'true');
  await page.locator('[data-workspace-group=center]').getByRole('tab', { name: 'Takes', exact: true }).click();
  await nav(page, 'Assets'); await ready(page);
  await expect(page.locator('.asset-list-row.selected')).toHaveAttribute('data-asset-id', assetId);
  await expect(page.getByLabel('Filter media', { exact: true })).toHaveValue('Images');
  await expect(page.getByRole('searchbox', { name: 'Search references' })).toHaveValue('raincoat');
  await nav(page, 'Shots'); await ready(page);
  await expect(page.locator('.shot-list-row').nth(1)).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('[data-workspace-group=center]').getByRole('tab', { name: 'Takes', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.reload(); await ready(page);
  await expect(page.locator('.shot-list-row').nth(1)).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('Script restores its passage and cursor without replaying edits or stealing focus', async ({ page, request }) => {
  const { id } = await fixture(request);
  await page.setViewportSize({ width: 1440, height: 950 });
  await page.goto(`/projects/${id}/script`); await ready(page);
  await page.locator('.outline-title').last().click();
  await expect.poll(() => center(page).evaluate(el => el.scrollTop)).toBeGreaterThan(1000);
  const editor = page.getByRole('textbox', { name: 'Screenplay', exact: true });
  const cursor = () => page.evaluate(id => JSON.parse(localStorage.getItem(`lumibelle.position.${id}.script.v1.data`))?.selection, id);
  await editor.press('ArrowRight');
  await expect.poll(async () => (await cursor())?.AnchorOffset).toBe(1);
  const selection = await cursor();
  const passage = page.locator(`[data-block-id="${selection.AnchorBlockId}"]`);
  await expect(passage).toBeInViewport();
  const text = await page.getByRole('textbox', { name: 'Screenplay', exact: true }).innerText();
  await nav(page, 'Assets'); await ready(page);
  await nav(page, 'Script'); await ready(page);
  await expect(passage).toBeInViewport();
  expect(await page.getByRole('textbox', { name: 'Screenplay', exact: true }).innerText()).toBe(text);
  expect(await page.getByRole('textbox', { name: 'Screenplay', exact: true }).evaluate(el => el === document.activeElement)).toBe(false);
  await editor.focus();
  await expect.poll(() => page.evaluate(() => {
    const range = window.getSelection();
    return { block: range?.anchorNode?.parentElement?.closest('[data-block-id]')?.dataset.blockId, offset: range?.anchorOffset };
  })).toEqual({ block: selection.AnchorBlockId, offset: 1 });
  await page.keyboard.press('ArrowRight');
  await expect.poll(async () => (await cursor())?.AnchorOffset).toBe(2);
  expect((await cursor()).AnchorBlockId).toBe(selection.AnchorBlockId);
  await page.reload(); await ready(page);
  await expect(passage).toBeInViewport();
});

test('Unavailable browser storage still allows opening and navigating projects', async ({ page, request }) => {
  const { id } = await fixture(request);
  await page.addInitScript(() => { Storage.prototype.getItem = Storage.prototype.setItem = () => { throw new DOMException('Disabled', 'SecurityError'); }; });
  await page.goto(`/projects/${id}/shots`); await ready(page);
  await nav(page, 'Assets'); await ready(page);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('Global setup follows shots and tabs while shot browsing stays independent', async ({ page, context, request }) => {
  await request.post('/fixtures/generation-setups/reset');
  const { id, slug } = await fixture(request);
  await page.goto(`/projects/${slug}/shots`); await ready(page);
  await openShotSetup(page, 'Generation settings');
  await page.locator('.setup-menu summary').click();
  await page.getByRole('button', { name: 'New setup', exact: true }).click();
  await expect(page.getByRole('combobox', { name: 'Named setup', exact: true }).locator('option')).toHaveCount(2);
  await expect(page.getByLabel('Setup name', { exact: true })).toHaveValue('New setup');
  await expect(page.getByRole('combobox', { name: 'Named setup', exact: true })).toBeEnabled();
  const setup = await page.getByRole('combobox', { name: 'Named setup', exact: true }).inputValue();
  await page.locator('.setup-menu summary').click();
  await closeShotSetup(page);
  await page.locator('[data-workspace-group=center]').getByRole('tab', { name: 'Takes', exact: true }).click();
  await page.locator('.takes-heading').getByRole('combobox', { name: 'Filter takes by setup', exact: true }).selectOption(setup);
  await page.locator('.shot-list-row').nth(1).click();
  await expect(page.locator('.shot-list-row').nth(1)).toHaveAttribute('aria-pressed', 'true');
  // A shot without takes opens on Shot; the setup filter still applies when its Takes view is opened.
  await expect(page.locator('[data-workspace-group=center]').getByRole('tab', { name: 'Shot', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.locator('[data-workspace-group=center]').getByRole('tab', { name: 'Takes', exact: true }).click();
  await expect(page.locator('.takes-heading').getByRole('combobox', { name: 'Filter takes by setup', exact: true })).toHaveValue(setup);
  await page.locator('.shot-list-row').first().click();
  await openShotSetup(page, 'Generation settings');
  await expect(page.getByRole('combobox', { name: 'Named setup', exact: true })).toHaveValue(setup);
  await closeShotSetup(page);
  const other = await context.newPage();
  await other.goto(`/projects/${slug}/shots`); await ready(other);
  await openShotSetup(other, 'Generation settings');
  await expect(other.getByRole('combobox', { name: 'Named setup', exact: true })).toHaveValue(setup);
  await closeShotSetup(other);
  await other.locator('.shot-list-row').nth(1).click();
  await expect(other.locator('.shot-list-row').nth(1)).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.shot-list-row').first()).toHaveAttribute('aria-pressed', 'true');
  await other.close();
  await page.goto(`/projects/${id}/shots?shotId=00000000-0000-0000-0000-000000000001&setupId=00000000-0000-0000-0000-000000000002`); await ready(page);
  await expect(page.locator('.shot-list-row.selected')).toHaveCount(1);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('Malformed browsing state falls back without breaking editors', async ({ page, request }) => {
  const { id } = await fixture(request);
  await page.addInitScript(id => {
    localStorage.setItem(`lumibelle.position.${id}.assets.v1.data`, JSON.stringify({ asset: 'missing', presentations: { broken: null } }));
    localStorage.setItem(`lumibelle.position.${id}.assets.v1.view`, JSON.stringify({ scroll: 'invalid', tabs: 4 }));
    localStorage.setItem(`lumibelle.position.${id}.shots.v1.data`, '{broken');
  }, id);
  await page.goto(`/projects/${id}/assets`); await ready(page);
  await expect(page.locator('.asset-list-row.selected')).toHaveCount(1);
  await nav(page, 'Shots'); await ready(page);
  await expect(page.locator('.shot-list-row.selected')).toHaveCount(1);
  await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
});

test('Cut restores the selected clip, exact paused frame and timeline zoom', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const shots = await (await request.post(`/fixtures/${id}/cut-takes`)).json();
  await page.goto(`/projects/${id}/cut`);
  await expect(page.locator('.cut-heading')).toHaveAttribute('data-interactive', 'true');
  await page.locator('.cut-workspace-toolbar').getByRole('button', { name: 'Choose takes', exact: true }).click();
  await page.locator('.cut-chooser').getByLabel(`Take for ${shots.shots[1].title}`, { exact: true }).selectOption(shots.takes[2].id);
  await page.locator('.cut-chooser').getByRole('button', { name: /Apply changes/ }).click();
  await expect(page.locator('.timeline-clip')).toHaveCount(2);
  await page.locator('.timeline-select').nth(1).focus(); await page.keyboard.press('Enter');
  await expect(page.locator('.timeline-clip').nth(1)).toHaveClass(/selected/);
  const clip = await page.locator('.timeline-clip.selected').getAttribute('data-clip-id');
  await page.getByRole('slider', { name: 'Cut position', exact: true }).focus(); await page.keyboard.press('End');
  const player = page.locator('.cut-player');
  await expect(player).toHaveAttribute('data-frame-index', String(shots.takes[2].frames.length - 1));
  const frame = await player.getAttribute('data-frame-index');
  await page.locator('[data-zoom=in]').click();
  await nav(page, 'Assets'); await ready(page);
  await nav(page, 'Cut');
  await expect(page.locator('.timeline-clip.selected')).toHaveAttribute('data-clip-id', clip);
  await expect(player).toHaveAttribute('data-frame-index', frame);
  await expect(player).toHaveAttribute('data-playing', 'false');
  await expect(page.locator('[data-zoom=fit]')).toHaveAttribute('aria-pressed', 'false');
  await page.reload();
  await expect(player).toHaveAttribute('data-frame-index', frame);
  await expect(player).toHaveAttribute('data-playing', 'false');
});

test('Project settings restores page scroll, and overview does not inherit the prior route section', async ({ page, request }) => {
  const { id, slug } = await fixture(request);
  await page.setViewportSize({ width: 390, height: 600 });
  await page.goto(`/projects/${id}/settings`);
  await expect(page.locator('h1')).toBeFocused();
  await expect(page.locator('[data-page-position=settings]')).toHaveAttribute('data-ready', 'true');
  await page.evaluate(() => window.scrollTo(0, document.body.scrollHeight));
  await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(100);
  const top = await page.evaluate(() => scrollY);
  await page.goto(`/projects/${slug}`);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Juniper finds a key');
  await nav(page, 'Settings');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Project settings');
  await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(top - 50);
});

test('Asset galleries retain separate anchors and explicit links reveal hidden targets', async ({ page, request }) => {
  const { id, slug } = await fixture(request);
  await request.post(`/fixtures/${id}/ux-images?count=25`);
  await page.goto(`/projects/${slug}/assets`); await ready(page);
  const owner = await page.locator('.asset-list-row.selected').getAttribute('data-asset-id');
  // Each look group is a horizontally scrolling strip.
  const gallery = page => page.locator('.asset-media-gallery').first();
  await page.locator('.asset-media-card').last().scrollIntoViewIfNeeded();
  await expect.poll(() => gallery(page).evaluate(el => el.scrollLeft)).toBeGreaterThan(500);
  const left = await gallery(page).evaluate(el => el.scrollLeft);
  await page.locator('.asset-list-row').nth(1).locator('.asset-choice').click();
  await expect(page.locator('.asset-list-row.selected')).not.toHaveAttribute('data-asset-id', owner);
  await page.locator(`.asset-list-row[data-asset-id="${owner}"] .asset-choice`).click();
  await expect(page.locator('.asset-list-row.selected')).toHaveAttribute('data-asset-id', owner);
  await expect.poll(() => gallery(page).evaluate(el => el.scrollLeft)).toBeGreaterThan(left - 100);
  await page.getByRole('searchbox', { name: 'Search references' }).fill('no matching media');
  await expect(page.locator('.asset-media-card')).toHaveCount(0);
  await page.goto(`/projects/${slug}/assets?assetId=${owner}`); await ready(page);
  await expect(page.getByRole('searchbox', { name: 'Search references' })).toHaveValue('');
  await expect(page.locator('.asset-media-card')).toHaveCount(26);
});

test('Narrow drawers restore their scrolling when opened after returning to a workspace', async ({ page, request }) => {
  const { slug } = await fixture(request);
  await page.setViewportSize({ width: 390, height: 650 });
  await page.goto(`/projects/${slug}/script`); await ready(page);
  await page.locator('[data-toggle-pane=left]').click();
  const outline = page.locator('.workspace-left .workspace-pane-body');
  await outline.hover();
  await page.mouse.wheel(0, 20000);
  await expect(page.locator('.outline-title').last()).toBeInViewport();
  await expect.poll(() => outline.evaluate(el => el.scrollTop)).toBeGreaterThan(1000);
  const top = await outline.evaluate(el => el.scrollTop);
  await page.keyboard.press('Escape');
  await nav(page, 'Assets'); await ready(page);
  await nav(page, 'Script'); await ready(page);
  await page.locator('[data-toggle-pane=left]').click();
  await expect.poll(() => outline.evaluate(el => el.scrollTop)).toBeGreaterThan(top - 50);
  await expect(page.getByRole('button', { name: 'Close Outline', exact: true })).toBeFocused();
});
