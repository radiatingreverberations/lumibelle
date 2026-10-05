import { generateTakes, composeProduction } from './workspace-tools.js';
import { planningComposer, submitPlanning } from './text-assistance-tools.js';
import { test, expect } from './fixtures.js';

const edges = page => page.evaluate(() => {
  const box = selector => document.querySelector(selector).getBoundingClientRect();
  const media = box('.shot-workspace-preview :is(video, .shot-preview-empty)'), text = box('.shot-direction textarea');
  return { top: Math.round(media.top - text.top), bottom: Math.round(media.bottom - text.bottom) };
});

test('the take preview and the action and camera text line up side by side', async ({ page, request }) => {
  test.setTimeout(120000);
  await page.setViewportSize({ width: 1440, height: 1000 });
  const project = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${project.id}/images`);
  await request.post(`/fixtures/${project.id}/approved`);
  await page.goto(`/projects/${project.id}/shots`);
  await expect(page.locator('.shots-heading')).toHaveAttribute('data-interactive', 'true');
  await page.getByRole('button', { name: 'Draft shots', exact: true }).click();
  await expect(planningComposer(page).getByRole('button', { name: 'Draft shots', exact: true })).toBeEnabled();
  await submitPlanning(page);
  await page.locator('.shot-planning-dialog').getByRole('button', { name: 'Add reviewed shots' }).click();
  await expect(page.locator('.shot-preview-empty')).toBeVisible();
  await expect.poll(() => edges(page)).toEqual({ top: 0, bottom: 0 });

  await composeProduction(page);
  await generateTakes(page);
  // While its takes are generated, the shot says so in the shot list.
  const activity = page.locator('.shot-outline-item').first().locator('.shot-generating');
  await expect(activity).toHaveText(/Generating|Queued/);
  const review = page.locator('.shot-review-dialog');
  await expect(review).toBeVisible({ timeout: 20000 });
  await expect(activity).toHaveCount(0);
  await review.getByRole('button', { name: 'Close take review', exact: true }).click();
  await expect(page.locator('.shot-workspace-preview video')).toBeVisible();
  // The shot list shows the shown take's size; Preview is a draft size, marked for regenerating.
  await expect(page.locator('.shot-list-resolution')).toHaveText('0.4 MP');
  await expect(page.locator('.shot-list-resolution')).toHaveClass(/draft/);
  // The caption heads the video as the label heads the text.
  await expect.poll(() => edges(page)).toEqual({ top: 0, bottom: 0 });
  const caption = await page.locator('.shot-preview-caption').boundingBox(), video = await page.locator('.shot-workspace-preview video').boundingBox();
  expect(caption.y + caption.height).toBeLessThanOrEqual(video.y);
});
