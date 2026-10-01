import { expect } from '@playwright/test';
export const planningComposer = page => page.locator('.ai-assist-dialog').filter({ has: page.getByRole('heading', { name: 'Draft shots', exact: true }) });
export const extractionComposer = page => page.locator('.ai-assist-dialog').filter({ has: page.getByRole('heading', { name: 'Extract assets from script', exact: true }) });

export async function openPlanning(page) {
  const actions = page.locator('.shots-library-tools');
  const start = actions.getByRole('button', { name: 'Draft shots', exact: true });
  // After a reload the control can show an earlier request until it learns that request is out of date,
  // then switch to Draft shots. Retry with whichever control is current instead of waiting on a stale one.
  await expect(async () => {
    if (await start.isVisible()) await start.click({ timeout: 2000 });
    else {
      await actions.getByLabel('Request options', { exact: true }).click({ timeout: 2000 });
      await actions.getByRole('button', { name: 'New request', exact: true }).click({ timeout: 2000 });
    }
    await expect(planningComposer(page)).toBeVisible({ timeout: 2000 });
  }).toPass({ timeout: 20000 });
}

export async function submitPlanning(page) {
  const composer = planningComposer(page);
  await composer.getByRole('button', { name: 'Draft shots', exact: true }).click();
  await expect(composer).toBeHidden();
  await openSubmittedReview(page.locator('.shots-library-tools .request-action-button'), page.locator('.shot-planning-dialog'));
}

export async function submitExtraction(page) {
  const composer = extractionComposer(page);
  await composer.getByRole('button', { name: 'Find assets', exact: true }).click();
  await expect(composer).toBeHidden();
  await openSubmittedReview(page.locator('.asset-library-extraction .request-action-button'), page.locator('.extraction-dialog'));
}

async function openSubmittedReview(control, review) {
  await expect(control).toBeEnabled();
  if (!await review.isVisible()) {
    try { await control.click({ timeout: 1000 }); }
    catch (error) {
      // A fast result can open its automatic review while Playwright is clicking.
      if (!await review.isVisible()) throw error;
    }
  }
  await expect(review).toBeVisible();
}

export async function scriptAssist(page) {
  await expect(page.locator('.studio-workspace[data-studio=Script]')).toHaveAttribute('data-ready', 'true');
  if (!await page.locator('.studio-workspace[data-studio=Script] .workspace-right').isVisible())
    await page.getByRole('button', { name: 'Show Assistant', exact: true }).click();
  await expect(page.locator('#script-instructions')).toBeVisible();
}
export async function closeComposer(page) {
  while (await page.locator('.ai-assist-dialog').last().isVisible()) {
    const id = await page.locator('.ai-assist-dialog').last().getAttribute('id');
    const dialog = page.locator(`[id="${id}"]`);
    await dialog.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(dialog).toBeHidden();
  }
}
export async function modelOptions(page, scope = page) {
  if (await page.locator('.script-assistant').count()) await scriptAssist(page);
  const chip = scope.getByRole('button', { name: 'Text model options', exact: true });
  if (await chip.isVisible()) await chip.click();
  else { await scope.locator('.assist-trigger').first().click(); await page.locator('.ai-assist-dialog .assist-composer-model .model-chip').click(); }
  return page.locator('.ai-assist-dialog').last();
}
export async function submitGuidance(page, scope) {
  const composer = page.locator('.ai-assist-dialog').last();
  await composer.getByRole('button', { name: 'Suggest', exact: true }).click();
  await expect(composer).toBeHidden();
  await scope.getByRole('button', { name: /View request|Review changes|View response|Needs attention/ }).click();
  await expect(page.locator('.guidance-dialog')).toBeVisible();
}
