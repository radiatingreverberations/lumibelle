import { openShotSetup, closeShotSetup, toolsTab, assetView } from './workspace-tools.js';
import { scriptAssist, modelOptions, closeComposer, submitPlanning, submitExtraction } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';
const assist = page => page.locator('.ai-assist-dialog').filter({ has: page.getByRole('button', { name: 'Close', exact: true }) }).last();
const jobs = async (request, id) => (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id);
const selection = async (request, job) => (await (await request.get(`/fixtures/ai-jobs/${job.id}/text-selection`)).json());
async function fresh(page, request, route = 'script', shots = false) {
 const { id } = await (await request.get('/fixtures/new')).json();
 if (route !== 'script') { await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/images`); }
 if (shots) await request.post(`/fixtures/${id}/production-shot`);
 await page.goto(`/projects/${id}/${route}`); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
 return id;
}
async function model(page, scope, projectDefault = false) {
 await modelOptions(page, scope);
 const dialog = assist(page); await dialog.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
 await dialog.getByRole('button', { name: projectDefault ? 'Set as project default' : 'Done', exact: true }).click();
 await expect(dialog.getByRole('combobox', { name: 'Text model', exact: true })).not.toBeVisible();
 if (await assist(page).isVisible()) await closeComposer(page);
}
test('Script keeps a dedicated assistant with project defaults, request overrides and consecutive revisions', async ({ page, request }) => {
 const id = await fresh(page, request);
 await expect(page.locator('.studio-workspace[data-has-right]')).toHaveAttribute('data-has-right', 'true');
 await scriptAssist(page);
 const panel = page.locator('.script-assistant');
 await model(page, panel);
 await page.getByLabel('Instructions', { exact: true }).fill('Draft a short scene about a mouse making breakfast.');
 if (page.viewportSize().width < 1100) await page.getByRole('button', { name: 'Close Assistant', exact: true }).click();
 await scriptAssist(page);
 await expect(page.getByLabel('Instructions', { exact: true })).toHaveValue(/mouse/);
 await page.getByRole('button', { name: 'Draft script', exact: true }).click();
 await page.getByRole('button', { name: 'Apply changes', exact: true }).click();
 let run = (await jobs(request, id)).find(j => j.kind === 'ScriptAssistant');
 expect((await selection(request, run)).selectionSource).toBe('RequestOverride');
 await expect(panel.locator('.assist-composer-model')).toContainText('Global');
 for (const instruction of ['FIRST_CHANGE', 'SECOND_CHANGE']) {
   await page.locator('#script-scope').selectOption('Document');
   await page.getByLabel('Instructions', { exact: true }).fill(instruction);
   await page.getByRole('button', { name: 'Revise', exact: true }).click();
   await page.getByRole('button', { name: 'Apply changes', exact: true }).click();
 }
 await expect.poll(async () => { const state = await (await request.get(`/fixtures/${id}`)).json(); return state.script.blocks[1].spans.map(s => s.text).join(''); }).toContain('First detail. Second detail.');
 await model(page, panel, true);
 await page.screenshot({ path: 'artifacts/text-assist-script-desktop.png' });
 await page.goto(`/projects/${id}/settings`);
 await expect(page.getByRole('heading', { name: 'Text assistance', exact: true })).toBeVisible();
 await expect(page.locator('.text-model-picker').filter({ has: page.getByRole('button', { name: 'Text model options', exact: true }) }).first()).toContainText('Alternate mock model');
 expect((await (await request.get(`/fixtures/${id}/preferences`)).json()).textDefault.model).toBe('mock/alternate');
 await page.goto(`/projects/${id}/script`);
 await page.setViewportSize({ width: 390, height: 844 });
 await scriptAssist(page);
 await expect(panel.getByRole('button', { name: 'Revise', exact: true })).toBeEnabled();
 await expect(panel.getByRole('button', { name: 'Revise', exact: true })).toBeInViewport();
 await expect(panel.locator('.assist-composer-model')).toContainText('Alternate mock model');
 await page.screenshot({ path: 'artifacts/text-assist-script-narrow.png' });
 await page.keyboard.press('Escape');
 await expect(page.getByRole('button', { name: 'Show Assistant', exact: true })).toBeFocused();
 expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
 await page.screenshot({ path: 'artifacts/text-assist-script-narrow-editor.png' });
});

test('shot prompt composer leaves the editor clear and preserves direction through close and submit', async ({ page, request }) => {
 const id = await fresh(page, request, 'shots', true);
 await openShotSetup(page);
 await expect(page.getByLabel('Direction for AI', { exact: true })).toHaveCount(0);
 const pane = page.locator('#shot-setup-prompt-panel');
 await model(page, pane);
 await pane.getByRole('button', { name: 'Prompt assistance', exact: true }).click();
 await page.getByLabel('Direction for AI', { exact: true }).fill('Use a gentle camera move.');
 await assist(page).getByRole('button', { name: 'Close', exact: true }).click();
 await pane.getByRole('button', { name: 'Prompt assistance', exact: true }).click();
 await expect(page.getByLabel('Direction for AI', { exact: true })).toHaveValue('Use a gentle camera move.');
 await page.getByRole('button', { name: 'Compose prompt', exact: true }).click();
 await expect(page.getByRole('textbox', { name: 'H3 prompt', exact: true })).toContainText('Vision-composed staging');
 await expect(page.getByRole('button', { name: 'Apply changes', exact: true })).toHaveCount(0);
 const job = (await jobs(request, id)).find(j => j.kind === 'PromptComposition');
 expect((await selection(request, job)).model.model).toBe('mock/alternate');
 await expect(pane.getByRole('button', { name: 'Text model options', exact: true })).toHaveCount(0);
 await expect(pane.locator('.prompt-toolbar').getByRole('button', { name: 'Prompt assistance', exact: true })).toBeVisible();
 await page.screenshot({ path: 'artifacts/text-assist-shots-desktop.png' });
 await closeShotSetup(page);
 await page.getByRole('button', { name: 'Draft shots', exact: true }).click();
 await expect(assist(page).getByLabel('Directing instructions (optional)')).toBeVisible();
 await submitPlanning(page);
 await expect(page.getByRole('button', { name: 'Add reviewed shots', exact: true })).toBeEnabled();
});
test('assets extraction, prompt enhancement and guidance share the composer and keep their review flows', async ({ page, request }) => {
 const id = await fresh(page, request, 'assets');
 await page.getByRole('button', { name: 'Extract assets from script', exact: true }).click();
 await expect(assist(page).getByRole('button', { name: 'Find assets', exact: true })).toBeEnabled();
 await submitExtraction(page);
 await expect(page.locator('.extraction-dialog')).toBeVisible();
 // On a slow runner the first Close can arrive before the review settles; retry until it is gone.
 await expect(async () => {
  if (await page.locator('.extraction-dialog').isVisible()) await page.locator('.extraction-dialog').getByRole('button', { name: 'Close', exact: true }).last().click({ timeout: 2000 });
  await expect(page.locator('.extraction-dialog')).toBeHidden({ timeout: 2000 });
 }).toPass({ timeout: 20000 });
 await toolsTab(page, 'Prompt');
 await page.getByLabel('Image prompt', { exact: true }).fill('A mouse wearing a blue coat.');
 await page.getByRole('button', { name: 'Improve prompt', exact: true }).click();
 await assist(page).getByRole('button', { name: 'Enhance', exact: true }).click();
 await expect(page.locator('.prompt-enhancement-dialog')).toBeVisible();
 await page.locator('.prompt-enhancement-dialog').getByRole('button', { name: 'Apply changes', exact: true }).click();
 await assetView(page, 'Asset details');
 await page.locator('.asset-editor > .asset-preservation > summary').click();
 await page.locator('.asset-editor > .asset-preservation').getByRole('button', { name: 'Suggest guidance', exact: true }).click();
 await assist(page).getByRole('button', { name: 'Suggest', exact: true }).click();
 await page.locator('.asset-editor > .asset-preservation').getByRole('button', { name: 'Review changes', exact: true }).click();
 await expect(page.locator('.guidance-dialog')).toBeVisible();
 await page.locator('.guidance-dialog').getByRole('button', { name: 'Apply changes', exact: true }).click();
 const captured = await jobs(request, id);
 for (const kind of ['AssetExtraction', 'PromptEnhancement', 'Guidance']) expect(captured.some(j => j.kind === kind)).toBeTruthy();
});
