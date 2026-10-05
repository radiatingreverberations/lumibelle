import { test, expect } from './fixtures.js';

test('every clip of a long cut shows a frame at Fit', async ({ page, request }) => {
  const { id } = await (await request.get('/fixtures/new')).json();
  const shots = await (await request.post(`/fixtures/${id}/cut-takes`)).json();
  const take = shots.takes[0], shot = shots.shots.find(s => s.id === take.shotId);
  const frames = take.frameCount ?? take.frames.length;
  // Thirty clips of one take: more than the frames a render used to share out from the start.
  const clips = Array.from({ length: 30 }, () => ({ id: crypto.randomUUID(), shotId: shot.id, takeId: take.id, shotTitle: shot.title, takeLabel: 'Take 1',
    frameCount: frames, fps: take.fps, startFrame: 0, endFrameExclusive: frames }));
  const saved = await request.post(`/fixtures/${id}/cut`, { data: clips }); expect(saved.ok(), await saved.text()).toBeTruthy();
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(`/projects/${id}/cut`);
  await expect(page.locator('.cut-heading')).toHaveAttribute('data-interactive', 'true');
  await expect(page.locator('.timeline-clip')).toHaveCount(30);
  await expect.poll(() => page.locator('.timeline-clip').evaluateAll(nodes => nodes.filter(n => !n.querySelector('.timeline-thumbnails img')).length)).toBe(0);
});
