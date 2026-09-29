import { test, expect } from './fixtures.js';

const state = async (request, id) => (await request.get(`/fixtures/${id}/cut`)).json();
async function setup(page, request) {
  const { id } = await (await request.get('/fixtures/new')).json();
  const response = await request.post(`/fixtures/${id}/cut-takes`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const shots = await response.json();
  await page.goto(`/projects/${id}/cut`);
  await expect(page.locator('.cut-heading')).toHaveAttribute('data-interactive', 'true');
  return { id, shots };
}
async function add(page, shots, second = true) {
  await page.locator('.cut-workspace-toolbar').getByRole('button', { name: 'Choose takes', exact: true }).click();
  const dialog = page.locator('.cut-chooser');
  if (!second) {
    await dialog.getByLabel('Action for Arrival', { exact:true }).selectOption('00000000-0000-0000-0000-000000000000');
    await dialog.getByLabel('Take for Arrival', { exact:true }).selectOption(shots.takes[0].id);
  } else await expect(dialog.getByLabel('Take for Arrival', { exact: true })).toHaveValue(shots.takes[0].id);
  if (second) await dialog.getByLabel(`Take for ${shots.shots[1].title}`, { exact: true }).selectOption(shots.takes[2].id);
  await expect(dialog.getByLabel('Take for Not generated')).toBeDisabled();
  await dialog.getByRole('button', { name: /Apply changes/ }).click();
  await expect(dialog).not.toBeVisible();
}


const selectClip = async (page, n) => { await page.locator('.timeline-select').nth(n).focus(); await page.keyboard.press('Enter'); };
async function drag(page, locator, dx, cancel = false) {
  const b = await locator.boundingBox(); await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2);
  await page.mouse.down(); await page.mouse.move(b.x + b.width / 2 + dx, b.y + b.height / 2, { steps: 8 });
  if (cancel) await page.keyboard.press('Escape');
  await page.mouse.up();
}
async function numeric(page, name, value) { await page.getByLabel(name, { exact:true }).fill(String(value)); await page.getByLabel(name, { exact:true }).blur(); }

test('timeline assembly, precision trims, one undo per drag, cancellation, replacement and reload', async ({page,request}) => {
  const {id,shots}=await setup(page,request); await add(page,shots);
  await expect(page.locator('.timeline-clip')).toHaveCount(2);
  const player=page.locator('.cut-player');
  await expect(player).toHaveAttribute('data-playing','false');
  await expect(player.locator('.cut-paused-frame')).toBeVisible();
  await numeric(page,'Start frame',5); await numeric(page,'End frame',40);
  await expect.poll(async()=> (await state(request,id)).clips[0]?.startFrame).toBe(4);
  await expect.poll(async()=> (await state(request,id)).clips[0]?.endFrameExclusive).toBe(40);
  await expect(page.locator('.cut-trim-summary')).toContainText('5–40');
  const start=page.getByRole('slider',{name:'Source start frame',exact:true});
  await start.focus(); await page.keyboard.press('ArrowRight');
  await expect(page.getByLabel('Start frame',{exact:true})).toHaveValue('6');
  await expect(start).toBeEnabled();
  await expect(start).toBeFocused();
  await page.keyboard.press('Shift+ArrowRight');
  await expect(page.getByLabel('Start frame',{exact:true})).toHaveValue('16');
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await expect(page.getByLabel('Start frame',{exact:true})).toHaveValue('6');
  const before=await page.getByLabel('End frame',{exact:true}).inputValue();
  await drag(page,page.getByRole('slider',{name:'Source end frame',exact:true}),-100,true);
  await expect(page.getByLabel('End frame',{exact:true})).toHaveValue(before);
  await drag(page,page.getByRole('slider',{name:'Source end frame',exact:true}),-100);
  await expect(page.getByLabel('End frame',{exact:true})).not.toHaveValue(before);
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await expect(page.getByLabel('End frame',{exact:true})).toHaveValue(before);
  await page.getByRole('button',{name:'Redo',exact:true}).click();
  await expect(page.getByLabel('End frame',{exact:true})).not.toHaveValue(before);
  await page.getByRole('button',{name:'Reset trim'}).click();
  await expect(page.getByLabel('Start frame',{exact:true})).toHaveValue('1');
  await page.getByRole('combobox',{name:'Take',exact:true}).selectOption(shots.takes[1].id);
  await expect(page.getByLabel('End frame',{exact:true})).toHaveValue(String(shots.takes[1].frames.length));
  await add(page,shots,false); await expect(page.locator('.timeline-clip')).toHaveCount(3);
  await page.getByRole('button',{name:'Move later',exact:true}).focus(); await page.keyboard.press('Enter');
  await expect.poll(async()=> (await state(request,id)).clips[2]?.takeId).toBe(shots.takes[0].id);
  await page.getByRole('button',{name:'Remove clip',exact:true}).click();
  await expect(page.locator('.cut-workspace-toolbar').getByRole('button',{name:'Choose takes',exact:true})).toBeFocused();
  await expect(page.locator('.timeline-clip')).toHaveCount(2);
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await expect.poll(async()=> (await state(request,id)).clips.length).toBe(3);
  await page.reload(); await expect(page.locator('.timeline-clip')).toHaveCount(3);
  await expect(page.locator('.cut-paused-frame')).toBeVisible();
  await expect(page.locator('[data-total]')).toContainText('3 clips');
  await expect(page.getByRole('button',{name:'Play',exact:true})).toBeInViewport();
  await page.screenshot({path:'test-results/timeline-desktop.png',fullPage:true});
  const source=await (await request.get('/fixtures/'+id+'/shots')).json();
  expect(source.shots[0].selectedTakeId).toBe(shots.takes[0].id); expect(source.takes).toHaveLength(3);
  await page.setViewportSize({width:390,height:844});
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  await page.getByRole('slider',{name:'Source start frame',exact:true}).scrollIntoViewIfNeeded();
  await expect(page.getByRole('slider',{name:'Source start frame',exact:true})).toBeInViewport();
  await page.screenshot({path:'test-results/timeline-mobile.png',fullPage:true});
});

test('combined playback keeps selection, exact frame stepping, audio, pause, handoff and replay',async({page,request})=>{
  const {id,shots}=await setup(page,request); await add(page,shots);
  await expect.poll(async()=> (await state(request,id)).clips.length).toBe(2);
  const cut=await state(request,id); cut.clips[0].startFrame=5; cut.clips[0].endFrameExclusive=24;
  cut.clips[1].startFrame=12; cut.clips[1].endFrameExclusive=48;
  await request.post('/fixtures/'+id+'/cut',{data:cut.clips}); await page.reload();
  const player=page.locator('.cut-player');
  await expect(player).toHaveAttribute('data-frame-index','5');
  await page.getByRole('button',{name:'Next frame',exact:true}).click();
  await expect(player).toHaveAttribute('data-frame-index','6');
  const head=page.getByRole('slider',{name:'Cut position',exact:true});
  await head.focus(); await page.keyboard.press('End');
  await expect(player).toHaveAttribute('data-frame-index','47');
  await expect(player).toHaveAttribute('data-clip-id',cut.clips[1].id);
  await page.getByRole('button',{name:'Play from beginning',exact:true}).click();
  await expect(player.locator('video')).toHaveCount(2);
  await page.getByRole('button',{name:'Pause',exact:true}).click();
  await expect(player).toHaveAttribute('data-playing','false');
  expect(await player.locator('video').evaluateAll(v=>v.every(x=>x.paused))).toBe(true);
  await page.getByRole('button',{name:'Mute',exact:true}).click();
  await expect(page.getByRole('button',{name:'Unmute',exact:true})).toHaveAttribute('aria-pressed','true');
  await page.getByRole('button',{name:'Play from beginning',exact:true}).click();
  await expect(player).toHaveAttribute('data-clip-id',cut.clips[1].id);
  await expect(page.locator('.timeline-clip.selected')).toHaveAttribute('data-clip-id',cut.clips[0].id);
  expect(await player.locator('video:not([hidden])').evaluate(v=>v.muted)).toBe(true);
  expect(await player.locator('video').evaluateAll(v=>v.filter(x=>!x.paused).length)).toBeLessThanOrEqual(1);
  await expect(player.getByText('End of cut',{exact:true})).toBeVisible();
  await expect(player).toHaveAttribute('data-playing','false');
  await page.getByRole('button',{name:'Replay',exact:true}).click();
  await expect(player).toHaveAttribute('data-clip-id',cut.clips[0].id);
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
  await expect(player).toHaveAttribute('data-playing','false');
  await page.evaluate(()=>{delete document.hidden;document.dispatchEvent(new Event('visibilitychange'));});
  await page.getByRole('button',{name:'Play',exact:true}).click();
  await page.getByRole('button',{name:'Move later',exact:true}).click();
  await expect(player).toHaveAttribute('data-playing','false');
});

test('unavailable takes and conflicts preserve drafts and can be repaired or removed',async({page,request})=>{
  const {id,shots}=await setup(page,request);await add(page,shots);
  await expect.poll(async()=> (await state(request,id)).clips.length).toBe(2);
  await request.post('/fixtures/'+id+'/cut-trash/'+shots.takes[0].id);
  await page.getByRole('button',{name:'Refresh takes',exact:true}).click();
  await expect(page.getByRole('button',{name:'Play from beginning',exact:true})).toBeDisabled();
  await expect(page.locator('.timeline-clip.unavailable')).toHaveCount(1);
  await expect(page.locator('.cut-inspector')).toContainText('Take is in Trash');
  await request.post('/fixtures/'+id+'/cut-restore');
  await page.getByRole('button',{name:'Refresh takes',exact:true}).click();
  await expect(page.getByRole('button',{name:'Play from beginning',exact:true})).toBeEnabled();
  const old=await state(request,id);await request.post('/fixtures/'+id+'/cut',{data:old.clips.toReversed()});
  await numeric(page,'Start frame',5);
  await expect(page.getByText('Conflict · draft retained',{exact:true})).toBeVisible();
  await expect(page.getByLabel('Start frame',{exact:true})).toHaveValue('5');
  const download=page.waitForEvent('download');await page.getByRole('button',{name:'Download unsaved copy'}).click();
  expect((await download).suggestedFilename()).toBe('unsaved-cut.json');
  await page.getByRole('button',{name:'Reload saved version…'}).click();
  await page.getByRole('button',{name:'Reload saved cut',exact:true}).click();
  await expect(page.locator('.timeline-select').first()).toContainText(shots.shots[1].title);
  await request.post('/fixtures/'+id+'/cut-trash/'+shots.takes[0].id);
  await page.getByRole('button',{name:'Refresh takes',exact:true}).click();
  await selectClip(page,1);await page.getByRole('button',{name:'Remove clip',exact:true}).click();
  await expect.poll(async()=> (await state(request,id)).clips.length).toBe(1);
  await expect(page.getByRole('button',{name:'Play from beginning',exact:true})).toBeEnabled();
});

test('delayed and failed next media buffers and retries without skipping',async({page,request})=>{
  const {id,shots}=await setup(page,request);await add(page,shots);
  await expect.poll(async()=> (await state(request,id)).clips.length).toBe(2);
  const cut=await state(request,id);cut.clips[0].endFrameExclusive=1;
  await request.post('/fixtures/'+id+'/cut',{data:cut.clips});await page.reload();
  // The first frame is drawn by the connected player; clicking Play earlier does nothing.
  await expect(page.locator('.cut-player')).toHaveAttribute('data-frame-ready','true');
  await page.route('**/takes/'+shots.takes[2].id,async route=>{await new Promise(r=>setTimeout(r,1000));await route.continue();});
  await page.getByRole('button',{name:'Play from beginning',exact:true}).click();
  await expect(page.locator('.cut-buffering')).toBeVisible();
  await expect(page.locator('.cut-player')).toHaveAttribute('data-clip-id',cut.clips[1].id);
  await page.getByRole('button',{name:'Pause',exact:true}).click();
  await page.unroute('**/takes/'+shots.takes[2].id);
  await page.route('**/takes/'+shots.takes[2].id,route=>route.abort());
  await page.getByRole('button',{name:'Play from beginning',exact:true}).click();
  await expect(page.locator('.cut-player [data-error]')).toBeVisible();
  await expect(page.locator('.cut-player')).toHaveAttribute('data-clip-id',cut.clips[0].id);
  await page.unroute('**/takes/'+shots.takes[2].id);
  await page.getByRole('button',{name:'Resume',exact:true}).click();
  await expect(page.locator('.cut-player')).toHaveAttribute('data-clip-id',cut.clips[1].id);
});

test('drag order, timeline edge trim, zoom, scrolling and long cut stay bounded',async({page,request})=>{
  const {id,shots}=await setup(page,request);await add(page,shots);
  await expect.poll(async()=> (await state(request,id)).clips.length).toBe(2);
  const cut=await state(request,id);const a=page.locator('.timeline-clip').first(),b=page.locator('.timeline-clip').nth(1);
  const ba=await a.boundingBox(),bb=await b.boundingBox();
  await drag(page,a.locator('.timeline-grip'),bb.x+bb.width-ba.x-25);
  await expect.poll(async()=> (await state(request,id)).clips[1]?.id).toBe(cut.clips[0].id);
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await expect.poll(async()=> (await state(request,id)).clips[0]?.id).toBe(cut.clips[0].id);
  const inHandle=page.getByRole('slider',{name:'Clip start frame',exact:true});
  const inBounds=await inHandle.boundingBox();
  await page.mouse.move(inBounds.x+inBounds.width/2,inBounds.y+inBounds.height/2);await page.mouse.down();
  await page.mouse.move(inBounds.x+inBounds.width/2+75,inBounds.y+inBounds.height/2,{steps:5});
  expect((await inHandle.boundingBox()).x).toBeGreaterThan(inBounds.x+50);
  await page.mouse.up();
  await expect.poll(async()=> (await state(request,id)).clips[0]?.startFrame).toBeGreaterThan(0);
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await expect(page.getByRole('slider',{name:'Clip start frame',exact:true})).toHaveAttribute('aria-valuenow','1');
  await drag(page,page.getByRole('slider',{name:'Clip end frame',exact:true}),-90);
  await expect.poll(async()=> (await state(request,id)).clips[0]?.endFrameExclusive).toBeLessThan(cut.clips[0].endFrameExclusive);
  const latest=await state(request,id);
  const entries=Array.from({length:60},(_,i)=>({...latest.clips[i%2],id:crypto.randomUUID()}));
  entries[20].startFrame=0;entries[20].endFrameExclusive=1;
  await request.post('/fixtures/'+id+'/cut',{data:entries});await page.reload();
  await expect(page.locator('.timeline-clip')).toHaveCount(60);
  const viewport=page.locator('.timeline-scroll');
  expect(await viewport.evaluate(e=>e.scrollWidth<=e.clientWidth+2)).toBe(true);
  await page.getByRole('button',{name:'Zoom in',exact:true}).click();
  await page.getByRole('button',{name:'Zoom in',exact:true}).click();
  expect(await viewport.evaluate(e=>e.scrollWidth>e.clientWidth)).toBe(true);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  expect(await page.locator('.timeline-thumbnails img').count()).toBeLessThanOrEqual(24);
  await viewport.evaluate(e=>e.scrollLeft=e.scrollWidth/2);
  await page.getByRole('slider',{name:'Cut position',exact:true}).focus();await page.keyboard.press('End');
  await page.getByRole('button',{name:'Fit',exact:true}).click();
  expect(await viewport.evaluate(e=>e.scrollWidth<=e.clientWidth+2)).toBe(true);
  await selectClip(page,20);
  await expect(page.getByRole('slider',{name:'Source end frame',exact:true})).toBeVisible();
  await page.setViewportSize({width:390,height:844});
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
});

test('trim previews are exact, stale archive responses cannot replace newer frames',async({page,request})=>{
  const {id,shots}=await setup(page,request);await add(page,shots);
  await expect.poll(async()=> (await state(request,id)).clips.length).toBe(2);
  const old=await state(request,id),end=page.getByRole('slider',{name:'Source end frame',exact:true});
  const bounds=await end.boundingBox(),strip=await page.locator('.source-strip').boundingBox();
  await page.route('**/takes/'+shots.takes[0].id+'/frames/*',async route=>{if(route.request().url().endsWith('/45')) await new Promise(r=>setTimeout(r,750));await route.continue();});
  await page.mouse.move(bounds.x+bounds.width/2,bounds.y+20);await page.mouse.down();
  await page.mouse.move(bounds.x+bounds.width/2-strip.width*10/shots.takes[0].frames.length,bounds.y+20,{steps:4});
  await expect(page.locator('.cut-player')).toHaveAttribute('data-frame-index','45');
  expect((await state(request,id)).revision).toBe(old.revision);
  await page.mouse.move(bounds.x+bounds.width/2-strip.width*20/shots.takes[0].frames.length,bounds.y+20,{steps:4});
  await expect(page.locator('.cut-player')).toHaveAttribute('data-frame-index','35');
  await page.keyboard.press('Escape');await page.mouse.up();
  await expect(page.locator('.cut-player')).toHaveAttribute('data-frame-index','0');
  await expect(page.getByLabel('End frame',{exact:true})).toHaveValue('56');
  await page.waitForTimeout(800);
  await expect(page.locator('.cut-player')).toHaveAttribute('data-frame-index','0');
});

test('touch trim handles work on mobile and cancellation retains the range',async({browser,request,baseURL})=>{
  const context=await browser.newContext({baseURL,viewport:{width:390,height:844},isMobile:true,hasTouch:true});
  const page=await context.newPage();
  try {
    const {id,shots}=await setup(page,request);await add(page,shots);
    await expect.poll(async()=> (await state(request,id)).clips.length).toBe(2);
    const handle=page.getByRole('slider',{name:'Source end frame',exact:true});await handle.scrollIntoViewIfNeeded();
    const box=await handle.boundingBox();expect(box.width).toBeGreaterThanOrEqual(22);
    const session=await context.newCDPSession(page),x=box.x+box.width/2,y=box.y+box.height/2;
    const touch=async(type,at)=>session.send('Input.dispatchTouchEvent',{type,touchPoints:at===null?[]:[{x:at,y}]});
    await touch('touchStart',x);await touch('touchMove',x-50);await touch('touchCancel',null);
    await expect(page.getByLabel('End frame',{exact:true})).toHaveValue('56');
    await touch('touchStart',x);await touch('touchMove',x-50);await touch('touchEnd',null);
    await expect.poll(async()=> (await state(request,id)).clips[0]?.endFrameExclusive).toBeLessThan(56);
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
    await page.screenshot({path:'test-results/timeline-touch.png',fullPage:true});
  } finally { await context.close(); }
});

test('choose takes inserts earlier shots and swaps exact clips without moving existing edits',async({page,request})=>{
  const {id,shots}=await setup(page,request),dialog=page.locator('.cut-chooser');
  const open=async()=>page.locator('.cut-workspace-toolbar').getByRole('button',{name:'Choose takes',exact:true}).click();
  const apply=async()=>{await dialog.getByRole('button',{name:/Apply changes/}).click();await expect(dialog).not.toBeVisible();};
  await open();await dialog.getByLabel('Take for Arrival',{exact:true}).selectOption('');
  await dialog.getByLabel('Take for '+shots.shots[1].title,{exact:true}).selectOption(shots.takes[2].id);await apply();
  await numeric(page,'Start frame',6);await numeric(page,'End frame',33);
  await expect.poll(async()=> (await state(request,id)).clips[0]?.endFrameExclusive).toBe(33);
  const second=(await state(request,id)).clips[0];
  await open();await expect(dialog.getByRole('button',{name:'Apply changes (1)',exact:true})).toBeEnabled();await apply();
  await expect.poll(async()=> (await state(request,id)).clips.length).toBe(2);
  let cut=await state(request,id);expect(cut.clips[1]).toEqual(second);
  await expect(page.locator('.cut-player')).toHaveAttribute('data-frame-take-id',shots.takes[0].id);
  const firstId=cut.clips[0].id;
  await numeric(page,'Start frame',5);await numeric(page,'End frame',20);
  await page.getByRole('button',{name:'Next take',exact:true}).click();
  await expect.poll(async()=> (await state(request,id)).clips[0]?.takeId).toBe(shots.takes[1].id);
  cut=await state(request,id);expect(cut.clips[0].id).toBe(firstId);expect(cut.clips[0].startFrame).toBe(0);expect(cut.clips[0].endFrameExclusive).toBe(56);expect(cut.clips[1]).toEqual(second);
  await expect(page.locator('.cut-player')).toHaveAttribute('data-frame-take-id',shots.takes[1].id);
  await page.getByRole('button',{name:'Undo',exact:true}).click();
  await expect(page.getByLabel('Start frame',{exact:true})).toHaveValue('5');
  await expect(page.getByLabel('End frame',{exact:true})).toHaveValue('20');
  await page.getByRole('button',{name:'Next take',exact:true}).click();
  await page.getByRole('button',{name:'Previous take',exact:true}).click();
  await expect(page.getByRole('combobox',{name:'Take',exact:true})).toHaveValue(shots.takes[0].id);
  await open();await expect(dialog.getByRole('button',{name:'Apply changes (0)',exact:true})).toBeDisabled();
  await dialog.getByLabel('Take for Arrival',{exact:true}).selectOption(shots.takes[1].id);await apply();
  await expect.poll(async()=> (await state(request,id)).clips[0]?.takeId).toBe(shots.takes[1].id);
  expect((await state(request,id)).clips.map(c=>c.id)).toEqual([firstId,second.id]);
  await page.getByRole('button',{name:'Move later',exact:true}).click();
  await open();await dialog.getByLabel('Take for Arrival',{exact:true}).selectOption(shots.takes[0].id);await apply();
  await expect.poll(async()=> (await state(request,id)).clips[1]?.takeId).toBe(shots.takes[0].id);
  expect((await state(request,id)).clips.map(c=>c.id)).toEqual([second.id,firstId]);
  await open();await dialog.getByLabel('Action for Arrival',{exact:true}).selectOption('00000000-0000-0000-0000-000000000000');await apply();
  await expect.poll(async()=> (await state(request,id)).clips.length).toBe(3);
  const duplicate=(await state(request,id)).clips[0];expect(duplicate.id).not.toBe(firstId);
  await selectClip(page,2);await open();
  await expect(dialog.getByLabel('Action for Arrival',{exact:true})).toHaveValue(firstId);
  await dialog.getByLabel('Action for Arrival',{exact:true}).selectOption(duplicate.id);
  await dialog.getByLabel('Take for Arrival',{exact:true}).selectOption(shots.takes[1].id);await apply();
  await expect.poll(async()=> (await state(request,id)).clips[0]?.takeId).toBe(shots.takes[1].id);
  cut=await state(request,id);expect(cut.clips[2].takeId).toBe(shots.takes[0].id);expect(cut.clips[1]).toEqual(second);
  await page.screenshot({path:'test-results/cut-swap-desktop.png',fullPage:true});
  await page.setViewportSize({width:390,height:844});await page.locator('.cut-take-switcher').scrollIntoViewIfNeeded();
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  await expect(page.getByRole('button',{name:'Previous take',exact:true})).toBeInViewport();
  await page.screenshot({path:'test-results/cut-swap-mobile.png',fullPage:true});
  await page.reload();await expect(page.locator('.timeline-clip')).toHaveCount(3);
});
