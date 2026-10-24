import { test, expect } from './fixtures.js';

const section = (page, name) => page.getByRole('tablist', { name: 'AI settings sections' }).getByRole('tab', { name, exact: true });
const provider = (page, name) => page.getByRole('tablist', { name: 'Connection providers' }).getByRole('tab', { name, exact: true });
const headerLink = page => page.locator('.global-navigation').getByRole('link', { name: 'AI settings', exact: true });

test('connections sidebar preserves drafts, supports keyboard navigation and restores URL selection', async ({ page }) => {
  await page.goto('/settings/ai');
  await expect(page.locator('h1')).toBeFocused();
  await expect(page.getByRole('tablist', { name: 'AI settings sections' }).getByRole('tab')).toHaveText([
    'Connections', 'Text models', 'Image models', 'Video models', 'LoRAs'
  ]);
  await expect(section(page, 'Connections')).toHaveAttribute('aria-selected', 'true');
  await expect(provider(page, 'ComfyUI')).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('Your models, ready wherever you write.')).toHaveCount(0);
  await page.locator('#comfy-url').fill('http://draft.invalid:8188');
  await provider(page, 'ComfyUI').focus();
  await page.keyboard.press('ArrowDown');
  await expect(provider(page, 'OpenAI-compatible')).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(provider(page, 'OpenRouter')).toBeFocused();
  await expect(page.locator('#comfy-url')).toBeHidden();
  await page.keyboard.press('Tab');
  await expect(page.locator('#openrouter-key')).toBeFocused();
  await page.locator('#openrouter-key').fill('draft-key');
  await provider(page, 'OpenRouter').focus(); await page.keyboard.press('End');
  await expect(provider(page, 'Claude Code')).toBeFocused();
  await page.keyboard.press('ArrowUp');
  await expect(provider(page, 'Codex')).toBeFocused();
  await page.locator('#codex-executable').fill('C:/draft/codex.exe');
  await section(page, 'Image models').click();
  await section(page, 'Connections').click();
  await expect(provider(page, 'Codex')).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('#codex-executable')).toHaveValue('C:/draft/codex.exe');
  await provider(page, 'ComfyUI').click();
  await expect(page.locator('#comfy-url')).toHaveValue('http://draft.invalid:8188');
  await provider(page, 'OpenRouter').click();
  await expect(page.locator('#openrouter-key')).toHaveValue('draft-key');
  await section(page, 'Connections').focus(); await page.keyboard.press('End');
  await expect(section(page, 'LoRAs')).toBeFocused();
  await page.keyboard.press('ArrowRight');
  await expect(section(page, 'Connections')).toBeFocused();
  // The URL keeps the tab; the provider selection is page state and resets on reload.
  await expect(page).toHaveURL(/tab=connections/); await expect(page).not.toHaveURL(/provider=/);
  await page.reload();
  await expect(provider(page, 'ComfyUI')).toHaveAttribute('aria-selected', 'true');
  await provider(page, 'OpenRouter').click();
  await expect(page.locator('#openrouter-key')).toHaveValue('');
});

test('OpenRouter request settings save with the connection and preserve text timeout drafts', async ({ page }) => {
  await page.goto('/settings/ai?tab=connections&provider=openrouter');
  await expect(page.locator('h1')).toBeFocused();
  const connection = page.locator('#connection-openrouter-form');
  const concurrency = page.getByLabel('Concurrent requests', { exact: true });
  const original = await concurrency.inputValue();
  const next = original === '4' ? '3' : '4';
  await concurrency.fill(next);
  await page.locator('#openrouter-key').fill('unsaved-key');
  await section(page, 'Text models').click(); await page.getByRole('tablist', { name: 'Text model providers' }).getByRole('tab', { name: 'Defaults & profiles', exact: true }).click();
  await expect(concurrency).toBeHidden();
  await expect(page.getByRole('heading', { name: 'Request timeout' })).toBeVisible();
  await expect(page.getByLabel('Temperature', { exact: true })).toHaveCount(0);
  await page.locator('#timeout').fill('505');
  await section(page, 'Connections').click();
  await expect(concurrency).toHaveValue(next);
  await connection.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(concurrency).toHaveValue(original);
  await expect(page.locator('#openrouter-key')).toHaveValue('');
  await concurrency.fill(next);
  await connection.getByRole('button', { name: 'Save connection', exact: true }).click();
  await expect(page.getByText('OpenRouter connection saved.', { exact: true })).toBeVisible();
  await section(page, 'Text models').click();
  await expect(page.locator('#timeout')).toHaveValue('505');
  await page.reload();
  await expect(page.locator('h1')).toBeFocused();
  await page.getByRole('tablist', { name: 'Text model providers' }).getByRole('tab', { name: 'Defaults & profiles', exact: true }).click();
  await expect(page.locator('#timeout')).not.toHaveValue('505');
  await section(page, 'Connections').click(); await provider(page, 'OpenRouter').click();
  await expect(concurrency).toHaveValue(next);
  await concurrency.fill(original);
  await connection.getByRole('button', { name: 'Save connection', exact: true }).click();
  await expect(page.getByText('OpenRouter connection saved.', { exact: true })).toBeVisible();
});

test('global settings links return to every project area and do not add tab history entries', async ({ page, request }) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  const { slug } = await (await request.get(`/fixtures/${id}/project-route`)).json();
  for (const [area, label] of [['', 'project'], ['/script', 'script'], ['/assets', 'assets'], ['/shots', 'shots'], ['/cut', 'cut'], ['/settings', 'project settings']]) {
    const origin = `/projects/${slug}${area}?selection=a%26b#details`;
    await page.goto(origin);
    await expect(page.locator('h1')).toBeFocused();
    await headerLink(page).click();
    await expect(page.locator('.writing-back')).toHaveText(`← Back to ${label}`);
    await expect(page.locator('.writing-back')).toHaveAttribute('href', origin);
    await section(page, 'Image models').click();
    await section(page, 'Connections').click();
    await provider(page, 'Codex').click();
    await expect(provider(page, 'Codex')).toHaveAttribute('aria-selected', 'true');
    await expect(page).toHaveURL(/tab=connections/); await expect(page).not.toHaveURL(/provider=/);
    const settingsUrl = page.url();
    await expect(headerLink(page)).toHaveAttribute('href', new URL(settingsUrl).pathname + new URL(settingsUrl).search);
    await headerLink(page).click();
    await expect(page.locator('.writing-back')).toHaveAttribute('href', origin);
    await page.goBack();
    await expect(page).toHaveURL(new URL(origin, page.url()).href);
  }
  await page.goto('/'); await headerLink(page).click();
  await expect(page.locator('.writing-back')).toHaveText('← All projects');
});

test('model links open the appropriate settings section with their project origin', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const { slug } = await (await request.get(`/fixtures/${id}/project-route`)).json();
  await page.goto(`/projects/${id}/script`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await page.getByRole('button', { name: 'Text model options', exact: true }).click();
  await page.getByRole('link', { name: 'Manage models', exact: true }).first().click();
  await expect(section(page, 'Text models')).toHaveAttribute('aria-selected', 'true');
  await page.locator('.writing-back').click();
  await expect(page).toHaveURL(`/projects/${slug}/script`);
  await page.goto(`/settings/ai?tab=video&projectId=${id}&returnTo=shots`);
  await expect(section(page, 'Video models')).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.writing-back')).toHaveAttribute('href', `/projects/${slug}/shots`);
  await page.goto('/settings/ai?tab=invalid&provider=invalid&returnUrl=https%3A%2F%2Foutside.test');
  await expect(section(page, 'Connections')).toHaveAttribute('aria-selected', 'true');
  await expect(provider(page, 'ComfyUI')).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.writing-back')).toHaveAttribute('href', '/');
});

test('connection panels fit desktop and mobile with a scrollable section row', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/settings/ai');
  await expect(page.locator('h1')).toBeFocused();
  const sidebar = await page.locator('.ai-provider-sidebar').boundingBox();
  const panel = await page.locator('#connection-comfyui-form').boundingBox();
  expect(sidebar.width).toBe(220); expect(panel.x).toBeGreaterThan(sidebar.x + sidebar.width);
  await page.screenshot({ path: 'test-results/ai-connections-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  for (const name of ['ComfyUI', 'OpenAI-compatible', 'OpenRouter', 'Codex']) {
    await provider(page, name).click();
    await expect(provider(page, name)).toHaveAttribute('aria-selected', 'true');
    const activePanel = page.locator('.ai-connection-content > section:not([hidden])');
    await expect(activePanel).toBeVisible();
    const bounds = await activePanel.boundingBox();
    const selector = await page.locator('.ai-provider-sidebar').boundingBox();
    expect(bounds.y).toBeGreaterThanOrEqual(selector.y + selector.height);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
    if (name === 'OpenRouter') await page.screenshot({ path: 'artifacts/openrouter-connections-mobile.png', fullPage: true });
  }
  await section(page, 'Connections').focus(); await page.keyboard.press('End');
  await expect(section(page, 'LoRAs')).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: 'artifacts/openrouter-loras-mobile.png', fullPage: true });
  await section(page, 'Connections').click();
  await page.screenshot({ path: 'test-results/ai-connections-mobile.png', fullPage: true });
});
