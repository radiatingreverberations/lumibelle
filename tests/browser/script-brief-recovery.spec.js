import { test, expect } from './fixtures.js';

for (const mode of ['warning', 'json', 'count']) {
  test(`planning ${mode} uses shared review and correction controls`, async ({ page, request }) => {
    await page.setViewportSize(mode === 'json' ? { width: 390, height: 844 } : { width: 1280, height: 900 });
    const { id } = await (await request.get('/fixtures/new')).json();
    const fixture = await request.post(`/fixtures/${id}/planning-review?stage=brief-${mode}`);
    if (!fixture.ok()) throw new Error((await fixture.text()).slice(0, 1400));
    const { id: jobId } = await fixture.json();
    await page.goto(`/projects/${id}/script?jobId=${jobId}`);
    let dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('button', { name: 'Paste / edit JSON', exact: true })).toBeVisible();
    if (mode === 'warning') {
      await expect(dialog.getByRole('alert')).toContainText('Brief ready for your approval');
      await expect(dialog.getByRole('region', { name: 'Planning warnings' })).toContainText('recommended');
      await expect(dialog.locator('.brief-beats li')).toHaveCount(1);
      await page.screenshot({ path: 'artifacts/brief-size-warning-desktop.png', fullPage: true });
    } else if (mode === 'count') {
      await expect(dialog.getByRole('region', { name: 'Planning warnings' })).toHaveCount(0);
      const continuity = dialog.locator('.brief-context details').filter({ has: page.locator('summary', { hasText: /^Continuity/ }) });
      await expect(continuity.locator('li')).toHaveCount(13);
    } else {
      await expect(dialog.getByRole('region', { name: 'Planning errors' })).toContainText('Invalid JSON at line');
      await expect(dialog.getByRole('button', { name: 'Use brief and continue', exact: true })).toHaveCount(0);
      await dialog.getByRole('button', { name: 'Paste / edit JSON', exact: true }).click();
      const editor = dialog.locator('#corrected-script-json');
      const original = await editor.inputValue();
      await editor.fill(original + '}');
      await page.screenshot({ path: 'artifacts/brief-json-editor-mobile.png', fullPage: true });
      await dialog.getByRole('button', { name: 'Review corrected JSON', exact: true }).click();
      await expect(dialog.locator('#corrected-script-json')).toHaveCount(0);
      await expect(dialog.getByRole('button', { name: 'Use brief and continue', exact: true })).toBeEnabled();
      expect((await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.kind === 'ScriptAssistant')).toHaveLength(1);
      await page.reload();
      dialog = page.getByRole('dialog');
      await expect(dialog).toContainText('Showing your saved JSON correction');
    }
    const continueButton = dialog.getByRole('button', { name: 'Use brief and continue', exact: true });
    await expect(continueButton).toBeInViewport();
    expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
    await continueButton.click();
    await expect(dialog.getByRole('button', { name: 'Apply changes', exact: true })).toBeEnabled({ timeout: 20000 });
    const jobs = (await (await request.get('/fixtures/ai-jobs')).json()).filter(j => j.kind === 'ScriptAssistant');
    expect(jobs).toHaveLength(1);
    expect(jobs[0].id).toBe(jobId);
    expect((await (await request.get(`/fixtures/${id}`)).json()).script.blocks.every(b => b.spans.every(s => !s.text))).toBe(true);
  });
}
