import { scriptAssist, closeComposer } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

const drawer = page => page.getByRole('dialog', { name: 'AI activity', exact: true });
const jobs = async (request, project) => (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === project);
async function open(page) { await page.locator('.ai-activity-trigger').click(); await expect(drawer(page)).toBeVisible(); }
async function close(page) { await drawer(page).getByRole('button', { name: 'Close AI activity' }).click(); }
async function draft(page, request, instructions) {
  const { id } = await (await request.get('/fixtures/new')).json();
  await page.goto(`/projects/${id}/script`);
  await expect(page.locator(".studio-workspace")).toHaveAttribute("data-ready", "true");
  await scriptAssist(page);
  await expect(page.getByRole('button', { name: 'Draft script', exact: true })).toBeEnabled();
  await closeComposer(page);
  await scriptAssist(page);
  await page.locator('#script-instructions').fill(instructions);
  await page.getByRole('button', { name: 'Draft script', exact: true }).click();
  return id;
}

test('visible completed review is acknowledged and history can be cleared, undone and restored without losing work', async ({ page, request }) => {
  const id = await draft(page, request, 'SLOW: A rabbit hears a kettle whistle.');
  await page.getByRole('button', { name: /View request/ }).click();
  const review = page.getByRole('dialog', { name: 'Review script proposal' });
  await expect(review).toBeVisible();
  await expect.poll(async () => (await jobs(request, id))[0]?.state).toBe('Completed');
  await expect(review.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled();
  await expect.poll(async () => (await jobs(request, id))[0]?.unread).toBe(false);
  await review.getByRole('button', { name: 'Close', exact: true }).click();
  const original = await (await request.get(`/fixtures/${id}`)).json();
  await open(page); await drawer(page).getByRole('tab', { name: 'History' }).click();
  await drawer(page).getByLabel('Project', { exact: true }).selectOption(id);
  const card = drawer(page).locator('.ai-activity-job');
  await expect(card).toContainText('Reported cost · $0.0000123 USD');
  await card.getByText('Usage details', { exact: true }).click();
  await expect(card).toContainText('42 input · 20 output tokens');
  await expect(card).toContainText('Mock provider');
  await page.screenshot({ path: 'artifacts/validation/activity-cost-desktop.png' });
  await drawer(page).getByRole('button', { name: 'Clear history', exact: true }).click();
  await expect(card).toHaveCount(0);
  await drawer(page).getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(card).toHaveCount(1);
  await drawer(page).getByRole('button', { name: 'Clear history', exact: true }).click();
  await close(page); await page.reload(); await open(page);
  await drawer(page).getByRole('tab', { name: 'History' }).click();
  await drawer(page).getByLabel('Project', { exact: true }).selectOption(id);
  await expect(card).toHaveCount(0);
  await drawer(page).getByLabel('Show cleared').check();
  await expect(card).toContainText('Cleared from activity');
  await card.getByRole('button', { name: 'Restore to activity' }).click();
  await expect(card).not.toContainText('Cleared from activity');
  expect((await jobs(request, id))[0].unread).toBe(false);
  expect((await (await request.get(`/fixtures/${id}`)).json()).history).toEqual(original.history);
});

test('unseen failures stay unread until inspected and inspection stays open after acknowledgement', async ({ page, request }) => {
  const id = await draft(page, request, 'SLOW INVALID: A rabbit hears a bell.');
  await open(page);
  await expect.poll(async () => (await jobs(request, id))[0]?.state).toBe('NeedsAttention');
  expect((await jobs(request, id))[0].unread).toBe(true);
  await drawer(page).getByRole('tab', { name: 'Needs attention' }).click();
  await drawer(page).getByLabel('Project', { exact: true }).selectOption(id);
  await drawer(page).getByRole('button', { name: 'Inspect response', exact: true }).click();
  await expect(drawer(page).locator('.ai-job-response')).toContainText('Invalid JSON');
  await expect.poll(async () => (await jobs(request, id))[0].unread).toBe(false);
  await expect(drawer(page).locator('.ai-job-response')).toBeVisible();
  await drawer(page).getByRole('button', { name: 'Hide response' }).click();
  await expect(drawer(page).locator('.ai-job-response')).toHaveCount(0);
});

test('provider details are collapsed, show separate capacity and key allowance, and stop polling when hidden', async ({ page, request }) => {
  await page.goto('/settings/ai'); await expect(page.locator('h1')).toBeFocused(); await open(page);
  const checks = async () => (await (await request.get('/fixtures/capacity')).json());
  const initial = await checks();
  const toggles = drawer(page).locator('.ai-provider-toggle');
  await expect(toggles).toHaveText([/ComfyUI.*VRAM/, /OpenAI-compatible.*Server/, /OpenRouter.*Allowance/, /Codex.*Allowance/, /Claude Code.*Limits/]);
  for (const button of await toggles.all()) await expect(button).toHaveAttribute('aria-expanded', 'false');
  await toggles.nth(0).click();
  const comfy = drawer(page).locator('#activity-capacity-ComfyUI');
  await expect(comfy).toContainText(/7[.,]0 \/ 20[.,]0 GiB/);
  await expect(comfy).toContainText(/1[.,]0 \/ 8[.,]0 GiB/);
  await expect(comfy.locator('meter')).toHaveCount(2);
  await toggles.filter({ hasText: 'OpenRouter' }).click();
  const router = drawer(page).locator('#activity-capacity-OpenRouter');
  await expect(router).toContainText('Key allowance remaining: $12.25 USD');
  await expect(router).toContainText('Spending cap: $20 USD · resets monthly');
  await expect.poll(async () => (await checks()).comfyChecks, { timeout: 8000 }).toBeGreaterThan(initial.comfyChecks + 1);
  await page.screenshot({ path: 'artifacts/validation/activity-capacity-desktop.png' });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: 'artifacts/validation/activity-capacity-mobile.png' });
  await toggles.nth(0).click();
  const collapsed = await checks();
  await page.waitForTimeout(5500);
  expect((await checks()).comfyChecks).toBe(collapsed.comfyChecks);
  expect((await checks()).routerChecks).toBe(collapsed.routerChecks);
  await drawer(page).getByRole('tab', { name: 'Active', exact: true }).focus();
  await page.keyboard.press('End'); await expect(drawer(page).getByRole('tab', { name: 'History' })).toBeFocused();
  await page.keyboard.press('Home'); await expect(drawer(page).getByRole('tab', { name: 'Active', exact: true })).toBeFocused();
  await page.keyboard.press('Escape'); await expect(drawer(page)).not.toBeVisible();
  await expect(page.locator('.ai-activity-trigger')).toBeFocused();
  const closed = await checks(); await page.waitForTimeout(5500);
  expect(await checks()).toEqual(closed);
});
