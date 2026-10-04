import { openShotSetup } from './workspace-tools.js';
import { test, expect } from './fixtures.js';
import { scriptAssist } from './text-assistance-tools.js';
import { chooseReelReferences, reelFraming } from './reel-tools.js';
const jobs = async (request, id) => (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.target.projectId === id);
const workflows = ['script', 'extraction', 'enhancement', 'guidance', 'planning', 'prompt', 'reel'];
for (const narrow of [false, true]) for (const workflow of workflows) {
 test(`${workflow} request control retains identity and timing (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`);
  const assets = await (await request.post(`/fixtures/${id}/images`)).json();
  const owner = assets.assets[0];
  if (workflow === 'prompt') await request.post(`/fixtures/${id}/production-shot`);
  const route = workflow === 'script' ? 'script' : ['planning', 'prompt'].includes(workflow) ? 'shots' : 'assets';
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/${route}${workflow === 'reel' ? `?assetId=${owner.id}&view=reels` : ''}`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  if (route === 'assets') await expect(page.locator('.asset-list-row.selected')).toHaveAttribute('data-asset-id', owner.id);
  await request.post('/fixtures/text-queue?paused=true');
  let control, composer, review;
  async function reveal(side) {
   const toggle = page.locator(`[data-toggle-pane=${side}]`);
   if (await toggle.getAttribute('aria-expanded') !== 'true') await toggle.click();
  }
  try {
   if (workflow === 'script') {
    await scriptAssist(page); await page.locator('#script-scope').selectOption('Document');
    await page.locator('#script-instructions').fill('FIRST_CHANGE');
    control = page.locator('.script-assistant .request-action-button');
    review = page.getByRole('dialog', { name: /Review script proposal/ });
    await control.click();
   } else {
    let start;
    if (workflow === 'extraction' || workflow === 'planning') {
     await reveal('left');
     start = page.getByRole('button', { name: workflow === 'extraction' ? 'Extract assets from script' : 'Draft shots', exact: true });
     control = page.locator(workflow === 'extraction' ? '.asset-library-extraction .request-action-button' : '.shots-library-tools .text-assistance .request-action-button').first();
     review = page.locator(workflow === 'extraction' ? '.extraction-dialog' : '.shot-planning-dialog');
    } else if (workflow === 'enhancement') {
     await reveal('right');
     await page.getByLabel('Image prompt', { exact: true }).fill('A clear portrait');
     start = page.getByRole('button', { name: 'Improve prompt', exact: true });
     control = page.locator('.prompt-enhancement .request-action-button'); review = page.locator('.prompt-enhancement-dialog');
    } else if (workflow === 'guidance') {
     await page.getByRole('button', { name: 'Edit asset details', exact: true }).click();
     await page.locator('.asset-details-dialog .asset-preservation > summary').click();
     control = page.locator('.asset-details-dialog .asset-preservation .request-action-button'); start = control; review = page.locator('.guidance-dialog');
    } else if (workflow === 'prompt') {
     await openShotSetup(page);
     control = page.locator('#shot-setup-prompt-panel .request-action-button'); start = control;
     review = page.locator('.ai-assist-dialog');
    } else {
     // Like the shot prompt, the reel request control is in the reel's Prompt dialog, beside its framing.
     const pictures = await chooseReelReferences(page, [`${owner.id}/${owner.images[0].id}`]);
     await pictures.getByRole('button', { name: 'Apply changes', exact: true }).click();
     await expect(pictures).toBeHidden();
     await (await reelFraming(page)).getByLabel('Framing preset', { exact: true }).selectOption('Custom');
     control = page.locator('#reel-setup-prompt-panel .request-action-button'); start = control; review = page.locator('.reel-review-dialog');
    }
    await start.click(); composer = page.locator('.ai-assist-dialog').last();
    if (workflow === 'reel' || workflow === 'prompt') {
     await composer.locator('.model-chip').click();
     await page.getByRole('combobox', { name: 'Text model', exact: true }).selectOption({ label: 'OpenRouter · Alternate mock model' });
     await page.getByRole('dialog').filter({ has: page.getByRole('combobox', { name: 'Text model', exact: true }) }).getByRole('button', { name: 'Done', exact: true }).click();
    }
    const action = { extraction: 'Find assets', planning: 'Draft shots', enhancement: 'Enhance', guidance: 'Suggest', prompt: 'Compose prompt', reel: 'Compose pair' }[workflow];
    await composer.getByRole('button', { name: action, exact: true }).click();
    await expect(composer).toBeHidden();
   }
   await expect(control).toContainText('Queued');
   await expect(control.locator('.request-time')).toContainText('waiting');
   await expect(control).toBeEnabled(); await expect(control).toBeInViewport();
   const captured = await jobs(request, id);
   expect(captured).toHaveLength(1);
   await control.focus(); await page.keyboard.press('Enter');
   await expect(review).toBeVisible(); await expect(review).toContainText('Queued in Lumibelle');
   const close = review.getByRole('button', { name: 'Close', exact: true }).last();
   await close.click(); await expect(review).toBeHidden();
   if (narrow) {
    if (['script', 'enhancement'].includes(workflow)) await reveal('right');
    else if (['extraction', 'planning'].includes(workflow)) await reveal('left');
   }
   await expect(control).toBeInViewport();
   expect((await jobs(request, id)).map(j => j.id)).toEqual(captured.map(j => j.id));
   if (workflow === 'planning' || workflow === 'reel') await page.screenshot({ path: `artifacts/compact-${workflow}-${narrow ? 'narrow' : 'desktop'}.png` });
   const menu = control.locator('..').locator('.request-action-menu');
   await menu.locator('summary').click(); await menu.getByRole('button', { name: 'Cancel request', exact: true }).click();
   await expect.poll(async () => (await jobs(request, id))[0].state).toBe('Cancelled');
   await expect(control).not.toContainText('Queued'); await expect(control).toBeEnabled();
   await expect(control.locator('.request-time')).toHaveCount(0);
   expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  } finally { await request.post('/fixtures/text-queue?paused=false'); }
 });
}
