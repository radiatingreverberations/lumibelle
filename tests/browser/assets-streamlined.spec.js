import { test, expect } from './fixtures.js';
const state = async (request, id) => (await (await request.get(`/fixtures/${id}`)).json()).assets;
async function tools(page) { const toggle = page.locator('[data-toggle-pane=right]'); if (await toggle.getAttribute('aria-expanded') !== 'true') await toggle.click(); }
async function gallery(page) {
 await expect(page.locator('.mud-dialog:visible')).toHaveCount(0);
 await expect(page.locator('.studio-workspace')).toHaveAttribute('data-suspended','false');
 const close=page.locator('.workspace-right .workspace-drawer-heading [data-close-pane]');
 if(await close.isVisible()) { await close.click(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-right-visible','false'); }
}
for (const narrow of [false, true]) test(`asset details modal keeps the gallery compact and saves notes and looks (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const library = await (await request.post(`/fixtures/${id}/images`)).json();
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await tools(page); await page.locator('#image-prompt').fill('Keep my generation draft'); await gallery(page);
  const open = page.getByRole('button', { name: 'Edit asset details', exact: true });
  const editor = page.locator('.asset-details-dialog');
  await expect(page.locator('.asset-pinned-details')).toHaveCount(0);
  await open.focus(); await page.keyboard.press('Enter');
  await expect(editor).toBeVisible();
  await expect(editor.getByLabel('Asset name', { exact: true })).toBeFocused();
  await editor.getByLabel('Character identity notes').fill('Clear notes beneath the character name.');
  await editor.getByRole('button', { name: 'Add look', exact: true }).click();
  const look = page.locator('.look-editor-dialog');
  await look.getByLabel('Look name', { exact: true }).fill('Raincoat');
  await look.getByLabel('Look description', { exact: true }).fill('A yellow raincoat.');
  await look.getByRole('button', { name: 'Save look', exact: true }).click();
  await expect(look).not.toBeVisible(); await expect(editor).toBeVisible();
  await expect.poll(async () => (await state(request, id)).assets[0].looks.some(l => l.name === 'Raincoat')).toBe(true);
  await editor.getByLabel('Asset name', { exact: true }).fill('');
  await editor.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(editor).toBeVisible(); await expect(editor.getByRole('alert')).toContainText('Every asset needs a name');
  await editor.getByLabel('Asset name', { exact: true }).fill(library.assets[0].name);
  await expect(editor.getByRole('button', { name: 'Close', exact: true })).toBeInViewport();
  await page.screenshot({ path: `artifacts/asset-details-${narrow ? 'narrow' : 'desktop'}.png` });
  await editor.getByLabel('Asset name', { exact: true }).focus(); await page.keyboard.press('Escape');
  await expect(editor).not.toBeVisible(); await expect(open).toBeFocused();
  await expect(page.locator('.asset-description-preview')).toHaveText('Clear notes beneath the character name.');
  await tools(page); await expect(page.locator('#image-prompt')).toHaveValue('Keep my generation draft'); await gallery(page);
  await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expect(editor).not.toBeVisible(); await open.click();
  await expect(editor.getByLabel('Character identity notes')).toHaveValue('Clear notes beneath the character name.');
  await expect(editor.getByLabel('Look', { exact: true }).locator('option')).toContainText(['All images', 'General / unassigned', 'Raincoat']);
});

for (const narrow of [false, true]) test(`streamlined gallery keeps drafts and separates preview, filter and input edits (${narrow ? 'narrow' : 'desktop'})`, async ({ page, request }) => {
  test.setTimeout(90000);
  const { id } = await (await request.get('/fixtures/new')).json();
  const lib = await (await request.post(`/fixtures/${id}/images?patterned=true`)).json();
  const owner = lib.assets[0], source = owner.images[0];
  await page.setViewportSize({ width: narrow ? 390 : 1173, height: narrow ? 844 : 1000 });
  await page.goto(`/projects/${id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await expect(page.getByRole('tab', { name: 'Inputs', exact: true })).toHaveCount(0);
  await expect(page.locator('.asset-media-card input[type=checkbox]')).toHaveCount(0);
  await tools(page);
  const prompt = page.locator('#image-prompt'); await prompt.fill('Creation draft stays here');
  await gallery(page);
  const card = page.locator(`[data-media-id="${source.id}"]`);
  await card.getByRole('button', { name: /^Preview image/ }).click();
  await expect(page.locator('.image-review-dialog')).toBeVisible();
  await page.getByRole('button', { name: 'Close image review', exact: true }).click();
  await card.locator('.media-select').click(); await expect(page.locator('.workspace-right')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Edit image', exact: true })).toBeVisible();
  // One compact context line: the image, then its asset (unless named the same) and look.
  const context = page.locator('.asset-tools-header .selection-context');
  const shown = source.name ?? (source.tags?.length ? source.tags.join(', ') : owner.name);
  await expect(context.locator('strong')).toHaveText(shown);
  await expect(context.locator('span')).toHaveText(shown === owner.name ? 'General' : `${owner.name} · General`);
  await expect(page.locator('.asset-tools-header .asset-tool-context')).toHaveCount(1);
  await expect(prompt).toHaveValue(''); await prompt.fill('Keep this edit instruction');
  await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
  const manager = page.locator('.image-input-manager-dialog');
  await manager.locator(`[data-reference="${lib.assets[1].id}/${lib.assets[1].images[0].id}"]`).click();
  await manager.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.locator('.compact-image-input')).toHaveCount(1);
  await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
  await manager.locator(`[data-reference="${lib.assets[1].id}/${lib.assets[1].images[0].id}"]`).click();
  await manager.getByRole('button', { name: 'Apply changes', exact: true }).click();
  await expect(page.locator('.compact-image-input')).toHaveCount(2);
  await page.getByRole('button', { name: 'Clear selection', exact: true }).press('Enter'); await expect(page.getByRole('combobox',{name:'Create media type',exact:true})).toBeFocused(); await expect(prompt).toHaveValue('Creation draft stays here');
  await gallery(page); await card.locator('.media-select').click(); await expect(page.locator('.workspace-right')).toBeVisible();
  await expect(prompt).toHaveValue('Keep this edit instruction'); await expect(page.locator('.compact-image-input')).toHaveCount(2);
  await gallery(page); await page.getByRole('searchbox', { name: 'Search references' }).fill('no matches');
  await tools(page); await expect(prompt).toHaveValue('Keep this edit instruction');
  await page.getByRole('button', { name: 'Reveal in gallery' }).click(); await gallery(page); await expect(card).toBeVisible();
  await page.screenshot({ path: `artifacts/assets-streamlined-${narrow ? 'narrow' : 'desktop'}.png` });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await tools(page); await page.getByRole('button', { name: 'Clear selection', exact: true }).click();
  await prompt.fill('SLOW: A new blue reference.'); await page.getByRole('button', { name: 'Generate images', exact: true }).click();
  await expect(page.locator('.image-review-dialog')).toBeVisible();
  await page.getByRole('button', { name: 'Close image review', exact: true }).click();
  await expect.poll(async () => (await state(request,id)).assets[0].images.length).toBe(2);
  const result = (await state(request,id)).assets[0].images.find(i => i.id !== source.id);
  expect(result.lookId).toBeNull();
  await gallery(page); await expect(page.locator('.asset-media-card').first()).toHaveAttribute('data-media-id', result.id);
});

test('asset Move to and Undo persist full order through a filtered list', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json(); const lib = await (await request.post(`/fixtures/${id}/images`)).json();
  await page.setViewportSize({width:1173,height:1000}); await page.goto(`/projects/${id}/assets`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true');
  const first = lib.assets[0], second = lib.assets[1];
  await page.getByLabel('Search assets',{exact:true}).fill(first.name);
  const move = page.getByRole('button',{name:`Move ${first.name}`,exact:true});
  await expect(move).toBeEnabled();
  await move.focus(); await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog',{name:'Move asset'});
  await dialog.getByLabel('Position').selectOption('after'); await dialog.getByLabel('Destination asset').selectOption(second.id);
  await dialog.getByRole('button',{name:'Move',exact:true}).click();
  await expect.poll(async()=> (await state(request,id)).assets.map(a=>a.id)).toEqual([second.id,first.id]);
  await page.locator('.asset-library').getByRole('button',{name:'Undo',exact:true}).click();
  await expect.poll(async()=> (await state(request,id)).assets.map(a=>a.id)).toEqual([first.id,second.id]);
  await page.reload(); await expect(page.locator('.asset-list-row').first()).toHaveAttribute('data-asset-id',first.id);
});

for (const narrow of [false, true]) test(`mixed references keep selection independent and save before switching (${narrow?'narrow':'desktop'})`, async ({page,request}) => {
  test.setTimeout(90000);
  const {id}=await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images?patterned=true`);
  await request.post(`/fixtures/${id}/reference-workspace`);
  await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`);
  const take=await (await request.post(`/fixtures/${id}/reference-video-take`)).json();
  expect((await request.post(`/fixtures/${id}/reference-reels?takeId=${take.id}`)).ok()).toBe(true);
  const lib=await state(request,id), owner=lib.assets[0], voice=lib.voices[0], reel=lib.reels[0];
  await page.setViewportSize({width:narrow?390:1173,height:narrow?844:1000});
  await page.goto(`/projects/${id}/assets`); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true');
  await expect(page.locator('.asset-media-card')).toHaveCount(4);
  await expect(page.locator('.asset-media-gallery video, .asset-media-gallery audio')).toHaveCount(0);
  const thumbnails = await page.locator('.asset-media-card .media-preview').evaluateAll(items => items.map(i => i.getBoundingClientRect().height));
  expect(Math.max(...thumbnails) - Math.min(...thumbnails)).toBeLessThan(1);
  for (const thumbnail of await page.locator('.asset-media-card .media-preview img').all()) await expect(thumbnail).toHaveCSS('object-fit', 'contain');
  await expect(page.locator(`[data-media-id="${voice.id}"] .media-preview`)).toHaveAccessibleDescription(/Voice.*excerpt/);
  await expect(page.locator(`[data-media-id="${reel.id}"] .media-preview`)).toHaveAccessibleDescription(/Reel.* s/);
  const card=key=>page.locator(`[data-media-id="${key}"] .media-select`);
  await card(voice.id).click(); await page.locator('.voice-details-tools').getByRole('button',{name:'Edit details',exact:true}).click(); const editor=page.locator('.voice-dialog'); await expect(editor).toBeVisible();
  await editor.getByLabel('Name', { exact: true }).fill('My revised voice');
  await editor.getByLabel('End (seconds)').fill('0');
  await expect(editor.getByRole('button',{name:'Save details',exact:true})).toBeDisabled();
  await editor.getByRole('button',{name:'Close',exact:true}).click(); await expect(editor.getByRole('alert')).toContainText('Save your voice');
  await editor.getByRole('button',{name:'Keep editing',exact:true}).click();
  await editor.getByLabel('End (seconds)').fill('2');
  await request.post(`/fixtures/${id}/touch-assets`);
  await editor.getByRole('button',{name:'Save details',exact:true}).click();
  await expect(editor.getByRole('alert')).toContainText('library changed');
  await expect(editor.getByLabel('Name', { exact: true })).toHaveValue('My revised voice');
  await editor.getByRole('button',{name:'Save details',exact:true}).click();
  await expect.poll(async()=>(await state(request,id)).voices[0].name).toBe('My revised voice');
  await expect(editor).not.toBeVisible(); await page.locator('.voice-details-tools').getByRole('button',{name:'Edit details',exact:true}).click();
  const audio=editor.locator('audio'); await expect.poll(()=>audio.evaluate(a=>a.readyState)).toBeGreaterThan(0);
  await editor.getByRole('button',{name:'Play excerpt',exact:true}).click(); await expect.poll(()=>audio.evaluate(a=>a.currentTime)).toBeGreaterThan(0);
  await editor.getByRole('button',{name:'Close',exact:true}).click();
  await gallery(page); await card(reel.id).click(); await page.locator('.reel-details-tools').getByRole('button',{name:'Edit details',exact:true}).click(); const details=page.locator('.reel-details-dialog'); await expect(details).toBeVisible();
  await details.getByLabel('Name',{exact:true}).fill('My reviewed reel');
  await details.locator('summary').getByText('Use guidance',{exact:true}).click();
  await details.getByLabel('Use guidance',{exact:true}).fill('My retained guidance');
  const save=details.getByRole('button',{name:'Save details',exact:true}); const saveBox=await save.boundingBox(); expect(saveBox.y+saveBox.height).toBeLessThanOrEqual(page.viewportSize().height);
  await details.getByRole('button',{name:'Close',exact:true}).click(); await expect(details.getByRole('alert')).toContainText('Save your reel'); await details.getByRole('button',{name:'Keep editing',exact:true}).click();
  await page.screenshot({path:`artifacts/media-details-${narrow?'narrow':'desktop'}.png`});
  await save.click(); await expect(details).not.toBeVisible();
  await gallery(page); await card(owner.images[0].id).click(); await expect(page.locator('.image-prompt-fields')).toBeVisible();
  await expect.poll(async()=>(await state(request,id)).reels.find(r=>r.id===reel.id).useGuidance).toBe('My retained guidance');
  await page.locator('#image-prompt').fill('Image edit remains selected'); await gallery(page);
  await page.getByLabel('Filter media',{exact:true}).selectOption('Voices');
  await expect(page.locator('.asset-media-card')).toHaveCount(1); await tools(page);
  await expect(page.locator('#image-prompt')).toHaveValue('Image edit remains selected');
  await page.getByRole('button',{name:'Reveal in gallery'}).click(); await gallery(page);
  await page.getByRole('button',{name:'Select multiple',exact:true}).click();
  await page.getByRole('checkbox',{name:'Select image',exact:true}).check();
  await tools(page); await expect(page.locator('#image-prompt')).toHaveValue('Image edit remains selected');
  await gallery(page); await page.screenshot({path:`artifacts/assets-mixed-${narrow?'narrow':'desktop'}.png`});
  await page.reload(); await expect(page.locator('[data-media-kind=Voice]')).toContainText('My revised voice');
  await expect(page.locator(`[data-media-id="${reel.id}"]`)).toContainText('My reviewed reel');
});

test('asset drag shows an insertion marker and persists the new order',async({page,request})=>{
 const {id}=await (await request.get('/fixtures/new')).json(); const lib=await (await request.post(`/fixtures/${id}/images`)).json();
 await page.setViewportSize({width:1173,height:1000});await page.goto(`/projects/${id}/assets`);await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true');
 const first=page.locator('.asset-list-row').first(), second=page.locator('.asset-list-row').nth(1);
 await expect(first.locator('.asset-drag')).toBeEnabled();
 const handle=await first.locator('.asset-drag').boundingBox(), target=await second.boundingBox();
 await page.mouse.move(handle.x+handle.width/2,handle.y+handle.height/2); await page.mouse.down();
 await page.mouse.move(target.x+target.width/2,target.y+target.height-5,{steps:10});
 await expect(second).toHaveClass(/asset-drop-after/);await page.mouse.up();
 await expect.poll(async()=>(await state(request,id)).assets.map(a=>a.id)).toEqual(lib.assets.map(a=>a.id).reverse());
 await expect(page.getByRole('dialog',{name:'Move asset'})).not.toBeVisible();
});

test('conflicting moves retain the draft until the author reviews the latest order',async({page,context,request})=>{
 const {id}=await (await request.get('/fixtures/new')).json(); const lib=await (await request.post(`/fixtures/${id}/images`)).json();
 const other=await context.newPage();
 try {
  for(const p of [page,other]) { await p.setViewportSize({width:1173,height:1000}); await p.goto(`/projects/${id}/assets`);await expect(p.locator('.studio-workspace')).toHaveAttribute('data-ready','true'); }
  async function prepareMove(p){await p.getByRole('button',{name:`Move ${lib.assets[0].name}`,exact:true}).click();const d=p.getByRole('dialog',{name:'Move asset'});await d.getByLabel('Position').selectOption('after');return d;}
  const stale=await prepareMove(other),first=await prepareMove(page);
  await first.getByRole('button',{name:'Move',exact:true}).click();
  await expect.poll(async()=>(await state(request,id)).assets[0].id).toBe(lib.assets[1].id);
  await stale.getByRole('button',{name:'Move',exact:true}).click();await expect(stale.getByRole('alert')).toContainText('another editor');
  await stale.getByRole('button',{name:'Review latest order',exact:true}).click();
  await stale.getByLabel('Position').selectOption('before');await stale.getByRole('button',{name:'Move',exact:true}).click();
  await expect.poll(async()=>(await state(request,id)).assets[0].id).toBe(lib.assets[0].id);
 } finally {await other.close();}
});
