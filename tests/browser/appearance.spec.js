import { test, expect } from './fixtures.js';

const theme = (page, value) => expect(page.locator('html')).toHaveAttribute('data-theme', value);
async function choose(page, value) {
  await page.getByRole('button', { name: 'Appearance', exact: true }).click();
  await page.getByLabel(new RegExp(`^${value} appearance`)).click();
}

test('system preference follows the OS, explicit choice persists and synchronizes across tabs', async ({ page, context }) => {
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.goto('/'); await theme(page, 'dark');
  await page.emulateMedia({ colorScheme: 'light' }); await theme(page, 'light');
  await choose(page, 'Dark'); await theme(page, 'dark');
  await page.reload(); await theme(page, 'dark');
  const second = await context.newPage();
  await second.goto('/'); await theme(second, 'dark');
  await choose(page, 'Light'); await theme(second, 'light');
  await page.emulateMedia({ colorScheme: 'dark' }); await theme(page, 'light');
  await choose(page, 'System'); await theme(page, 'dark');
  await page.emulateMedia({ colorScheme: 'light' }); await theme(page, 'light');
  await second.close();
});

test('saved theme is applied before Blazor starts', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('lumibelle.appearance.v1', 'dark'));
  await page.route('**/_framework/blazor.web.js', route => route.abort());
  await page.goto('/'); await theme(page, 'dark');
  expect(await page.locator('html').evaluate(el => getComputedStyle(el).colorScheme)).toBe('dark');
  expect(await page.locator('body').evaluate(el => getComputedStyle(el).backgroundColor)).toBe('rgb(24, 22, 26)');
});

test('unavailable storage does not prevent changing appearance or editing', async ({ page, request }) => {
  const project = await (await request.get('/fixtures/new')).json();
  await page.addInitScript(() => {
    Storage.prototype.getItem = () => { throw new DOMException('Unavailable', 'SecurityError'); };
    Storage.prototype.setItem = () => { throw new DOMException('Unavailable', 'SecurityError'); };
  });
  await page.goto(`/projects/${project.id}/script`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  await choose(page, 'Dark'); await theme(page, 'dark');
  const editor = page.locator('.script-canvas .tiptap');
  await editor.click(); await page.keyboard.press('Control+End'); await page.keyboard.type(' Storage-free draft.');
  await expect(editor).toContainText('Storage-free draft.');
});

test('theme changes preserve editor DOM, draft, selection and undo history', async ({ page, request }) => {
  const project = await (await request.get('/fixtures/new')).json();
  await page.goto(`/projects/${project.id}/script`);
  await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
  const editor = page.locator('.script-canvas .tiptap');
  const original = await editor.textContent();
  await editor.click(); await page.keyboard.press('Control+End'); await page.keyboard.type(' Theme draft.');
  const node = await editor.elementHandle();
  const selection = await editor.evaluate(el => { const s = getSelection(); return { offset: s.anchorOffset, text: s.anchorNode?.textContent }; });
  await choose(page, 'Dark'); await theme(page, 'dark');
  expect(await node.evaluate(el => el === document.querySelector('.script-canvas .tiptap'))).toBe(true);
  await expect(editor).toContainText('Theme draft.');
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect(editor).toHaveText(original);
  await page.getByRole('button', { name: 'Redo', exact: true }).click();
  await expect(editor).toContainText('Theme draft.');
  // A cross-tab preference notification leaves an active text selection untouched.
  await editor.click(); await page.keyboard.press('Control+End');
  await page.evaluate(() => dispatchEvent(new StorageEvent('storage', { key: 'lumibelle.appearance.v1', newValue: 'light' })));
  await theme(page, 'light');
  expect(await editor.evaluate(el => { const s = getSelection(); return { offset: s.anchorOffset, text: s.anchorNode?.textContent }; })).toEqual(selection);
});

test('compact navigation and themed studios fit desktop, tablet, phone and zoom-equivalent layouts', async ({ page, request }) => {
  const project = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${project.id}/images`);
  await request.post(`/fixtures/${project.id}/approved`);
  await request.post(`/fixtures/${project.id}/production-shot`);
  await request.post(`/fixtures/${project.id}/reference-workspace`);
  test.setTimeout(180000);
  for (const appearance of ['Light', 'Dark']) {
    await page.goto('/dev/design-system'); await choose(page, appearance);
    for (const [width, height] of [[1440,900],[1280,720],[768,1024],[390,844],[720,450]]) {
      await page.setViewportSize({ width, height });
      for (const studio of ['script','assets','shots','cut']) {
        await page.goto(`/projects/${project.id}/${studio}`);
        if(studio !== 'cut') await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready', 'true');
        else await expect(page.locator('.cut-heading')).toHaveAttribute('data-interactive','true');
        await theme(page, appearance.toLowerCase());
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `${studio} ${appearance} ${width} overflow`).toBe(true);
        await expect(page.locator('.project-tabs')).toBeInViewport();
        expect((await page.locator('.studio-projectbar').boundingBox()).height).toBeLessThanOrEqual(width < 760 ? 96 : 48);
        if(studio !== 'cut') {
          if (width < 1100) await expect(page.locator('[data-toggle-pane=right]')).toBeInViewport();
          else await expect(page.locator('[data-toggle-pane=right]')).not.toBeVisible();
          if (width < 760) await expect(page.locator('[data-toggle-pane=left]')).toBeInViewport();
          else await expect(page.locator('[data-toggle-pane=left]')).not.toBeVisible();
        }
        if(width === 1440 || width === 390) await page.screenshot({ animations:'disabled', path:`artifacts/design-${studio}-${appearance.toLowerCase()}-${width}.png` });
      }
    }
  }
});

test('development gallery uses live dialog and review components in both themes', async ({ page }) => {
  await page.goto('/dev/design-system');
  await expect(page.getByRole('heading', { name: 'Lumibelle design system' })).toBeVisible();
  for(const appearance of ['Light', 'Dark']) {
    await choose(page, appearance); await theme(page, appearance.toLowerCase());
    await page.getByRole('button', { name:'Medium', exact:true }).click();
    const dialog=page.getByRole('dialog'); await expect(dialog).toBeVisible();
    expect(await dialog.evaluate(el=>getComputedStyle(el).backgroundColor)).toBe(appearance==='Dark'?'rgb(33, 30, 35)':'rgb(255, 255, 255)');
    await page.screenshot({animations:'disabled',path:`artifacts/design-dialog-${appearance.toLowerCase()}.png`});
    await dialog.getByRole('button',{name:'Cancel',exact:true}).click();
  }
});

test('project pages, provider drafts, activity and media dialogs share both palettes', async ({ page, request }) => {
  test.setTimeout(120000);
  const {id} = await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/images`);
  await request.post(`/fixtures/${id}/reference-workspace`);
  for(const appearance of ['Light','Dark']) {
    await page.goto('/'); await choose(page, appearance);
    for(const width of [1440,390]) {
      await page.setViewportSize({width,height:width===390?844:900});
      for(const [name,url] of [['library','/'],['overview',`/projects/${id}`],['settings',`/projects/${id}/settings`],['ai-settings','/settings/ai'],['trash','/trash']]) {
        await page.goto(url); await expect(page.getByRole('button',{name:'Appearance',exact:true})).toBeEnabled();
        await theme(page,appearance.toLowerCase());
        expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),`${name} ${width}`).toBe(true);
        await page.screenshot({animations:'disabled',path:`artifacts/design-${name}-${appearance.toLowerCase()}-${width}.png`});
      }
      await page.getByRole('button',{name:/^AI activity/}).click();
      await expect(page.locator('.ai-activity-panel')).toBeVisible();
      await page.screenshot({animations:'disabled',path:`artifacts/design-activity-${appearance.toLowerCase()}-${width}.png`});
      await page.keyboard.press('Escape');
      await page.goto(`/projects/${id}/assets`);
      await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true');
      const card=page.locator('.asset-media-card[data-media-kind=Image]').first();
      await card.hover();
      await card.getByRole('button',{name:'Image actions',exact:true}).click();
      await page.getByRole('menuitem',{name:'Edit details…',exact:true}).click();
      const dialog=page.getByRole('dialog',{name:'Image details',exact:true});
      await expect(dialog.getByRole('button',{name:'Save details',exact:true})).toBeInViewport();
      await page.screenshot({animations:'disabled',path:`artifacts/design-image-details-${appearance.toLowerCase()}-${width}.png`});
      await dialog.getByRole('button',{name:'Close',exact:true}).click();
    }
  }
  await page.goto('/settings/ai');
  await page.getByRole('tablist',{name:'Connection providers'}).getByRole('tab',{name:'OpenRouter',exact:true}).click();
  const key=page.locator('#openrouter-key'); await expect(key).toBeVisible();
  await key.fill('local-unsaved-draft');
  await choose(page,'Light'); await expect(key).toHaveValue('local-unsaved-draft');
  await choose(page,'Dark'); await expect(key).toHaveValue('local-unsaved-draft');
});

test('touch layout keeps pane controls reachable with 44px targets', async ({ browser, request, baseURL }) => {
  const {id}=await (await request.get('/fixtures/new')).json();
  await request.post(`/fixtures/${id}/approved`); await request.post(`/fixtures/${id}/production-shot`);
  const context=await browser.newContext({viewport:{width:390,height:844},isMobile:true,hasTouch:true});
  const page=await context.newPage();
  try {
    await page.goto(`${baseURL}/projects/${id}/shots`);
    await expect(page.locator('.studio-workspace')).toHaveAttribute('data-ready','true');
    for(const side of ['left','right']) {
      const toggle=page.locator(`[data-toggle-pane=${side}]`); const box=await toggle.boundingBox();
      expect(box.width).toBeGreaterThanOrEqual(44); expect(box.height).toBeGreaterThanOrEqual(44);
      await toggle.tap(); await expect(page.locator(`.workspace-${side}`)).toBeVisible();
      await page.locator(`.workspace-${side} [data-close-pane]`).tap();
      await expect(toggle).toBeFocused();
    }
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
  } finally { await context.close(); }
});
