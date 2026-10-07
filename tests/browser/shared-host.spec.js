import {test,expect} from './fixtures.js';

test('desktop recovery links save exact JSON text while media downloads retain their resource URLs', async ({page}) => {
 await page.goto('/');
 const text = '{\n  "name": "Ríley 日本語 ✦",\n  "dialogue": "First line\\nSecond line"\n}\n';
 await page.evaluate(async text => {
  const bridge = await import('/_content/Lumibelle.UI/host-bridge.js');
  window.nativeSaves = [];
  bridge.mount({invokeMethodAsync: async (...args) => { window.nativeSaves.push(args); }});
  const bytes = new TextEncoder().encode(text);
  const data = 'data:application/json;base64,' + btoa(String.fromCharCode(...bytes));
  for (const name of ['asset-review.json', 'shot-review.json']) {
   const a = document.createElement('a'); a.href = data; a.download = name; a.textContent = name;
   document.body.append(a);
  }
  const a = document.createElement('a'); a.href = '/media/projects/fixture/takes/fixture?frame=4';
  a.download = 'take.mp4'; a.textContent = 'take.mp4'; document.body.append(a);
 }, text);
 for (const name of ['asset-review.json', 'shot-review.json']) {
  await page.getByRole('link', {name, exact:true}).click();
  await expect.poll(() => page.evaluate(() => window.nativeSaves)).toContainEqual(['SaveText', name, text]);
 }
 await page.getByRole('link', {name:'take.mp4', exact:true}).click();
 await expect.poll(() => page.evaluate(() => window.nativeSaves)).toContainEqual(['SaveResource', 'take.mp4', '/media/projects/fixture/takes/fixture?frame=4']);
 await page.evaluate(async () => (await import('/_content/Lumibelle.UI/host-bridge.js')).unmount());
});

test('shared UI assets, source routes, ranged videos, frames and missing resources use the extracted web adapters', async ({page,request})=>{
 const {id}=await (await request.get('/fixtures/new')).json();
 const fixture=await request.post(`/fixtures/${id}/cut-takes`); expect(fixture.ok(),await fixture.text()).toBeTruthy();
 const shots=await fixture.json(); const media=`/media/projects/${id}/takes/${shots.takes[0].id}`;
 const head=await request.head(media); expect(head.status()).toBe(200); expect(Number(head.headers()['content-length'])).toBeGreaterThan(100);
 const partial=await request.get(media,{headers:{Range:'bytes=16-47'}}); expect(partial.status()).toBe(206); expect((await partial.body()).length).toBe(32); expect(partial.headers()['content-range']).toMatch(/^bytes 16-47\//);
 expect((await request.get(media,{headers:{Range:'bytes=999999999999-'}})).status()).toBe(416);
 expect((await request.get(`${media}/frames/0`)).headers()['content-type']).toMatch(/^image\//);
 expect((await request.get(`/media/projects/${id}/takes/00000000-0000-0000-0000-000000000001`)).status()).toBe(404);
 for(const resource of ['lumibelle.css','bootstrap.js','script-editor.js','prompt-editor.js','take-player.js','cut-player.js']) {
  const response=await request.get(`/_content/Lumibelle.UI/${resource}`); expect(response.status(),resource).toBe(200); expect((await response.body()).length).toBeGreaterThan(20);
 }
 await page.goto(`/projects/${id}/script`); await expect(page.getByRole('textbox',{name:'Screenplay',exact:true})).toBeVisible();
 await page.goto(`/projects/${id}/shots`); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true');
 await page.goto(`/projects/${id}/cut`); await expect(page.getByRole('heading',{name:'Cut studio',exact:true})).toBeVisible();
 await page.goto('/Error'); await expect(page.getByRole('heading',{name:'We couldn’t open that page',exact:true})).toBeVisible();
});
