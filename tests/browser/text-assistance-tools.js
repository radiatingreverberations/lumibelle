import { expect } from '@playwright/test';
export const planningComposer = page => page.locator('.ai-assist-dialog').filter({ has: page.getByRole('heading', { name: 'Draft shots', exact: true }) });
export const extractionComposer = page => page.locator('.extraction-dialog');

export async function openPlanning(page) {
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  const scenes = page.getByRole('navigation', { name: 'Project navigation' }).getByRole('link', { name: 'Scenes', exact: true });
  if (await scenes.getAttribute('aria-current') !== 'page') {
    // On narrow screens an open drawer covers the navigation; follow its link instead of clicking it.
    await page.goto(await scenes.getAttribute('href'));
    await expect(scenes).toHaveAttribute('aria-current', 'page');
    await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  }
  const actions = page.locator('.scene-drafting-tools');
  if (!await actions.isVisible() && await page.getByRole('button', { name: 'Show Shots', exact: true }).isVisible())
    await page.getByRole('button', { name: 'Show Shots', exact: true }).click();
  const start = actions.getByRole('button', { name: 'Draft shots', exact: true });
  // After a reload the control can show an earlier request until it learns that request is out of date,
  // then switch to Draft shots. Retry with whichever control is current instead of waiting on a stale one.
  await expect(async () => {
    if (await start.isVisible()) await start.click({ timeout: 2000 });
    else {
      // An earlier request opens its review, which starts over with New breakdown (or New request).
      await actions.locator('.request-action-button').click({ timeout: 2000 });
      await page.getByRole('button', { name: /^(New breakdown|New request)$/ }).first().click({ timeout: 2000 });
    }
    await expect(planningComposer(page)).toBeVisible({ timeout: 2000 });
  }).toPass({ timeout: 20000 });
}

// Return to the production editor after accepting a scene breakdown.
export async function openShotsAfterPlanning(page) {
  await expect(page.locator('.shot-planning-dialog')).toBeHidden();
  await page.goto(await page.locator('.project-tabs').getByRole('link', { name: 'Shots', exact: true }).getAttribute('href'));
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
}

export async function submitPlanning(page) {
  const composer = planningComposer(page);
  await composer.getByRole('button', { name: 'Draft shots', exact: true }).click();
  await expect(composer).toBeHidden();
  await openSubmittedReview(page.locator('.scene-drafting-tools .request-action-button'), page.locator('.shot-planning-dialog'));
}

export async function submitExtraction(page) {
  const composer = extractionComposer(page);
  await expect(composer.locator('.extraction-workflow [aria-current=step]')).toContainText('Choose scenes');
  const original = await composer.elementHandle();
  await composer.getByRole('button', { name: 'Find assets', exact: true }).click();
  await expect(composer).toBeVisible();
  expect(await composer.evaluate((element, first) => element === first, original)).toBe(true);
  await expect(composer.locator('.extraction-workflow [aria-current=step]')).toContainText(/Find assets|Review & add/);
  await expect(page.locator('.ai-assist-dialog').filter({ has: page.getByRole('heading', { name: 'Extract assets from script', exact: true }) })).toHaveCount(0);
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
  const workspace = page.locator('.studio-workspace[data-studio=Script]');
  await expect(workspace).toHaveAttribute('data-ready', 'true');
  // Closing a modal restores the mobile drawer asynchronously; wait before deciding to open it.
  await expect(workspace).toHaveAttribute('data-suspended', 'false');
  if (!await workspace.locator('.workspace-right').isVisible())
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
