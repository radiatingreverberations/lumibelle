import { test, expect } from './fixtures.js';

async function setup(request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  const plan = await request.post(`/fixtures/${id}/review-plan`);
  expect(plan.ok(), await plan.text()).toBeTruthy();
  return { id, plan: (await plan.json()).plan };
}
const state = async (request, id) => (await request.get(`/fixtures/${id}/review-state`)).json();

const nav = page => page.getByRole('region', { name: 'Checks and plans' });
const center = page => page.getByRole('region', { name: 'Plan steps' });
const tools = page => page.getByRole('region', { name: 'Plan', exact: true });

test('review uses the workspace panes, lists findings, and a plan runs only after it is approved', async ({ page, request }) => {
  const { id, plan } = await setup(request);
  await page.goto(`/projects/${id}/review?planId=${plan}`);
  await expect(page.getByRole('heading', { name: 'Review', level: 1 })).toBeFocused();
  await expect(page.getByRole('navigation', { name: 'Project navigation' }).getByRole('link', { name: 'Review', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-studio', 'Review');

  // The link opened the plan. The list holds it and the checks.
  await expect(nav(page).getByRole('button', { name: /Notes on rough cut 2/ })).toHaveAttribute('aria-current', 'true');
  await expect(center(page).getByRole('heading', { name: /Notes on rough cut 2/ })).toBeVisible();
  await expect(tools(page)).toContainText('Waiting for your approval');
  await expect(tools(page)).toContainText('Proposed by claude-code');
  await expect(tools(page)).toContainText('Approving this plan makes 3 changes to the project and renders 0 takes.');
  await expect(center(page).getByText('She holds nothing, but the next shot shows a letter.')).toBeVisible();
  const steps = center(page).locator('.review-steps > li');
  await expect(steps).toHaveCount(3);
  await expect(steps.nth(0)).toContainText('Change the prompt of #1 and mark it reviewed');
  await steps.nth(0).getByText('Compare prompts').click();
  await expect(steps.nth(0)).toContainText('holding a letter');

  // Nothing has changed yet.
  const before = await state(request, id);
  expect(before.prompts[0]).toBe('The camera holds as she looks up.');
  expect(before.selected[1]).toBeNull();

  // Reject one step; the totals follow and the step can be kept again.
  await steps.nth(1).getByRole('button', { name: 'Reject step' }).click();
  await expect(steps.nth(1)).toContainText('Rejected');
  await expect(tools(page)).toContainText('makes 2 changes to the project');
  await expect(steps.nth(1).getByRole('button', { name: 'Keep step' })).toBeVisible();

  await tools(page).getByRole('button', { name: 'Approve plan' }).click();
  await expect(tools(page)).toContainText('Finished');
  await expect(steps.nth(0)).toContainText('Done');
  await expect(steps.nth(2)).toContainText('Done');
  await expect(tools(page)).toContainText('Approved here');
  await expect(tools(page).getByRole('button', { name: 'Approve plan' })).toHaveCount(0);

  const after = await state(request, id);
  expect(after.prompts[0]).toBe('The camera holds as she looks up, holding a letter.');
  expect(after.selected[1]).toBeNull();
  expect(after.plans[0].steps).toEqual(['Done', 'Rejected', 'Done']);
  const cut = await (await request.get(`/fixtures/${id}/cut`)).json();
  expect(cut.clips.length).toBe(1);

  // The checks are one click away: the third shot has no takes. Groups with nothing certain in them start closed.
  await nav(page).getByRole('button', { name: /Continuity checks/ }).click();
  const checks = page.getByRole('region', { name: 'Continuity checks' });
  await expect(checks.getByRole('status')).toContainText('worth a look');
  await expect(page.getByRole('region', { name: 'About the checks' }).getByRole('button', { name: 'Measure sound' })).toBeVisible();
  await checks.getByText('Shots without a selected take').click();
  await expect(checks.getByText('No takes yet.')).toBeVisible();
  await expect(checks.getByRole('link', { name: /#3 Not generated/ }).first()).toHaveAttribute('href', /\/shots\?shotId=/);

  // A finished plan can be deleted from its menu, after a confirmation.
  await nav(page).getByRole('button', { name: /Notes on rough cut 2/ }).click();
  await tools(page).getByRole('button', { name: 'Plan actions' }).click();
  await page.getByRole('menuitem', { name: 'Delete plan…' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete plan', exact: true }).click();
  await expect(nav(page).getByText(/The plans it proposes appear here/)).toBeVisible();
  await expect(checks).toBeVisible();
});

test('the panes become drawers on a phone and choosing a plan reveals it', async ({ page, request }) => {
  const { id } = await setup(request);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`/projects/${id}/review`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  // A submitted plan opens first, since it is waiting for the director.
  await expect(center(page).getByRole('heading', { name: /Notes on rough cut 2/ })).toBeInViewport();
  await page.locator('[data-toggle-pane=right]').click();
  await expect(tools(page).getByRole('button', { name: 'Approve plan' })).toBeInViewport();
});

test('a running plan shows where it is, renders through the queue and waits for a pick', async ({ page, request }) => {
  const { id } = await setup(request);
  const render = await request.post(`/fixtures/${id}/review-render-plan`);
  expect(render.ok(), await render.text()).toBeTruthy();
  const { plan } = await render.json();
  await page.goto(`/projects/${id}/review?planId=${plan}`);
  await expect(center(page).getByRole('heading', { name: /Render the last shot/ })).toBeVisible();
  await expect(tools(page)).toContainText('renders 2 takes');
  const steps = center(page).locator('.review-steps > li');
  await expect(steps.nth(1)).toContainText('Render 2 takes of #3');

  await tools(page).getByRole('button', { name: 'Approve plan' }).click();
  // The progress line follows the plan without a reload, through to the pick.
  const progress = center(page).locator('.review-plan-progress');
  await expect(progress).toContainText('of 2 steps done');
  await expect(steps.nth(1)).toContainText('Pick a take', { timeout: 30000 });
  await expect(progress).toContainText('1 of 2 steps done · 1 take choice waiting for you');
  expect((await state(request, id)).selected[2]).toBeNull();

  await steps.nth(1).getByRole('button', { name: 'Use this take' }).first().click();
  await expect(tools(page)).toContainText('Finished');
  await expect(progress).toHaveCount(0);
  expect((await state(request, id)).selected[2]).not.toBeNull();
});

test('stopping a plan with a render waiting cancels it and closes every step', async ({ page, request }) => {
  const { id } = await setup(request);
  const { plan } = await (await request.post(`/fixtures/${id}/review-render-plan`)).json();
  await page.goto(`/projects/${id}/review?planId=${plan}`);
  await tools(page).getByRole('button', { name: 'Approve plan' }).click();
  await expect(center(page).locator('.review-plan-progress')).toBeVisible();
  await tools(page).getByRole('button', { name: 'Stop plan' }).click();
  await expect(tools(page)).toContainText('Stopped');
  const steps = (await state(request, id)).plans.find(p => p.id === plan).steps;
  expect(steps[0]).toBe('Done');
  expect(steps[1]).toBe('Skipped');
  await expect(center(page).getByRole('button', { name: 'Use this take' })).toHaveCount(0);
});

test('agent access is off until turned on, and the endpoint needs the token', async ({ page, request, baseURL }) => {
  const call = token => request.post('/mcp', {
    headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    data: { jsonrpc: '2.0', id: 1, method: 'tools/list' }
  });
  expect((await call('anything')).status()).toBe(404);

  await page.goto('/settings/ai');
  // Focus moves to the heading once the page is interactive.
  await expect(page.locator('h1')).toBeFocused();
  // It belongs to the Connections list only.
  const entry = page.getByRole('tablist', { name: 'Connection providers' }).getByRole('tab', { name: 'Agent access', exact: true });
  await entry.click();
  await expect(entry).toHaveAttribute('aria-selected', 'true');
  const panel = page.getByRole('tabpanel', { name: 'Agent access' });
  await expect(panel).toBeVisible();
  await expect(panel).not.toContainText('Claude');
  const enabled = panel.getByLabel('Allow agent access from this computer');
  await expect(enabled).not.toBeChecked();
  await expect(panel.getByLabel('Access token')).toHaveCount(0);
  await enabled.check();

  await expect(panel.getByLabel('Server address')).toHaveValue(`${baseURL}/mcp`);
  const field = panel.getByLabel('Access token');
  await expect(field).toHaveValue('••••••••');
  await panel.getByRole('button', { name: 'Show token' }).click();
  await expect(field).toHaveValue(/^[A-Za-z0-9_-]{40,}$/);
  const token = await field.inputValue();

  expect((await call(null)).status()).toBe(401);
  const listed = await call(token);
  expect(listed.status()).toBe(200);
  const body = await listed.text();
  expect(body).toContain('run_checks');
  expect(body).toContain('propose_step');
  expect(body).not.toContain('approve_plan');

  // Approval from the agent's conversation is a separate switch, off by default.
  const approval = panel.getByLabel('Allow approving plans in the agent’s conversation');
  await expect(approval).not.toBeChecked();
  await approval.check();
  await expect(approval).toBeChecked();
  expect(await (await call(token)).text()).toContain('approve_plan');

  // Replacing the token refuses the old one.
  await panel.getByRole('button', { name: 'Replace token…' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Replace token', exact: true }).click();
  await expect(field).not.toHaveValue(token);
  expect((await call(token)).status()).toBe(401);

  await enabled.uncheck();
  await expect(field).toHaveCount(0);
  expect((await call(token)).status()).toBe(404);
});
