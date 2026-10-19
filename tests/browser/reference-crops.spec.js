import { test, expect } from './fixtures.js';
import { openReferenceCrop, imageOutput, toolsTab, addImageReference } from './workspace-tools.js';
import { cropColors, expectCropThumbnail } from './crop-thumbnails.js';

async function openAssets(page, id) {
  await page.goto(`/projects/${id}/assets`); await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true');
}
async function setRange(locator, value) {
  await locator.evaluate((node, value) => { node.value = String(value); node.dispatchEvent(new Event('input', { bubbles: true })); }, value);
}

for (const workflow of ['Krea2', 'Flux2Klein9bKv']) {
  test(`${workflow}: independent crops persist through switching, review, Trash, and restoration`, async ({ page, request }) => {
    await page.setViewportSize({ width: 1440, height: 1000 });
    const project = await (await request.get('/fixtures/new')).json();
    const library = await (await request.post(`/fixtures/${project.id}/images?patterned=true`)).json();
    const donor = library.assets[1], reference = donor.images[0];
    await openAssets(page, project.id);
    await page.locator('.asset-media-card[data-media-kind=Image] .media-select').click();
    await toolsTab(page, 'Inputs');
    await addImageReference(page, `${donor.id}/${reference.id}`);
    const baseRow = page.locator('.compact-image-input').nth(0), referenceRow = page.locator('.compact-image-input').nth(1);
    const green = Array(4).fill(cropColors[1]), blue = Array(4).fill(cropColors[2]);
    const manager = page.locator('.image-input-manager-dialog');
    async function cropInput(index, zoom, x, y, apply = true, reset = false) {
      await page.getByRole('button', { name: /^(?:Edit|Manage) references$/, exact: true }).click();
      const crop = await openReferenceCrop(page, manager, index);
      if (reset) await crop.getByRole('button',{name:'Full image',exact:true}).click();
      else {
        await crop.getByText('Precise crop controls',{exact:true}).click();
        for (const [label,value] of [['Crop zoom',zoom],['Crop horizontal position',x],['Crop vertical position',y]])
          await crop.getByRole('slider',{name:label,exact:true}).evaluate((el,v)=>{el.value=String(v);el.dispatchEvent(new Event('change',{bubbles:true}));},value);
      }
      await crop.getByRole('button', { name: 'Apply crop', exact: true }).click();
      await manager.getByRole('button',{name:apply?'Apply changes':'Cancel',exact:true}).click();
    }
    await expectCropThumbnail(page, baseRow, .8, cropColors);
    await expectCropThumbnail(page, referenceRow, 1.5, cropColors);
    await cropInput(0,2,100,0);
    await expectCropThumbnail(page, baseRow,.8,green);
    await cropInput(1,4,0,100,false);
    await expectCropThumbnail(page, referenceRow,1.5,cropColors);
    await cropInput(1,4,0,100);
    await expectCropThumbnail(page, referenceRow,1.5,blue);
    await cropInput(0,1,50,50,true,true);
    await expectCropThumbnail(page,baseRow,.8,cropColors);
    await expectCropThumbnail(page,referenceRow,1.5,blue);
    await cropInput(0,2,100,0);
    await page.locator('#asset-image-workflow').selectOption('Flux2Klein9bKv');
    await toolsTab(page, 'Inputs');
    await expect(referenceRow).toContainText('Cropped');
    await expectCropThumbnail(page, baseRow, .8, green);
    await expectCropThumbnail(page, referenceRow, 1.5, blue);
    await toolsTab(page, 'Prompt');
    await page.locator('#asset-image-workflow').selectOption('Krea2');
    await imageOutput(page);
    await page.locator('.image-generation-settings > summary').click();
    await imageOutput(page);
    await page.locator('#base-reference-boost').fill('1.75');
    await imageOutput(page);
    await page.locator('#reference-boost').fill('5.5');
    await toolsTab(page, 'Prompt');
    await page.locator('#image-prompt').fill('Place this person in the base scene, matching the lighting.');
    await expect(page.getByRole('button', { name: 'Generate edited images', exact: true })).toBeEnabled();
    if (workflow === 'Krea2') {
      await page.screenshot({ path: 'test-results/krea-two-images-desktop.png', fullPage: true });
      await page.setViewportSize({ width: 390, height: 844 });
      await toolsTab(page, 'Inputs');
      await expectCropThumbnail(page, baseRow, .8, green);
      await expectCropThumbnail(page, referenceRow, 1.5, blue);
      await page.screenshot({ path: 'test-results/edit-crop-thumbnails-mobile.png' });
      await imageOutput(page);
      await expect(page.locator('.generation-panel')).toBeVisible();
      await expect(page.locator('#base-reference-boost')).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBeTruthy();
      await page.screenshot({ path: 'test-results/krea-two-images-mobile.png', fullPage: true });
      await page.setViewportSize({ width: 1440, height: 1000 });
    } else await page.locator('#asset-image-workflow').selectOption(workflow);
    await imageOutput(page);
    await page.locator('#candidate-count').selectOption('2');
    await page.getByRole('button', { name: 'Generate edited images', exact: true }).click();
    await expect(page.getByRole('button',{name:/^(?:Edit|Manage) references$/,exact:true})).toBeDisabled();
    const modal = page.locator('.image-review-dialog');
    await expect(modal).toBeVisible();
    await modal.getByRole('button', { name: 'Compare with Reference 2', exact: true }).click();
    await expect(modal.getByRole('button', { name: 'Submitted crops', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(modal.locator('.review-viewer img[alt="Reference 2"]')).toHaveAttribute('style', /width:400%/);
    await modal.getByRole('button', { name: 'View Source', exact: true }).click();
    await expect(modal.getByRole('button', { name: 'Full images', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await modal.getByRole('button', { name: 'Compare with Reference 2', exact: true }).click();
    await expect(modal.locator('.review-viewer img[alt="Source"]')).toHaveAttribute('style', /width:200%/);
    await expect(modal.locator('.review-viewer img[alt="Reference 2"]')).toHaveAttribute('style', /width:400%/);
    await modal.getByRole('button', { name: 'Side by side', exact: true }).click();
    await expect(modal.locator('.review-viewer.side-by-side')).toBeVisible();
    await expect(modal.getByRole('button', { name: 'View Take 2', exact: true })).toBeVisible();
    await modal.getByRole('button', { name: 'Close image review' }).click();
    const saved = (await (await request.get(`/fixtures/${project.id}`)).json()).assets;
    const takes = saved.assets[0].images.filter(i => i.origin === 2);
    expect(takes).toHaveLength(2);
    for (const take of takes) {
      expect(take.generation.edit.sourceCrop).toEqual({ x: .5, y: 0, width: .5, height: .5 });
      expect(take.generation.edit.referenceCrops).toEqual([{ reference: { assetId: donor.id, imageId: reference.id }, crop: { x: 0, y: .75, width: .25, height: .25 } }]);
      expect(take.generation.edit.baseReferenceBoost).toBe(workflow === 'Krea2' ? 1.75 : null);
    }
    await request.post(`/fixtures/${project.id}/trash/${reference.id}`);
    await openAssets(page, project.id);
    await page.getByRole('button',{name:/^Preview image/}).first().click();
    await modal.getByRole('button', { name: 'Compare with Reference 2', exact: true }).click();
    await expect(modal.getByRole('button', { name: 'Restore reference', exact: true })).toBeVisible();
    await setRange(modal.getByRole('slider', { name: 'Comparison slider' }), 71);
    await expect(modal.locator('.review-viewer img[alt="Reference 2"]')).toHaveAttribute('src', /\/media\/trash\//);
    await expect(modal.locator('.review-viewer img[alt="Reference 2"]')).toHaveAttribute('style', /width:400%/);
    await modal.getByRole('button', { name: 'Restore reference', exact: true }).click();
    await expect(modal.getByRole('button', { name: 'Restore reference', exact: true })).toHaveCount(0);
    await expect(modal.getByRole('slider', { name: 'Comparison slider' })).toHaveValue('71');
    await expect(modal.locator('.review-viewer img[alt="Reference 2"]')).toHaveAttribute('src', /\/media\/projects\//);
    await expect(modal.locator('.review-viewer img[alt="Reference 2"]')).toHaveAttribute('style', /width:400%/);
    await expect(modal.locator('.review-viewer img[alt="Reference 2"]')).toBeVisible();
  });
}
