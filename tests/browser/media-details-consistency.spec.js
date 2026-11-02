import { test, expect } from './fixtures.js';

for (const narrow of [false, true]) test(`media details share layout, actions and safe dismissal (${narrow ? 'narrow' : 'desktop'})`, async ({page, request}) => {
  test.setTimeout(90000);
  const {id} = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  await request.post(`/fixtures/${id}/reference-workspace`);
  await request.post(`/fixtures/${id}/regeneration-source`);
  const library = async () => (await (await request.get(`/fixtures/${id}`)).json()).assets;
  await expect.poll(async () => (await library()).reels.length).toBe(1);
  const initial = await library(), owner = initial.assets[0];
  await page.setViewportSize({width:narrow?390:1173, height:narrow?844:1000});
  await page.goto(`/projects/${id}/assets?assetId=${owner.id}`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true');
  const closeTools = page.getByRole('button',{name:'Close Asset tools',exact:true});
  if (await closeTools.isVisible()) await closeTools.click();
  for (const kind of ['Image','Reel','Voice']) {
    const card = page.locator(`.asset-media-card[data-media-kind=${kind}]`).first();
    const trigger = card.getByRole('button',{name:/^(Actions for|Image actions)/});
    async function open() {
      await trigger.click();
      await page.getByRole('menuitem',{name:'Edit details…',exact:true}).click();
      await expect(page.getByRole('dialog',{name:`${kind} details`,exact:true})).toBeVisible();
    }
    await open();
    const dialog=page.getByRole('dialog',{name:`${kind} details`,exact:true});
    const save=dialog.getByRole('button',{name:'Save details',exact:true});
    await expect(save).toBeDisabled(); await expect(save).toBeInViewport();
    await expect(dialog.locator('.dialog-header h2')).toHaveText(`${kind} details`);
    expect(await dialog.locator('.media-details-save-actions button').allTextContents()).toEqual(['Close','Save details']);
    expect(await dialog.locator('.mud-dialog-actions .mud-button-filled-primary').count()).toBe(1);
    const name=dialog.getByRole('textbox',{name:'Name',exact:true});
    const original=await name.inputValue();
    const revised=`${original || kind} reviewed`;
    await name.fill(`${original} draft`);
    await dialog.locator('.dialog-close').click();
    await expect(dialog.getByRole('button',{name:'Keep editing',exact:true})).toBeInViewport();
    await dialog.getByRole('button',{name:'Keep editing',exact:true}).click();
    await expect(name).toHaveValue(`${original} draft`);
    await name.fill(original);
    await expect(save).toBeDisabled();
    await expect(dialog.getByRole('button',{name:'Keep editing',exact:true})).not.toBeVisible();
    await page.screenshot({path:`artifacts/media-details-${kind.toLowerCase()}-${narrow?'narrow':'desktop'}.png`});
    await name.press('Escape'); await expect(dialog).not.toBeVisible();
    // A real save still uses the existing media-specific persistence path.
    await open(); await name.fill(revised);
    await save.click();
    await expect.poll(async () => {
      const saved=await library();
      return kind==='Image' ? saved.assets.find(a=>a.id===owner.id).images.some(i=>i.name===revised)
        : (kind==='Reel'?saved.reels:saved.voices).some(i=>i.name===revised);
    }).toBe(true);
    if (await dialog.isVisible()) await dialog.getByRole('button',{name:'Close',exact:true}).click();
    await expect(dialog).not.toBeVisible();
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  }
});
