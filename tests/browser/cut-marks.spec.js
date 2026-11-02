import { test, expect } from './fixtures.js';

const state = async (request, id) => (await request.get(`/fixtures/${id}/cut`)).json();
async function setup(page, request) {
    const { id } = await (await request.get('/fixtures/new')).json();
    const response = await request.post(`/fixtures/${id}/cut-takes`);
    expect(response.ok(), await response.text()).toBeTruthy();
    const shots = await response.json();
    await page.goto(`/projects/${id}/cut`);
    await expect(page.locator('.cut-heading')).toHaveAttribute('data-interactive', 'true');
    await page.locator('.cut-workspace-toolbar').getByRole('button', { name: 'Choose takes…', exact: true }).click();
    const chooser = page.locator('.cut-chooser');
    await chooser.getByLabel(`Take for ${shots.shots[1].title}`, { exact: true }).selectOption(shots.takes[2].id);
    await chooser.getByRole('button', { name: /Apply changes/ }).click();
    await expect(chooser).not.toBeVisible();
    await expect.poll(async () => (await state(request, id)).clips.length).toBe(2);
    await expect(page.locator('.cut-player')).toHaveAttribute('data-frame-ready', 'true');
    return { id, shots, cut: await state(request, id) };
}
async function shown(page, frame, clipId) {
    const player = page.locator('.cut-player');
    await expect(player).toHaveAttribute('data-frame-ready', 'true');
    await expect(player).toHaveAttribute('data-frame-index', String(frame));
    if (clipId) await expect(player).toHaveAttribute('data-clip-id', clipId);
    await expect(player.locator('.cut-paused-frame')).toBeVisible();
}
async function sourceFrame(page, frame) {
    // Range inputs publish on input, without a blur or a server round trip.
    await page.getByRole('slider', { name: 'Source frame', exact: true }).evaluate((input, frame) => {
        input.value = String(frame + 1); input.dispatchEvent(new Event('input', { bubbles: true }));
    }, frame);
}

test('End here trims the visible clip, not stale selection, includes that frame and undoes once', async ({ page, request }) => {
    const { id, cut } = await setup(page, request);
    const last = cut.clips[1];
    await page.getByRole('slider', { name: 'Cut position', exact: true }).focus();
    await page.keyboard.press('End'); await shown(page, last.endFrameExclusive - 1, last.id);
    await expect(page.locator('.timeline-clip.selected')).toHaveAttribute('data-clip-id', cut.clips[0].id);
    await page.getByRole('button', { name: 'Previous frame', exact: true }).click();
    await shown(page, last.endFrameExclusive - 2, last.id);
    await expect(page.locator('[data-trim-target]')).toContainText(last.shotTitle);
    await page.getByRole('button', { name: 'End here', exact: true }).click();
    await expect.poll(async () => (await state(request, id)).clips[1].endFrameExclusive).toBe(last.endFrameExclusive - 1);
    await shown(page, last.endFrameExclusive - 2, last.id);
    await expect(page.locator('.timeline-clip.selected')).toHaveAttribute('data-clip-id', last.id);
    await expect(page.getByRole('button', { name: 'End here', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Undo', exact: true }).click();
    await expect.poll(async () => (await state(request, id)).clips).toEqual(cut.clips);
    await page.getByRole('button', { name: 'Redo', exact: true }).click();
    await expect.poll(async () => (await state(request, id)).clips[1].endFrameExclusive).toBe(last.endFrameExclusive - 1);
});

test('full-source frame review restores trimmed frames and supports keyboard precision', async ({ page, request }) => {
    const { id, cut } = await setup(page, request);
    cut.clips[0].startFrame = 5; cut.clips[0].endFrameExclusive = 20;
    await request.post(`/fixtures/${id}/cut`, { data: cut.clips }); await page.reload();
    await shown(page, 5, cut.clips[0].id);
    await page.getByRole('button', { name: 'Preview selected clip', exact: true }).click();
    const slider = page.getByRole('slider', { name: 'Source frame', exact: true });
    await slider.focus(); await page.keyboard.press('Home'); await shown(page, 0);
    await expect(page.getByRole('button', { name: 'End here', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Start here', exact: true }).click();
    await expect.poll(async () => (await state(request, id)).clips[0].startFrame).toBe(0);
    await shown(page, 0);
    await page.getByRole('button', { name: 'Next frame', exact: true }).click(); await shown(page, 1);
    await page.getByRole('button', { name: 'Previous frame', exact: true }).click(); await shown(page, 0);
    await slider.focus(); await page.keyboard.press('End'); await shown(page, cut.clips[0].frameCount - 1);
    await page.getByRole('button', { name: 'End here', exact: true }).click();
    await expect.poll(async () => (await state(request, id)).clips[0].endFrameExclusive).toBe(cut.clips[0].frameCount);
    await shown(page, cut.clips[0].frameCount - 1);
    await expect(page.getByRole('button', { name: 'Next frame', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Return to cut', exact: true }).click();
    await expect(slider).not.toBeVisible();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('button', { name: 'Preview selected clip', exact: true }).click();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expect(slider).toBeVisible();
});

test('failed frame review cannot create a trim and retry restores the exact target', async ({ page, request }) => {
    const { id, cut } = await setup(page, request);
    await page.getByRole('button', { name: 'Preview selected clip', exact: true }).click();
    const url = `**/takes/${cut.clips[0].takeId}/frames/10`;
    await page.route(url, route => route.request().resourceType() === 'fetch' ? route.abort() : route.continue());
    await sourceFrame(page, 10);
    await expect(page.getByRole('button', { name: 'Retry frame', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Start here', exact: true })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'End here', exact: true })).toBeDisabled();
    expect((await state(request, id)).clips).toEqual(cut.clips);
    await page.unroute(url);
    await page.getByRole('button', { name: 'Retry frame', exact: true }).click(); await shown(page, 10);
    await page.getByRole('button', { name: 'End here', exact: true }).click();
    await expect.poll(async () => (await state(request, id)).clips[0].endFrameExclusive).toBe(11);
});

test('replacing a take invalidates full-source review before new marks can be made', async ({ page, request }) => {
    const { id, cut, shots } = await setup(page, request);
    await page.getByRole('button', { name: 'Preview selected clip', exact: true }).click();
    await sourceFrame(page, 10); await shown(page, 10);
    await page.getByRole('combobox', { name: 'Take', exact: true }).selectOption(shots.takes[1].id);
    await expect.poll(async () => (await state(request, id)).clips[0].takeId).toBe(shots.takes[1].id);
    await shown(page, 0, cut.clips[0].id);
    await expect(page.locator('.cut-player')).toHaveAttribute('data-frame-take-id', shots.takes[1].id);
    await expect(page.getByRole('slider', { name: 'Source frame', exact: true })).not.toBeVisible();
    await page.getByRole('button', { name: 'Next frame', exact: true }).click(); await shown(page, 1);
    await page.getByRole('button', { name: 'End here', exact: true }).click();
    await expect.poll(async () => (await state(request, id)).clips[0].endFrameExclusive).toBe(2);
});
