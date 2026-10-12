import { test, expect } from './fixtures.js';
import { toolsTab, assetView, imageAction, addImageReference, editShotReference } from './workspace-tools.js';

async function setup(page, request, environment = false) {
  const project = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${project.id}/images?environment=${environment}`)).json();
  await page.goto(`/projects/${project.id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  return { project, library, state: async () => (await (await request.get(`/fixtures/${project.id}`)).json()).assets };
}

test('Assets ignores obsolete nested tabs and retains details and reachable tools at every size', async ({page,request}) => {
  await page.addInitScript(()=>localStorage.setItem('lumibelle.workspace.Assets.v1',JSON.stringify({left:200,right:340,tab:'Settings'})));
  await page.setViewportSize({width:1173,height:1272});
  await setup(page,request,true);
  await expect(page.getByRole('heading',{name:'Create image',exact:true})).toBeVisible();
  for(const label of ['Images','Asset details','Voices','Prompt','Inputs','Output']) await expect(page.getByRole('tab',{name:label,exact:true})).toHaveCount(0);
  await assetView(page,'Asset details');
  await page.getByLabel('Character identity notes').fill('Keep these local edits while switching views.');
  await assetView(page,'Voices'); await assetView(page,'Images');
  await assetView(page,'Asset details');
  await expect(page.getByLabel('Character identity notes')).toHaveValue('Keep these local edits while switching views.');
  await assetView(page,'Images');
  for(const [width,height] of [[1280,720],[820,1000],[587,636],[390,844]]) {
    await page.setViewportSize({width,height}); await toolsTab(page,'Prompt');
    await expect(page.getByRole('button',{name:'Generate images',exact:true})).toBeInViewport();
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
    await assetView(page,'Images'); await page.screenshot({path:`test-results/overhaul-assets-${width}.png`});
  }
});

test('large visual picker stays bounded, preserves click order and enforces nine shot images', async ({ page, request }) => {
  test.setTimeout(90000);
  const { project } = await setup(page, request);
  await request.post(`/fixtures/${project.id}/approved`);
  const library = await (await request.post(`/fixtures/${project.id}/ux-images`)).json();
  await request.post(`/fixtures/${project.id}/reference-workspace?silent=true`);
  await page.goto(`/projects/${project.id}/shots`); await toolsTab(page, 'References');
  await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
  const picker = page.locator('.project-image-picker');
  await expect(picker.locator('.picker-grid > button')).toHaveCount(48);
  await picker.getByRole('button', { name: 'Show more', exact: true }).click();
  await expect(picker.locator('.picker-grid > button')).toHaveCount(96);
  const choices = await picker.locator('.picker-grid > button:not([disabled])').evaluateAll(nodes => nodes.map(node => node.dataset.reference));
  const ordered = [];
  for (const i of [4,2,0,6,1,3,5,7,8]) {
    const choice = picker.locator(`[data-reference="${choices[i]}"]`);
    ordered.push(choices[i].split('/')[1]);
    await choice.click();
    await expect(choice).toBeDisabled();
  }
  await expect(picker.locator('.picker-grid > button[aria-pressed=false]:not([disabled])')).toHaveCount(0);
  await page.getByRole('button', { name: 'Apply changes' }).click();
  await expect.poll(async () => (await (await request.get(`/fixtures/${project.id}/production`)).json()).compositions[0].inputs.images.map(b => b.mediaId)).toEqual(ordered);
  await expect(page.locator('.compact-reference')).toHaveCount(9);
  await expect(page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true })).toBeEnabled();
  await page.setViewportSize({ width: 390, height: 844 }); await toolsTab(page, 'References');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

for (const narrow of [false, true]) test(`editable image details guard leaving, save explicitly and preserve comparison through arrivals (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  await page.setViewportSize({ width: narrow ? 390 : 1440, height: narrow ? 844 : 950 });
  const { library, state } = await setup(page, request);
  await page.locator('.asset-media-card[data-media-kind=Image] .media-select').click();
  await expect(page.getByLabel('Edit instruction')).toBeVisible();
  await toolsTab(page, 'Prompt');
  await page.getByLabel('Image workflow', { exact: true }).selectOption('Flux2Klein9bKv');
  await page.getByLabel('Edit instruction').fill('Change the coat');
  await toolsTab(page, 'Inputs');
  await addImageReference(page, `${library.assets[1].id}/${library.assets[1].images[0].id}`);
  await page.getByRole('button', { name: 'Generate edited images', exact: true }).click();
  const review = page.locator('.image-review-dialog');
  await expect(review.getByRole('button', { name: 'View Take 1', exact: true })).toBeVisible();
  const editor = review.locator('.review-metadata-editor');
  await expect(editor).toBeVisible();
  await editor.getByLabel('Name', { exact: true }).fill('My chosen frame');
  await review.getByRole('button', { name: 'Compare with Source', exact: true }).click();
  await review.getByRole('button', { name: 'Side by side', exact: true }).click();
  await review.getByRole('button', { name: 'Generate more…', exact: true }).click();
  await page.getByRole('button', { name: 'Queue 1 image', exact: true }).click();
  await expect(review.getByRole('button', { name: 'View Take 2', exact: true })).toBeVisible();
  await expect(editor.getByLabel('Name', { exact: true })).toHaveValue('My chosen frame');
  expect((await state()).assets[0].images.some(i => i.name === 'My chosen frame')).toBe(false);
  await review.getByRole('button', { name: 'View Source', exact: true }).click();
  await expect(review.locator('.review-dirty-prompt')).toBeVisible();
  await review.getByRole('button', { name: 'Keep editing', exact: true }).click();
  await expect(review.getByRole('button', { name: 'Compare with Source', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await page.locator('.image-review-dialog').getByRole('button', { name: 'Save details', exact: true }).click();
  await expect(editor).toBeVisible();
  await expect(review.getByRole('button', { name: 'Save details', exact: true })).toBeDisabled();
  const saved = (await state()).assets[0].images.find(i => i.name === 'My chosen frame');
  expect(saved.generation.prompt).toBe('Change the coat');
  await expect(editor.getByLabel('Name', { exact: true })).toHaveValue('My chosen frame');
  await review.getByRole('button', { name: 'View Source', exact: true }).click();
  await expect(editor.getByLabel('Name', { exact: true })).toHaveValue(library.assets[0].images[0].name ?? '');
  await review.getByRole('button', { name: 'View Take 1', exact: true }).click();
  await expect(editor.getByLabel('Name', { exact: true })).toHaveValue('My chosen frame');
  await editor.getByLabel('Name', { exact: true }).fill('Do not save');
  await page.keyboard.press('Escape');
  await expect(review.locator('.review-dirty-prompt')).toBeVisible();
  await review.getByRole('button', { name: 'Discard changes', exact: true }).click();
  await expect(review).not.toBeVisible();
  expect((await state()).assets[0].images.some(i => i.name === 'Do not save')).toBe(false);
});

test('reference use hints save with the setup and Undo restores the previous hint', async ({ page, request }) => {
  const { project, library } = await setup(page, request);
  await request.post(`/fixtures/${project.id}/approved`);
  await request.post(`/fixtures/${project.id}/production-shot`);
  await page.goto(`/projects/${project.id}/shots`);
  await toolsTab(page, 'References');
  await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
  const dialog = page.locator('.shot-reference-dialog');
  await dialog.locator(`[data-reference="${library.assets[0].id}/${library.assets[0].images[0].id}"]`).click();
  await dialog.getByLabel('Voice for Juniper', { exact: true }).selectOption('none');
  await dialog.getByRole('button', { name: 'Apply changes', exact: true }).click();
  const savedSetup = async () => (await (await request.get(`/fixtures/${project.id}/production`)).json()).compositions[0];
  await expect.poll(async () => (await savedSetup()).inputs.images.length).toBe(1);
  const original = (await savedSetup()).inputs.images[0];
  await editShotReference(page);
  await dialog.getByLabel('AI use hint', { exact: true }).selectOption('Identity');
  await dialog.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect.poll(async () => (await savedSetup()).inputs.images[0].aiUseHint).toBe('Identity');
  expect((await savedSetup()).inputs.images[0].id).toBe(original.id);
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect.poll(async () => (await savedSetup()).inputs.images[0].aiUseHint).toBe(original.aiUseHint);
});
