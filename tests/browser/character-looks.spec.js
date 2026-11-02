import { test, expect } from './fixtures.js';
import { toolsTab, assetView, imageAction } from './workspace-tools.js';
const state=async(request,id)=>(await (await request.get(`/fixtures/${id}`)).json()).assets;
async function fixture(page,request) {
 const {id}=await (await request.get('/fixtures/new')).json(); await request.post(`/fixtures/${id}/images`);
 await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/reference-setups`);
 const library=await state(request,id); await page.goto(`/projects/${id}/assets`);
 await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true'); return {id,owner:library.assets[0]};
}
async function lookAction(page,id,action) {
 await assetView(page,'Asset details'); await page.getByLabel('Look',{exact:true}).selectOption(id);
 await page.getByRole('button',{name:'Look actions',exact:true}).click();
 await page.getByRole('menuitem',{name:action,exact:true}).click();
}
for(const narrow of [false,true]) test(`look filters and organization preserve drafts and unassigned image edits (${narrow?'narrow':'desktop'})`,async({page,request})=>{
 test.setTimeout(90000); await page.setViewportSize({width:narrow?390:1173,height:narrow?844:1000});
 const {id,owner}=await fixture(page,request); const look=owner.looks[0], source=owner.images.find(i=>i.lookId===look.id);
 await toolsTab(page,'Prompt'); await page.locator('#image-prompt').fill('My independent creation prompt');
 await assetView(page,'Images'); await page.getByLabel('Gallery look').selectOption(look.id);
 await page.locator(`[data-media-id="${source.id}"] .media-select`).click();
 await expect(page.getByLabel('Save results to look',{exact:true})).toHaveCount(0);
 await page.getByLabel('Edit instruction').fill('A new outfit, preserving the face.');
 await page.getByRole('button',{name:'Generate edited images',exact:true}).click();
 const review=page.locator('.image-review-dialog'); await expect(review).toBeVisible();
 await expect.poll(async()=>(await state(request,id)).assets[0].images.length).toBe(owner.images.length+1);
 const output=(await state(request,id)).assets[0].images.find(i=>!owner.images.some(o=>o.id===i.id));
 expect(output.lookId).toBeNull(); expect(output.generation.edit.referenceLooks[0].context.lookId).toBe(look.id);
 await review.getByRole('button',{name:'Close image review',exact:true}).click();
 await expect(review).not.toBeVisible(); if(narrow) await expect(page.getByLabel('Edit instruction',{exact:true})).toBeFocused(); // Generate is disabled until the next instruction.
 await assetView(page,'Images'); await page.getByLabel('Gallery look').selectOption('general');
 const outputCard=page.locator(`[data-media-id="${output.id}"]`);
 await outputCard.getByRole('button',{name:'Image actions',exact:true}).click();
 await page.getByRole('menuitem',{name:'Edit details…',exact:true}).click();
 await review.getByLabel('Image look',{exact:true}).selectOption(look.id);
 await review.getByRole('button',{name:'Save details',exact:true}).click(); await review.getByRole('button',{name:'Close image review',exact:true}).click();
 await expect.poll(async()=>(await state(request,id)).assets[0].images.find(i=>i.id===output.id).lookId).toBe(look.id);
 await toolsTab(page,'Prompt'); await page.getByRole('button',{name:'Clear selection',exact:true}).click();
 await expect(page.locator('#image-prompt')).toHaveValue('My independent creation prompt');
 await page.getByRole('button',{name:'Review latest edit',exact:true}).click(); await review.getByRole('button',{name:'Generate more…',exact:true}).click();
 await page.getByRole('button',{name:'Queue 1 image',exact:true}).click();
 await expect.poll(async()=>(await state(request,id)).assets[0].images.length).toBe(owner.images.length+2);
 const extra=(await state(request,id)).assets[0].images.find(i=>i.id!==output.id&&!owner.images.some(o=>o.id===i.id));
 expect(extra.lookId).toBeNull(); expect(extra.generation.edit.referenceLooks[0].context.lookId).toBe(look.id);
});

test('look management stays in Details and bulk organization never changes tools',async({page,request})=>{
 await page.setViewportSize({width:1173,height:1000}); const {id,owner}=await fixture(page,request);
 await toolsTab(page,'Prompt'); await page.locator('#image-prompt').fill('My handwritten creation prompt');
 await lookAction(page,owner.looks[0].id,'Duplicate look');
 await expect.poll(async()=>(await state(request,id)).assets[0].looks.length).toBe(3);
 const copy=(await state(request,id)).assets[0].looks[2]; await lookAction(page,copy.id,'Edit look…');
 const dialog=page.locator('.look-editor-dialog'); await dialog.getByLabel('Look name',{exact:true}).fill('Raincoat');
 await dialog.getByRole('button',{name:'Save look',exact:true}).click();
 await lookAction(page,copy.id,'Archive look'); await expect.poll(async()=>(await state(request,id)).assets[0].looks[2].archived).toBe(true);
 await lookAction(page,copy.id,'Unarchive look');
 await assetView(page,'Images'); await page.getByRole('button',{name:'Select multiple',exact:true}).click();
 await page.getByRole('checkbox',{name:'Select image',exact:true}).first().check();
 await page.getByLabel('Move selected to look',{exact:true}).selectOption(copy.id); await page.getByRole('button',{name:'Assign selected',exact:true}).click();
 await expect.poll(async()=>(await state(request,id)).assets[0].images.filter(i=>i.lookId===copy.id).length).toBe(1);
 await expect(page.locator('#image-prompt')).toHaveValue('My handwritten creation prompt');
 await page.reload(); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true'); await page.getByLabel('Gallery look').selectOption(copy.id); await expect(page.locator('.reference-card')).toHaveCount(1);
 await imageAction(page,'Edit details…'); await expect(page.locator('.image-review-dialog').getByLabel('Image look',{exact:true})).toHaveValue(copy.id);
});
