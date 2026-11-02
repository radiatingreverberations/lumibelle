import { test, expect } from './fixtures.js';
const state = async (request, id) => (await (await request.get(`/fixtures/${id}`)).json()).assets;
async function tools(page) { const toggle=page.locator('[data-toggle-pane=right]'); if(await toggle.getAttribute('aria-expanded')!=='true') await toggle.click(); }
async function gallery(page) {
 await expect(page.locator('.mud-dialog:visible')).toHaveCount(0);
 await expect(page.locator('.studio-workspace')).toHaveAttribute('data-suspended','false');
 const close=page.locator('.workspace-right .workspace-drawer-heading [data-close-pane]');
 if(await close.isVisible()) { await close.click(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-right-visible','false'); }
}
for (const narrow of [false,true]) test(`compact cards preview thumbnails, select bodies and organize looks (${narrow?'narrow':'desktop'})`,async({page,request})=>{
 test.setTimeout(90000);
 const {id}=await (await request.get('/fixtures/new')).json();
 for(const step of ['images?patterned=true','approved','reference-setups','production-shot']) expect((await request.post(`/fixtures/${id}/${step}`)).ok()).toBe(true);
 const take=await (await request.post(`/fixtures/${id}/reference-video-take`)).json();
 expect((await request.post(`/fixtures/${id}/reference-reels?takeId=${take.id}`)).ok()).toBe(true);
 const initial=await state(request,id), owner=initial.assets[0], source=owner.images[0], reel=initial.reels[0], other=initial.reels[1], look=owner.looks[0];
 await page.setViewportSize({width:narrow?390:1173,height:narrow?844:1000}); await page.goto(`/projects/${id}/assets`);
 await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true');
 const card=id=>page.locator(`[data-media-id="${id}"]`);
 const lookDialog=page.getByRole('dialog').filter({has:page.getByRole('heading',{name:'Change look',exact:true})});
 async function openLook(item) {
  await card(item.id).getByRole('button',{name:item.media?'Actions for '+item.name:'Image actions',exact:true}).click();
  await page.getByRole('menuitem',{name:'Change look…',exact:true}).click();
  await expect(lookDialog).toBeVisible();
 }
 async function changeLook(item,value) {
  await openLook(item); await lookDialog.getByLabel('Look',{exact:true}).selectOption(value);
  await lookDialog.getByRole('button',{name:'Apply changes',exact:true}).click();
 }
 await expect(page.locator('.asset-media-card select')).toHaveCount(0);
 await expect(page.locator('.asset-media-card .cover-badge')).toHaveCount(0);
 await expect(card(source.id).locator('.media-look-badge')).toHaveText('General');
 // Clicking the card title reaches the body selection button.
 const body=card(source.id).locator('.reference-card-body'); await body.scrollIntoViewIfNeeded();
 const title=await body.locator('.media-name').boundingBox(); await page.mouse.click(title.x+title.width/2,title.y+title.height/2);
 await expect(page.getByRole('heading',{name:'Edit image',exact:true})).toBeVisible();
 await page.locator('#image-prompt').fill('Keep this authored edit instruction'); await gallery(page);
 await card(reel.id).locator('.media-preview').click();
 const preview=page.getByRole('dialog'); await expect(preview.locator('video')).toBeVisible();
 await expect(card(source.id)).toHaveClass(/selected/);
 await preview.getByRole('button',{name:'Close',exact:true}).click();
 await expect(card(reel.id).locator('.media-preview')).toBeFocused();
 await card(source.id).locator('.media-preview').click();
 await page.getByRole('button',{name:'Close image review',exact:true}).click();
 await expect(card(source.id)).toHaveClass(/selected/);
 // Menu and look controls never select their card or replace the edit prompt.
 await card(other.id).getByRole('button',{name:`Actions for ${other.name}`,exact:true}).click();
 await expect(page.getByRole('menuitem',{name:'Move to Trash',exact:true})).toBeVisible(); await card(other.id).getByRole('button',{name:`Actions for ${other.name}`,exact:true}).click();
 await expect(card(source.id)).toHaveClass(/selected/);
 await openLook(source); await lookDialog.getByLabel('Look',{exact:true}).selectOption(look.id);
 await lookDialog.getByRole('button',{name:'Cancel',exact:true}).click();
 expect((await state(request,id)).assets[0].images.find(i=>i.id===source.id).lookId).toBeNull();
 await changeLook(source,look.id); await expect(lookDialog).not.toBeVisible();
 await expect.poll(async()=>(await state(request,id)).assets[0].images.find(i=>i.id===source.id).lookId).toBe(look.id);
 await expect(card(source.id).locator('.media-look-badge')).toHaveText(look.name);
 await tools(page); await expect(page.locator('#image-prompt')).toHaveValue('Keep this authored edit instruction');
 await gallery(page);
 // A revision conflict is visible and the same quick action can be retried.
 await request.post(`/fixtures/${id}/touch-assets`);
 await changeLook(other,look.id);
 await expect(page.getByRole('alert').filter({hasText:'library changed while saving'})).toBeVisible();
 await expect(lookDialog.getByLabel('Look',{exact:true})).toHaveValue(look.id);
 await lookDialog.getByRole('button',{name:'Apply changes',exact:true}).click();
 await expect(lookDialog).not.toBeVisible();
 await expect.poll(async()=>(await state(request,id)).reels.find(r=>r.id===other.id).lookId).toBe(look.id);
 await expect(card(source.id)).toHaveClass(/selected/);
 // Keyboard selection keeps follow-up tools separate from metadata editing.
 await card(reel.id).locator('.media-select').focus(); await page.keyboard.press('Enter');
 const details=page.locator('.reel-details-tools'); await expect(details).toBeVisible();
 await details.getByRole('button',{name:'Edit details',exact:true}).click();
 const modal=page.locator('.reel-details-dialog');
 await modal.locator('summary').getByText('Use guidance',{exact:true}).click();
 await modal.getByLabel('Use guidance',{exact:true}).fill('Keep the reviewed rear silhouette.');
 await modal.getByRole('button',{name:'Save details',exact:true}).click(); await expect(modal).not.toBeVisible(); await gallery(page);
 await changeLook(reel,look.id); await expect(lookDialog).not.toBeVisible();
 await expect.poll(async()=>{const r=(await state(request,id)).reels.find(r=>r.id===reel.id); return [r.lookId,r.useGuidance];}).toEqual([look.id,'Keep the reviewed rear silhouette.']);
 await expect(card(reel.id).locator('.media-look-badge')).toHaveText(look.name);
 await tools(page); await details.getByRole('button',{name:'Edit details…',exact:true}).click(); await expect(modal.getByRole('combobox',{name:'Look',exact:true})).toHaveValue(look.id); await modal.getByRole('button',{name:'Close',exact:true}).click();
 await gallery(page); await page.getByLabel('Gallery look',{exact:true}).selectOption(look.id);
 await changeLook(reel,''); await expect(lookDialog).not.toBeVisible(); await expect(card(reel.id)).toHaveCount(0);
 await expect(page.getByLabel('Gallery look',{exact:true})).toBeFocused();
 await tools(page); await expect(details).toBeVisible(); await page.getByRole('button',{name:'Reveal in gallery'}).click(); await gallery(page);
 await expect(card(reel.id)).toHaveClass(/selected/);
 await card(reel.id).scrollIntoViewIfNeeded(); await page.screenshot({path:`artifacts/assets-compact-cards-${narrow?'narrow':'desktop'}.png`});
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
 // Selecting the same card again clears it, including keyboard Space activation.
 await card(reel.id).locator('.media-select').focus(); await page.keyboard.press('Space'); await tools(page);
 await expect(page.getByRole('combobox',{name:'Create media type',exact:true})).toBeVisible();
 await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true');
 await openLook(source); await expect(lookDialog.getByLabel('Look',{exact:true})).toHaveValue(look.id);
 await lookDialog.getByRole('button',{name:'Cancel',exact:true}).click();
 const saved=await state(request,id); expect(saved.reels.find(r=>r.id===reel.id).media).toEqual(reel.media);
 expect(saved.reels.find(r=>r.id===reel.id).generation).toEqual(reel.generation);
});
