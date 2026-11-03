// Records the manual's clips and screenshots from a running Lumibelle, normally a copy of the
// "The Open Window" demo library (see tools/demo/README.md). Each clip is recorded in parts so that
// generation waits are left out; lumibelle-web's scripts/cut-clip.sh joins the parts.
//
//   node tools/demo/record-manual-media.mjs --url http://127.0.0.1:5192 --route the-open-window --out <folder>
//
// Options: --only take-review,connection,create-prop,edit-image (default: all), --headed,
// --shot <regex> for the take review shot (default "Figures on the lawn"), --wait-minutes (default 20).
// Clips are recorded at 1856x1028, the crop the site's cut script uses for screen recordings, without
// browser chrome; screenshots at 2x. The prop and edit clips queue a Krea 2 job each.

import { chromium } from '@playwright/test';
import { mkdir, writeFile, copyFile, rm } from 'node:fs/promises';
import path from 'node:path';

const args = Object.fromEntries(process.argv.slice(2).reduce((list, item, i, all) => {
  if (item.startsWith('--')) list.push([item.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]);
  return list;
}, []));
const url = (args.url ?? 'http://127.0.0.1:5192').replace(/\/$/, '');
const route = args.route ?? 'the-open-window';
const out = path.resolve(args.out ?? 'artifacts/manual-media');
const only = new Set(String(args.only ?? 'take-review,connection,create-prop,edit-image').split(','));
const shotPattern = new RegExp(args.shot ?? 'Figures on the lawn', 'i');
const waitLimit = Number(args['wait-minutes'] ?? 20) * 60000;
const width = 1856, height = 1028;
const project = rest => `${url}/projects/${route}/${rest}`;
const segments = {}; // clip name -> list of recorded part files

await mkdir(path.join(out, 'raw'), { recursive: true });
const browser = await chromium.launch({ channel: args.channel ?? 'msedge', headless: !args.headed });
try {
  if (only.has('connection')) await connection();
  if (only.has('take-review')) await takeReview();
  if (only.has('create-prop')) await createProp();
  if (only.has('edit-image')) await editImage();
} finally {
  await browser.close();
  await writeFile(path.join(out, 'segments.json'), JSON.stringify(segments, null, 2));
}
console.log(`Parts in ${path.join(out, 'raw')}; segment list in segments.json`);

// --- media --------------------------------------------------------------------------------------

async function connection() {
  const context = await browser.newContext({ viewport: { width: 1200, height: 900 }, deviceScaleFactor: 2, colorScheme: 'light' });
  const page = await context.newPage();
  try {
    await page.goto(`${url}/settings/ai`);
    await page.locator('#ai-tab-connections').click();
    const provider = page.locator('#ai-provider-comfyui');
    if (await provider.count()) await provider.click();
    const form = page.locator('#connection-comfyui-form');
    await form.waitFor();
    await form.getByRole('button', { name: 'Check connection', exact: true }).click();
    await form.getByRole('status').filter({ hasText: /Connected to ComfyUI/ }).waitFor({ timeout: 60000 });
    await form.getByRole('status').filter({ hasText: /Live progress/ }).waitFor({ timeout: 60000 });
    await page.mouse.move(0, 0);
    await page.waitForTimeout(500);
    const file = path.join(out, 'ai-settings-comfyui-connection.png');
    await form.screenshot({ path: file });
    console.log(`screenshot: ${file}`);
  } finally { await context.close(); }
}

async function takeReview() {
  const shot = await shotId(shotPattern);
  await part('shots-take-review', 1, async page => {
    await page.goto(project(`shots?shotId=${shot}`));
    await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
    await page.waitForTimeout(1500);
    await glide(page, page.locator('.shot-list-row[aria-pressed="true"]'));
    await page.waitForTimeout(800);
    await click(page, page.locator('[data-workspace-group=center]').getByRole('tab', { name: /^Takes/ }));
    const card = page.locator('.unified-take-card').first();
    await card.waitFor();
    await page.waitForTimeout(2000);
    await click(page, card.locator('video, img, button').first());
    const player = page.locator('.take-player');
    await player.waitFor();
    await page.waitForTimeout(1200);
    await click(page, player.getByRole('button', { name: 'Play', exact: true }));
    // Takes are only a few seconds long: pause partway, before playback ends by itself.
    await page.waitForTimeout(2800);
    const pause = player.getByRole('button', { name: 'Pause', exact: true });
    if (await pause.isVisible().catch(() => false)) await click(page, pause);
    await page.waitForTimeout(800);
    // Step through single frames with the keyboard, then point at the frame tools.
    for (let i = 0; i < 6; i++) { await page.keyboard.press(']'); await page.waitForTimeout(350); }
    await page.waitForTimeout(600);
    const review = page.getByRole('dialog').filter({ has: player });
    for (const label of [/^Save frame/, /^Continue/, /^Refine from video/]) {
      await glide(page, review.getByText(label).first());
      await page.waitForTimeout(1300);
    }
    await page.keyboard.press('Escape');
    await page.waitForTimeout(1200);
  });
}

async function createProp() {
  const name = 'Tea tray';
  // --prop-results re-records only the second part, once the prop already has its images.
  if (!args['prop-results']) await part('assets-create-prop', 1, async page => {
    await page.goto(project('assets'));
    await page.locator('.studio-workspace[data-studio="Assets"][data-ready="true"]').waitFor({ timeout: 45000 });
    await page.locator('.asset-list .asset-choice').first().waitFor();
    await page.waitForTimeout(1200);
    await click(page, page.getByRole('button', { name: /^Create asset/ }).first());
    const dialog = page.getByRole('dialog').filter({ hasText: /Create an asset/ });
    await dialog.waitFor();
    await page.waitForTimeout(600);
    const kind = dialog.getByLabel('Kind', { exact: true });
    const props = (await kind.locator('option').evaluateAll(list => list.map(o => ({ value: o.value, text: o.textContent })))).find(o => /^Props?$/i.test(o.text.trim()));
    await glide(page, kind);
    if (props) await kind.selectOption(props.value);
    await page.waitForTimeout(600);
    await type(page, dialog.getByLabel('Name', { exact: true }), name);
    await type(page, dialog.getByLabel('Visual notes', { exact: true }), 'An Edwardian silver tea tray', 25);
    await click(page, dialog.getByRole('button', { name: 'Create asset', exact: true }));
    await dialog.waitFor({ state: 'hidden' });
    await page.waitForTimeout(1500);
    const prompt = page.getByLabel('Image prompt', { exact: true });
    await type(page, prompt, 'An Edwardian silver tea tray with a china teapot, two cups and saucers, on a plain background', 18);
    await page.waitForTimeout(600);
    await click(page, page.getByRole('button', { name: 'Generate images', exact: true }));
    await page.waitForTimeout(3000);
  });
  await waitUntil('the prop images', async page => {
    await page.goto(project('assets'));
    await page.locator('.studio-workspace[data-studio="Assets"][data-ready="true"]').waitFor({ timeout: 45000 });
    const row = page.locator('.asset-list-row').filter({ hasText: name }).first();
    await row.locator('.asset-choice').click();
    await page.waitForTimeout(1500);
    // New images arrive as pending takes; done once the request has finished and they are there.
    const idle = !/Encoding|Queued|Sampling|Waiting|Running|Decoding/i.test(await page.locator('.workspace-right').innerText().catch(() => ''));
    return idle && (await page.locator('.workspace-center .reference-image img').count() > 0
      || await page.getByRole('button', { name: /^Show pending takes/ }).isVisible().catch(() => false));
  });
  await part('assets-create-prop', 2, async page => {
    await page.goto(project('assets'));
    await page.locator('.studio-workspace[data-studio="Assets"][data-ready="true"]').waitFor({ timeout: 45000 });
    await click(page, page.locator('.asset-list-row').filter({ hasText: name }).first().locator('.asset-choice'));
    await page.waitForTimeout(1500);
    const pending = page.getByRole('button', { name: /^Show pending takes/ });
    if (await pending.isVisible().catch(() => false)) { await click(page, pending); await page.waitForTimeout(1200); }
    const images = page.locator('.workspace-center .reference-image');
    for (let i = 0; i < Math.min(await images.count(), 3); i++) { await glide(page, images.nth(i)); await page.waitForTimeout(1200); }
    await page.waitForTimeout(2000);
  });
}

async function editImage() {
  const environment = await assetId(/Living Room/i);
  await part('assets-edit-image', 1, async page => {
    await page.goto(project(`assets?assetId=${environment}`));
    await page.locator('.studio-workspace[data-studio="Assets"][data-ready="true"]').waitFor({ timeout: 45000 });
    const image = page.locator('[data-media-kind="Image"] .reference-image').first();
    await image.waitFor();
    await page.waitForTimeout(1200);
    await click(page, image);
    const review = page.locator('.image-review-dialog');
    await review.waitFor();
    await page.waitForTimeout(1500);
    await click(page, review.getByRole('button', { name: 'Edit image', exact: true }));
    await review.waitFor({ state: 'hidden' }).catch(() => {});
    await page.waitForTimeout(1200);
    const prompt = page.getByLabel('Edit instruction', { exact: true });
    await type(page, prompt, 'Make it dusk: deep blue twilight through the open French window, and the lamps lit inside the room.', 18);
    await page.waitForTimeout(600);
    await click(page, page.getByRole('button', { name: 'Generate edited images', exact: true }));
    await page.waitForTimeout(3000);
  });
  await waitUntil('the edited image', async page => {
    await page.goto(project(`assets?assetId=${environment}`));
    await page.locator('.studio-workspace[data-studio="Assets"][data-ready="true"]').waitFor({ timeout: 45000 });
    await page.waitForTimeout(1500);
    return /Completed/i.test(await page.locator('.workspace-right').innerText().catch(() => ''))
      && await page.getByRole('button', { name: /^Review latest (edit|images)/ }).isVisible().catch(() => false);
  });
  await part('assets-edit-image', 2, async page => {
    await page.goto(project(`assets?assetId=${environment}`));
    await page.locator('.studio-workspace[data-studio="Assets"][data-ready="true"]').waitFor({ timeout: 45000 });
    await page.waitForTimeout(1200);
    await click(page, page.getByRole('button', { name: /^Review latest (edit|images)/ }).first());
    const review = page.locator('.image-review-dialog');
    await review.waitFor();
    await page.waitForTimeout(2000);
    const compare = review.getByRole('button', { name: /^Compare with/ }).first();
    if (await compare.isVisible().catch(() => false)) await click(page, compare);
    await page.waitForTimeout(1500);
    // Sweep the comparison wipe across the image, then show both side by side.
    const wipe = review.locator('.review-wipe').first();
    const box = await wipe.boundingBox().catch(() => null);
    if (box) {
      const y = box.y + box.height / 2;
      await page.mouse.move(box.x + box.width * 0.5, y, { steps: 10 });
      await page.mouse.down();
      for (const share of [0.2, 0.85, 0.5]) { await page.mouse.move(box.x + box.width * share, y, { steps: 30 }); await page.waitForTimeout(500); }
      await page.mouse.up();
    }
    await page.waitForTimeout(1200);
    const sideBySide = review.getByRole('button', { name: 'Side by side', exact: true });
    if (await sideBySide.isVisible().catch(() => false)) { await click(page, sideBySide); await page.waitForTimeout(3000); }
  });
}

// --- helpers ------------------------------------------------------------------------------------

async function shotId(pattern) {
  return work(async page => {
    await page.goto(project('shots'));
    await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
    const rows = await page.locator('.shot-list-row').evaluateAll(list => list.map(r => ({ id: r.getAttribute('data-shot-id'), title: r.querySelector('.shot-list-title')?.textContent ?? '' })));
    const hit = rows.find(r => pattern.test(r.title));
    if (!hit) throw new Error(`No shot matches ${pattern}`);
    return hit.id;
  });
}

async function assetId(pattern) {
  return work(async page => {
    await page.goto(project('assets'));
    await page.locator('.asset-list .asset-choice').first().waitFor({ timeout: 45000 });
    const rows = await page.locator('.asset-list-row').evaluateAll(list => list.map(r => ({ id: r.getAttribute('data-asset-id'), text: r.textContent ?? '' })));
    const hit = rows.find(r => pattern.test(r.text));
    if (!hit) throw new Error(`No asset matches ${pattern}`);
    return hit.id;
  });
}

async function part(name, number, run) {
  const context = await browser.newContext({ viewport: { width, height }, recordVideo: { dir: path.join(out, 'raw'), size: { width, height } }, colorScheme: 'light' });
  await context.addInitScript(cursor);
  const page = await context.newPage();
  const started = Date.now();
  try { await run(page); }
  catch (error) { await page.screenshot({ path: path.join(out, `${name}-${number}-failed.png`) }).catch(() => {}); throw new Error(`${name} part ${number}: ${error.message}`); }
  finally {
    const video = page.video();
    await context.close();
    const target = path.join(out, 'raw', `${name}-${number}.webm`);
    await copyFile(await video.path(), target); await rm(await video.path(), { force: true });
    (segments[name] ??= []).push(target);
    console.log(`recorded ${name} part ${number} (${((Date.now() - started) / 1000).toFixed(1)}s)`);
  }
}

async function work(run) {
  const context = await browser.newContext({ viewport: { width, height }, colorScheme: 'light' });
  const page = await context.newPage();
  try { return await run(page); } finally { await context.close(); }
}

async function waitUntil(label, check) {
  const started = Date.now();
  await work(async page => {
    for (;;) {
      if (await check(page).catch(() => false)) return;
      if (Date.now() - started > waitLimit) throw new Error(`Gave up waiting for ${label}`);
      await page.waitForTimeout(5000);
    }
  });
  console.log(`waited ${Math.round((Date.now() - started) / 1000)}s for ${label}`);
}

// A visible cursor, since the recordings have no system pointer.
function cursor() {
  const install = () => {
    if (document.getElementById('demo-cursor')) return;
    const style = document.createElement('style');
    style.textContent = `#demo-cursor{position:fixed;left:0;top:0;width:22px;height:30px;z-index:2147483646;pointer-events:none;transform:translate(-3px,-2px);transition:transform .08s linear}
      #demo-cursor svg{filter:drop-shadow(0 1px 2px rgba(0,0,0,.5))}#demo-cursor.down svg{transform:scale(.88);transform-origin:4px 3px}`;
    document.head.appendChild(style);
    const el = document.createElement('div'); el.id = 'demo-cursor';
    el.innerHTML = '<svg viewBox="0 0 22 30" width="22" height="30"><path d="M3 2 L3 23 L8.5 18 L12.5 27 L16 25.5 L12 16.5 L19 16.5 Z" fill="#fff" stroke="#111" stroke-width="1.5" stroke-linejoin="round"/></svg>';
    document.body.append(el);
    addEventListener('mousemove', e => { el.style.transform = `translate(${e.clientX - 3}px,${e.clientY - 2}px)`; }, true);
    addEventListener('mousedown', () => el.classList.add('down'), true);
    addEventListener('mouseup', () => el.classList.remove('down'), true);
  };
  if (document.body) install(); else document.addEventListener('DOMContentLoaded', install);
}

async function glide(page, locator, steps = 22) {
  const target = locator.first();
  await target.scrollIntoViewIfNeeded({ timeout: 3000 }).catch(() => {});
  const box = await target.boundingBox({ timeout: 3000 }).catch(() => null);
  if (box) await page.mouse.move(box.x + Math.min(box.width / 2, 160), box.y + Math.min(box.height / 2, 40), { steps });
}
async function click(page, locator) { await glide(page, locator); await page.waitForTimeout(200); await locator.first().click(); await page.waitForTimeout(350); }
async function type(page, locator, text, delay = 35) { await click(page, locator); await locator.first().fill(''); await locator.first().pressSequentially(text, { delay }); await page.waitForTimeout(300); }
