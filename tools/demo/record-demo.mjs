// Makes a film from a short story in Lumibelle, from a blank project, and records the demo while it does.
//
// Each phase drives the real UI. The actions are recorded; the long AI waits (analysis, drafting, images,
// reels, takes) happen off camera and appear in the demo as a badge with the real elapsed time, over the frozen last frame before it.
//
// With --serve, the recorder runs its own Lumibelle on a scratch library in the output folder and saves a
// checkpoint of that library before every phase. --from <phase> restores the checkpoint and continues from
// there; --retake <phase> re-records just that phase and keeps everything else. A review phase (outline,
// screenplay, library, ...) starts after its AI job has finished, so retaking it calls no model at all.
// See tools/demo/README.md.
//
//   node tools/demo/record-demo.mjs --serve web --source story.txt --text-model gemma
//
// Options:
//   --serve web|mock  Run Lumibelle here: "web" is the Release build with your AI settings copied into a
//                     scratch library; "mock" is the browser-test host with mocked models. Without --serve,
//                     drive --url as it is, with no checkpoints.
//   --url             Address (default http://127.0.0.1:5191).
//   --seed <dir>      Data folder whose ai-settings.json and generation-setups.json seed the scratch library
//                     (default App_Data). Nothing else is read from it.
//   --source          UTF-8 text of the story (required for the source phase).
//   --name            Project name (default "The Open Window").
//   --text-model      Regex for the text model made the project default (default "gemma").
//   --text-label      The text model's name on the wait badges (default "Gemma 4").
//   --set-model       Regex: make this the project's text model before resuming (with --from).
//   --vision-model    Regex for a model that reads images, where an assistant asks for one (default: text model).
//   --shot            Regex for the shot featured on camera (default "lawn|window|hunters").
//   --environment     Regex for the location the reel is made from (default: the living or drawing room).
//   --scenes 2,6,7    Draft shots only for these scenes, numbered from 1 in script order (default: all).
//   --film-shots 2,8  Give references, prompts and takes only to these shots, numbered from 1 in list order.
//   --out             Output folder (default artifacts/demo): state.json, clips, checkpoints, library, demo.mp4.
//   --from <phase>    Restore that phase's checkpoint (with --serve) and run from it, keeping earlier clips.
//   --until <phase>   Stop after this phase.
//   --retake <phase>  Restore, re-record only this phase, keep all other clips, and assemble.
//   --size WxH        Viewport and video size (default 1920x1080).
//   --channel         Browser channel (default msedge). --headed shows the browser.
//   --wait-minutes    Longest wait for one AI job (default 240).
//   --continue        With --from: that phase's job is already running or paused in the library as it is. Do not
//                     restore a checkpoint or record the phase's start again; rejoin its wait (resuming a paused
//                     screenplay batch) and keep the clips already recorded for it.
//   --started-at      With --continue: when that job really started (ISO time or HH:MM today), for its badge.
//   --film-tail       Seconds of the exported film at the end of the demo (default 15).
//   --skip-ffmpeg     Record only; do not assemble demo.mp4.
//   --assemble-only   Skip the phases; rebuild demo.mp4 from the clips already recorded.
//   --omit a,b,c      Phases whose clips are left out of demo.mp4 (default: the job-starting ones,
//                     create,script,assets,reel-render,keyframes,shots,prompts,takes). --omit "" keeps all.
//   --speed           Playback speed of the UI recordings (default 1.75). The film plays at normal speed.
//   --card-seconds    Length of each elapsed-time badge (default 2). --cold-open seconds (default 5).
//   --slow-note       Third line on the first badge of 20 minutes or more (default: a note on local generation speed;
//                     --slow-note "" leaves it out). The first such badge also stays on screen a second longer.
//   --keep-serving    Leave the scratch Lumibelle running at the end, to look around.
//
// The captions and cursor are drawn inside the page, so they are in the recording. Cards are rendered as
// screenshots and turned into still clips by ffmpeg; encoded parts are cached between runs.

import { chromium } from '@playwright/test';
import { spawn, spawnSync } from 'node:child_process';
import { mkdir, rm, writeFile, readFile, copyFile, stat, readdir, link } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const PHASES = ['create', 'source', 'outline', 'script', 'screenplay', 'assets', 'library', 'images', 'approve',
  'reel', 'reel-render', 'keyframes', 'picks', 'switch', 'scenes', 'coverage', 'shots', 'references', 'prompts', 'prompt-review',
  'takes', 'takes-review', 'refine', 'compare', 'cut'];
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const args = parse(process.argv.slice(2));
const url = (args.url ?? 'http://127.0.0.1:5191').replace(/\/$/, '');
const out = path.resolve(args.out ?? path.join(repo, 'artifacts/demo'));
const library = path.join(out, 'library');
const checkpoints = path.join(out, 'checkpoints');
const [width, height] = (args.size ?? '1920x1080').split('x').map(Number);
const projectName = args.name ?? 'The Open Window';
const textModel = new RegExp(args['text-model'] ?? 'gemma', 'i');
const writer = args['text-label'] ?? 'Gemma 4'; // named on the wait badges
const visionModel = new RegExp(args['vision-model'] ?? args['text-model'] ?? 'gemma', 'i'); // for assistants that read pictures
const featuredPattern = new RegExp(args.shot ?? 'lawn|window|hunters', 'i');
const waitLimit = Number(args['wait-minutes'] ?? 240) * 60000;
const continuing = Boolean(args.continue);
if (continuing && !args.from) fail('--continue needs --from <phase>.');
const retake = args.retake;
const from = retake ?? args.from ?? PHASES[0];
const until = retake ?? args.until ?? PHASES.at(-1);
for (const phase of [from, until]) if (!PHASES.includes(phase)) fail(`Unknown phase "${phase}". Phases: ${PHASES.join(', ')}`);
if (args.serve && !['web', 'mock'].includes(args.serve)) fail('--serve is "web" or "mock".');

const captions = {
  title: ['Lumibelle', 'An open-source studio for growing a story into a film.'],
  create: 'A new project. Nothing in it yet.',
  source1: 'Start from any text: Saki’s “The Open Window”, 1914, public domain.',
  source2: 'It reads the story in sections and proposes an outline, every beat linked to the original passage.',
  script1: 'Every sequence of the outline is drafted into screenplay, then checked against the source.',
  script2: 'A screenplay, written from the story.',
  assets1: 'Characters, locations and props come out of the script.',
  assets2: 'You review what goes into the library.',
  images1: 'Reference images for every asset, in one batch: Krea 2 in ComfyUI, on your own GPU.',
  images2: 'You choose which takes become references.',
  reel1: 'From one picture of the drawing room, an environment reel: MiniMax H3 travels around the set.',
  reel2: 'One continuous camera move, so every angle shows the same room.',
  reel3: 'Keyframes are picked from the reel. Every shot can now see the room from the right side.',
  scenes1: 'Each scene is drafted into shots, using the keyframes to understand the set.',
  scenes2: 'You review the coverage before it becomes shots.',
  shots1: 'Each shot gets its references: the cast, and the room from the keyframes.',
  prompts1: 'The prompt is composed from the actual reference pictures.',
  takes1: 'Takes are MiniMax H3, generated in ComfyUI, with sound. Quick size for drafts.',
  takes2: 'Same faces, same room, same window, shot after shot.',
  takes3: 'Finals are refined in one overnight batch, reusing the frames picked for consistency.',
  refine1: 'Refine from video: the draft guides a full-size render of the same shot.',
  compare1: 'Draft and final side by side: the same performance, at full size.',
  cut1: 'Cut the takes together and export.',
  switch1: 'A local 31B model keeps everything on your own machine, but writes at about 7 tokens per second here.',
  switch2: 'Switch to a fast hosted open model at any point, and carry on where you are.',
  end: ['Lumibelle is open source (AGPL-3.0)', 'github.com/radiatingreverberations/lumibelle · lumibelle.ai', args['end-models'] ?? 'Open-weight models: MiniMax H3 and Krea 2 in ComfyUI · Gemma 4 31B · DeepSeek 4.1 Flash'],
};

await mkdir(path.join(out, 'raw'), { recursive: true });
const statePath = path.join(out, 'state.json');
const state = existsSync(statePath) ? JSON.parse(await readFile(statePath, 'utf8')) : { clips: [] };
const order = phase => PHASES.indexOf(phase);
// Rerunning phases replaces their clips. A retake keeps the clips of every other phase.
// Continuing a phase keeps the clips it recorded before its wait.
if (!args['assemble-only']) state.clips = state.clips.filter(c => order(c.step) >= 0 && (retake ? c.step !== retake : order(c.step) < order(from) || continuing && c.step === from && c.kind === 'webm'));
const save = () => writeFile(statePath, JSON.stringify(state, null, 2));
let server = null;
let step = null;
let current = null; // the recorded section, for trimming its loading time
let rejoining = continuing; // skip recording until the continued phase reaches its wait
let batchResumes = 0; // times the screenplay wait resumed a paused batch
let sequence = Math.max(0, ...state.clips.map(c => c.sequence ?? 0));

let browser = null;

// Runs from the end of the file, once every constant below is initialized.
async function main() {
  if (args.serve && !args['assemble-only']) {
    if (continuing) console.log(`Continuing "${from}" with the library as it is`);
    else if (from === PHASES[0] && !retake) await freshLibrary();
    else if (existsSync(path.join(checkpoints, from))) await restore(from);
    else if (!existsSync(library)) fail(`No checkpoint for "${from}" and no library to continue from.`);
    else console.warn(`No checkpoint for "${from}": continuing from the library as it is.`);
    await startServer();
  }
  browser = await chromium.launch({ channel: args.channel ?? 'msedge', headless: !args.headed });
  if (args['assemble-only']) { try { await assemble(); } finally { await browser.close(); } return; }
  try {
    // --set-model switches the project's default text model before resuming, for the phases still to run.
    if (args['set-model'] && state.route) await work(async page => {
      await page.goto(project('source'));
      await ready(page, 'Source');
      await chooseTextModel(page, new RegExp(args['set-model'], 'i'));
    });
    for (const name of PHASES.slice(order(from), order(until) + 1)) {
      step = name;
      // The checkpoint of the first phase is the one just restored; save every later one as it is reached.
      if (args.serve && (name !== from || !existsSync(path.join(checkpoints, name)))) await checkpoint(name);
      console.log(`\n== ${name}`);
      const started = Date.now();
      await phases[name]();
      console.log(`== ${name} done in ${minutes(Date.now() - started)}`);
      await save();
    }
    if (!args['skip-ffmpeg'] && (until === PHASES.at(-1) || retake)) await assemble();
  } finally {
    await save();
    await browser.close();
    if (server && !args['keep-serving']) await stopServer();
    else if (server) console.log(`Lumibelle is still running at ${url} (pid ${server.pid}).`);
  }
}

// --- phases --------------------------------------------------------------------------------------

const phases = {
  async create() {
    await record('create', async page => {
      await page.goto(`${url}/`);
      await page.getByRole('heading', { name: 'Your projects', exact: true }).waitFor();
      await mark(page);
      await caption(page, captions.create);
      await click(page, page.getByRole('button', { name: 'New project', exact: true }));
      const dialog = page.getByRole('dialog').filter({ has: page.getByRole('heading', { name: 'New project', exact: true }) });
      await type(page, dialog.getByLabel('Project name', { exact: true }), projectName);
      await dialog.getByRole('combobox', { name: 'Project visual style', exact: true }).selectOption('Photographic').catch(() => {});
      await type(page, dialog.getByLabel('Project visual style direction', { exact: true }),
        'England, 1914. Edwardian country house, natural window light, muted greens and browns, period costume.', 8);
      await click(page, dialog.getByRole('button', { name: 'Create project', exact: true }));
      await page.waitForURL(/\/projects\/[^/]+/, { timeout: 30000 });
      state.route = page.url().match(/\/projects\/([^/?#]+)/)[1];
      await beat(1500);
    });
    console.log(`Project route: ${state.route}`);
  },

  async source() {
    if (!args.source) fail('Pass --source <story.txt>.');
    const text = (await readFile(path.resolve(args.source), 'utf8')).trim();
    await work(async page => {
      await page.goto(project('source'));
      await ready(page, 'Source');
      await chooseTextModel(page);
    });
    await record('source', async page => {
      await page.goto(project('source'));
      await ready(page, 'Source');
      await mark(page);
      await caption(page, captions.source1);
      await type(page, page.getByLabel('Source title', { exact: true }), 'The Open Window');
      const original = page.getByLabel('Original text', { exact: true });
      await click(page, original);
      await original.fill(text);
      await beat(1200);
      await type(page, page.getByLabel('Adaptation direction', { exact: true }),
        'Keep Saki’s dry tone and the twist. One afternoon at the Sappletons’ house, Framton’s nerves, Vera’s story, the return at dusk.', 6);
      await click(page, page.getByRole('button', { name: 'Save', exact: true }));
      await click(page, page.getByRole('button', { name: 'Analyze source', exact: true }));
      await caption(page, captions.source2);
      await beat(4000);
    });
    await waitFor(`${writer} read the story and proposed an outline`, project('source'), async page =>
      page.getByRole('button', { name: 'Accept outline', exact: true }).isEnabled({ timeout: 1000 }).catch(() => false));
  },

  async outline() {
    await record('outline', async page => {
      await page.goto(project('source'));
      await ready(page, 'Source');
      const left = page.locator('.workspace-left');
      await left.getByRole('tab', { name: 'Outline', exact: true }).click();
      await mark(page);
      await caption(page, captions.source2);
      const sequences = left.getByRole('button', { name: /^\d+\.\d+ / });
      for (let i = 0; i < Math.min(await sequences.count(), 2); i++) { await click(page, sequences.nth(i)); await beat(2200); }
      await click(page, page.getByRole('button', { name: 'Accept outline', exact: true }));
      await beat(1500);
    });
  },

  async script() {
    await record('script', async page => {
      await page.goto(project('script'));
      await ready(page, 'Script');
      await mark(page);
      await caption(page, captions.script1);
      await page.getByLabel('Action', { exact: true }).selectOption('draft-remaining');
      await beat(800);
      await click(page, page.getByRole('button', { name: 'Draft remaining sequences', exact: true }));
      await beat(5000);
    });
    await waitFor(`${writer} drafted and checked the screenplay`, project('script'), async page => {
      const batch = page.getByRole('region', { name: 'Screenplay batch', exact: true });
      const text = await batch.textContent({ timeout: 1000 }).catch(() => '');
      if (/Batch complete/i.test(text)) return true;
      // A batch stopped by hand keeps the sequences it added: with no batch left and scenes in the script, it is done.
      if (!text && await page.locator('.script-outline-item').count() > 0 && await batch.count() === 0) return true;
      // A batch pauses when a request is interrupted (a restart, say) or a review stays unresolved. Resume it
      // a few times before giving up; completed sequences are kept either way.
      // The batch controls sit in the assistant's footer, outside the batch region.
      const resume = page.getByRole('button', { name: /^(Retry and continue|Resume batch|Use brief and resume batch)$/ });
      if (await resume.count() > 0 && await resume.first().isEnabled().catch(() => false)) {
        if (++batchResumes > 4) throw new Error(`The screenplay batch keeps stopping: ${text.trim().slice(0, 300)}`);
        console.warn(`  the screenplay batch paused; choosing "${(await resume.first().innerText()).trim()}" (${batchResumes} of 4)`);
        await resume.first().click();
        await beat(3000);
        return false;
      }
      if (/needs attention|stopped/i.test(text)) throw new Error(`The screenplay batch stopped: ${text.trim().slice(0, 300)}`);
      return false;
    });
  },

  async screenplay() {
    await record('screenplay', async page => {
      await page.goto(project('script'));
      await ready(page, 'Script');
      await mark(page);
      await caption(page, captions.script2);
      await glide(page, page.locator('.workspace-center'));
      await wheel(page, 10, 3500, 1400);
      const scenes = page.locator('.script-outline-item .outline-title');
      if (await scenes.count() > 1) { await click(page, scenes.nth(1)); await beat(2000); }
    });
  },

  async assets() {
    await record('assets', async page => {
      await page.goto(project('assets'));
      await ready(page, 'Assets');
      await mark(page);
      await caption(page, captions.assets1);
      await click(page, page.locator('.asset-library-extraction .request-action-button'));
      const dialog = page.locator('.extraction-dialog');
      await dialog.getByRole('button', { name: 'Find assets', exact: true }).waitFor();
      await beat(1200);
      await click(page, dialog.getByRole('button', { name: 'Find assets', exact: true }));
      await beat(5000);
    });
    await waitFor(`${writer} found the cast, locations and props`, project('assets'), async page => {
      await ready(page, 'Assets', 0);
      const dialog = page.locator('.extraction-dialog');
      if (!await dialog.isVisible()) await page.locator('.asset-library-extraction .request-action-button').click({ timeout: 2000 }).catch(() => {});
      const done = await dialog.locator('.apply-extraction').isEnabled({ timeout: 1500 }).catch(() => false);
      // The dialog stays open while it streams, so the time-lapse shows the asset cards arriving.
      return done;
    });
  },

  async library() {
    await record('library', async page => {
      await page.goto(project('assets'));
      await ready(page, 'Assets');
      await click(page, page.locator('.asset-library-extraction .request-action-button'));
      const dialog = page.locator('.extraction-dialog');
      await dialog.locator('.apply-extraction').waitFor();
      await mark(page);
      await caption(page, captions.assets2);
      await glide(page, dialog);
      await wheel(page, 6, 3000, 900);
      await click(page, dialog.locator('.apply-extraction'));
      await dialog.waitFor({ state: 'hidden', timeout: 30000 });
      await page.locator('.asset-list .asset-choice').first().waitFor();
      await beat(2500);
    });
  },

  async images() {
    await record('images', async page => {
      await page.goto(project('assets'));
      await ready(page, 'Assets');
      await page.locator('.asset-list .asset-choice').first().waitFor();
      await mark(page);
      await caption(page, captions.images1);
      await click(page, page.locator('.bulk-assets-launch'));
      const dialog = page.locator('.asset-batch-dialog');
      await dialog.waitFor();
      const all = dialog.getByRole('button', { name: 'Select all ready items', exact: true });
      if (await all.isVisible()) await click(page, all);
      await beat(1000);
      await click(page, dialog.getByRole('button', { name: 'Review generation plan' }));
      await beat(2500);
      await click(page, dialog.getByRole('button', { name: 'Start batch', exact: true }));
      await beat(4000);
    });
    await waitFor('Krea 2 made reference images for every asset', project('assets'), async page => {
      await ready(page, 'Assets', 0);
      const dialog = page.locator('.asset-batch-dialog');
      if (!await dialog.isVisible()) await page.locator('.bulk-assets-launch').click({ timeout: 2000 }).catch(() => {});
      const status = await dialog.locator('.bulk-run-status').textContent({ timeout: 2000 }).catch(() => '');
      if (/paused|failed/i.test(status) && !/complete/i.test(status)) throw new Error(`The image batch stopped: ${status.trim()}`);
      return /Batch complete/i.test(status);
    });
  },

  async approve() {
    await record('approve', async page => {
      await page.goto(project('assets'));
      await ready(page, 'Assets');
      await click(page, page.locator('.bulk-assets-launch'));
      const dialog = page.locator('.asset-batch-dialog');
      await dialog.locator('.bulk-result-card').first().waitFor();
      await mark(page);
      await caption(page, captions.images2);
      // The first candidate of every asset becomes its reference; the others stay as takes.
      const cards = dialog.locator('.bulk-result-card');
      const count = await cards.count();
      for (let i = 0; i < count; i++) {
        const approve = cards.nth(i).getByRole('button', { name: 'Use as reference', exact: true }).first();
        if (!await approve.isVisible().catch(() => false)) continue;
        if (i < 3) await click(page, approve); else { await approve.scrollIntoViewIfNeeded(); await approve.click(); await beat(250); }
      }
      await beat(1500);
      await dialog.getByRole('button', { name: 'Close', exact: true }).first().click();
      await beat(1000);
      await click(page, page.locator('.asset-list .asset-choice').first());
      await beat(3000);
    });
  },

  async reel() {
    // The environment for the reel: the drawing room if the extraction named one, otherwise the first location.
    state.environment = await work(async page => {
      await page.goto(project('assets'));
      await ready(page, 'Assets');
      await page.locator('.asset-list .asset-choice').first().waitFor();
      const environments = page.locator('.asset-list-row').filter({ hasText: /Environment/ });
      // Without a location (the mocked host extracts only a character), any asset still makes a reel.
      const rows = await environments.count() > 0 ? environments : page.locator('.asset-list-row');
      if (rows !== environments) console.warn('No environment asset: making the reel from the first asset.');
      const names = await rows.locator('.asset-choice').evaluateAll(list => list.map(b => (b.innerText.split('\n')[0] ?? '').trim()));
      // Prefer the room where the story plays, then any interior, then the house; --environment overrides.
      const preferences = [args.environment ? new RegExp(args.environment, 'i') : null, /living room|drawing room|sitting room|parlou?r/i, /room|interior|hall/i, /house|sappleton/i].filter(Boolean);
      const match = preferences.map(p => names.findIndex(n => p.test(n))).find(i => i >= 0);
      const index = match ?? 0;
      const id = await rows.nth(index).getAttribute('data-asset-id');
      if (!id) throw new Error('No environment asset to make a reel from.');
      return { id, name: names[index] };
    });
    console.log(`Environment: ${state.environment.name}`);
    await record('reel', async page => {
      await page.goto(project(`assets?assetId=${state.environment.id}`));
      await ready(page, 'Assets');
      const image = page.locator('[data-media-kind="Image"] .reference-image').first();
      await image.waitFor();
      await mark(page);
      await caption(page, captions.reel1);
      await click(page, image);
      const review = page.locator('.image-review-dialog');
      await click(page, review.getByRole('button', { name: 'Create reel from this image', exact: true }));
      await page.locator('.reel-tools').waitFor();
      await click(page, page.locator('.reel-tools').getByRole('button', { name: 'Prompt', exact: true }));
      const setup = page.locator('.reel-setup-dialog');
      await setup.locator('[data-prompt-ready="true"]').waitFor();
      await glide(page, setup.locator('.environment-coverage'));
      await beat(2500);
      await openAssist(page, setup.getByRole('button', { name: 'Reel assistance', exact: true }));
      await click(page, page.locator('.ai-assist-dialog').getByRole('button', { name: 'Compose pair', exact: true }));
      await beat(3000);
    });
    await waitFor(`${writer} composed the camera move`, project(`assets?assetId=${state.environment.id}&view=reels`), async page => {
      await ready(page, 'Assets', 0);
      const setup = page.locator('.reel-setup-dialog');
      if (!await setup.isVisible()) await page.locator('.reel-tools').getByRole('button', { name: 'Prompt', exact: true }).click({ timeout: 2000 }).catch(() => {});
      const prompt = await setup.getByRole('textbox', { name: 'H3 prompt', exact: true }).textContent({ timeout: 2000 }).catch(() => '');
      // A composed pair either fills the prompt or waits under Review changes; the next phase applies it.
      return prompt.trim().length > 40 || await setup.getByRole('button', { name: 'Review changes', exact: true }).isVisible().catch(() => false);
    });
  },

  async 'reel-render'() {
    await record('reel-render', async page => {
      await page.goto(project(`assets?assetId=${state.environment.id}&view=reels`));
      await ready(page, 'Assets');
      const tools = page.locator('.reel-tools');
      await tools.waitFor();
      await mark(page);
      await caption(page, captions.reel2);
      await click(page, tools.getByRole('button', { name: 'Prompt', exact: true }));
      const setup = page.locator('.reel-setup-dialog');
      const promptBox = setup.getByRole('textbox', { name: 'H3 prompt', exact: true });
      await setup.locator('[data-prompt-ready="true"]').waitFor({ timeout: 15000 }).catch(() => {});
      const review = setup.getByRole('button', { name: 'Review changes', exact: true });
      if ((await promptBox.textContent().catch(() => '')).trim().length < 40 && await review.isVisible().catch(() => false)) {
        // The composed pair waits for review: open it and apply it.
        await click(page, review);
        // "Apply anyway" when the review carries a note that does not block generation.
        const apply = page.getByRole('button', { name: /^(Apply changes|Apply anyway)$/ }).last();
        await apply.waitFor({ timeout: 15000 });
        await beat(2000);
        await click(page, apply);
        await beat(1000);
      }
      await glide(page, promptBox);
      await beat(3000);
      await click(page, setup.getByRole('button', { name: 'Done', exact: true }));
      await quick(tools.getByLabel('Reel resolution', { exact: true }));
      await click(page, tools.getByRole('button', { name: 'Generate reel', exact: true }));
      await beat(3000);
    });
    await waitFor('MiniMax H3 rendered the environment reel', project(`assets?assetId=${state.environment.id}`), async page => {
      await ready(page, 'Assets', 0);
      return await page.locator('[data-media-kind="Reel"]').count() > 0;
    });
  },

  async keyframes() {
    await record('keyframes', async page => {
      await page.goto(project(`assets?assetId=${state.environment.id}`));
      await ready(page, 'Assets');
      const item = page.locator('[data-media-kind="Reel"]').first();
      await item.waitFor();
      await mark(page);
      await caption(page, captions.reel2);
      await click(page, item.locator('.reference-image'));
      await beat(7000);
      await page.keyboard.press('Escape');
      await beat(600);
      await click(page, item.getByRole('button', { name: /^Actions for / }));
      await click(page, page.getByRole('menuitem', { name: 'Manage keyframes…', exact: true }));
      const editor = page.locator('.keyframe-editor');
      await editor.waitFor();
      await click(page, editor.getByRole('button', { name: 'Suggest reel views', exact: true }));
      const assist = page.getByRole('dialog', { name: 'Suggest reel views', exact: true });
      await click(page, assist.getByRole('button', { name: 'Suggest views', exact: true }));
      await beat(2500);
    });
    await waitFor(`${writer} picked keyframes from the reel`, project(`assets?assetId=${state.environment.id}`), async page => {
      await ready(page, 'Assets', 0);
      const editor = page.locator('.keyframe-editor');
      if (!await editor.isVisible()) {
        await page.locator('[data-media-kind="Reel"]').first().getByRole('button', { name: /^Actions for / }).click({ timeout: 2000 }).catch(() => {});
        await page.getByRole('menuitem', { name: 'Manage keyframes…', exact: true }).click({ timeout: 2000 }).catch(() => {});
      }
      const review = editor.getByRole('region', { name: 'Review suggested views', exact: true });
      if (await review.locator('.suggested-view-list article').count().catch(() => 0) > 0) return true;
      // Finished suggestions can also wait behind Review changes; the next phase opens them.
      if (await editor.getByRole('button', { name: 'Review changes', exact: true }).isVisible().catch(() => false)) return true;
      // A failed suggestion falls back to Quick picks in the next phase.
      return await review.getByRole('alert').count().catch(() => 0) > 0 || await editor.getByRole('button', { name: 'Needs attention', exact: true }).isVisible().catch(() => false);
    }, { softAfter: 10 * 60000 });
  },

  async picks() {
    await record('picks', async page => {
      await page.goto(project(`assets?assetId=${state.environment.id}`));
      await ready(page, 'Assets');
      const item = page.locator('[data-media-kind="Reel"]').first();
      await item.getByRole('button', { name: /^Actions for / }).click();
      await page.getByRole('menuitem', { name: 'Manage keyframes…', exact: true }).click();
      const editor = page.locator('.keyframe-editor');
      await editor.waitFor();
      const review = editor.getByRole('region', { name: 'Review suggested views', exact: true });
      const reviewChanges = editor.getByRole('button', { name: 'Review changes', exact: true });
      if (await reviewChanges.isVisible().catch(() => false) && !await review.isVisible().catch(() => false)) await reviewChanges.click();
      const suggested = await review.locator('.suggested-view-list article').first().waitFor({ timeout: 8000 }).then(() => true, () => false);
      await mark(page);
      await caption(page, captions.reel3);
      if (suggested) {
        await glide(page, review.locator('.suggested-view-list article').first());
        await beat(2500);
        await click(page, review.getByRole('button', { name: 'Replace picks with selected views', exact: true }));
      } else {
        console.warn('No suggested views: using Quick picks.');
        const tools = editor.locator('.keyframe-selection-tools');
        if (!await tools.evaluate(d => d.open)) await click(page, tools.locator(':scope > summary'));
        await click(page, tools.getByRole('button', { name: 'Quick picks', exact: true }));
      }
      await beat(2500);
      const save = editor.getByRole('button', { name: 'Save keyframes', exact: true });
      if (!await save.isEnabled()) {
        // Nothing usable was found automatically: add four evenly spaced frames by hand.
        console.warn('No automatic picks: adding four frames by hand.');
        const timeline = editor.getByLabel('Frame timeline', { exact: true });
        const last = Number(await timeline.getAttribute('max') ?? 96);
        for (const share of [0.08, 0.35, 0.62, 0.9]) {
          await timeline.fill(String(Math.round(last * share)));
          await beat(600);
          await click(page, editor.getByRole('button', { name: 'Add current frame', exact: true }));
        }
      }
      await click(page, save);
      await beat(2000);
    });
  },

  // Shows the text model being switched mid-project. --switch-to picks the model (default: DeepSeek).
  async switch() {
    const target = new RegExp(args['switch-to'] ?? 'Refuseless.*DeepSeek', 'i');
    await record('switch', async page => {
      await page.goto(project('source'));
      await ready(page, 'Source');
      await mark();
      await caption(page, captions.switch1);
      await beat(2500);
      await click(page, page.getByRole('button', { name: 'Text model options', exact: true }).first());
      const dialog = page.getByRole('dialog', { name: 'Text model', exact: true });
      const select = dialog.getByRole('combobox', { name: 'Text model', exact: true });
      await select.locator('option').first().waitFor({ state: 'attached', timeout: 15000 });
      await beat(800);
      const options = await select.locator('option').allTextContents();
      const from = options.find(o => textModel.test(o) && !/^Use project default/i.test(o));
      const to = options.find(o => target.test(o) && !/^Use project default/i.test(o));
      if (!to) throw new Error(`No model matches ${target}`);
      await glide(page, select);
      if (from) { await select.selectOption({ label: from }); await beat(1500); }
      await caption(page, captions.switch2);
      await select.selectOption({ label: to });
      await beat(2000);
      const projectDefault = dialog.getByRole('button', { name: 'Set as project default', exact: true });
      if (await projectDefault.isVisible().catch(() => false)) await click(page, projectDefault);
      else await click(page, dialog.getByRole('button', { name: 'Done', exact: true }));
      await beat(1500);
    });
  },

  async scenes() {
    state.scenes = await work(async page => {
      await page.goto(project('scenes'));
      await page.locator('.scene-outline-item').first().waitFor({ timeout: 30000 });
      return page.locator('.scene-outline-item').evaluateAll(items => items.map(i => i.getAttribute('data-scene-id')));
    });
    // --scenes 2,6,7 keeps those scenes (numbered from 1 in script order); the others get no shots.
    if (args.scenes) {
      const keep = String(args.scenes).split(',').map(n => Number(n.trim())).filter(n => n >= 1);
      const all = state.scenes;
      state.scenes = keep.map(n => all[n - 1]).filter(Boolean);
      console.log(`Drafting scenes ${keep.join(', ')} of ${all.length}`);
    }
    console.log(`${state.scenes.length} scenes`);
    let started = Date.now();
    for (const [i, sceneId] of state.scenes.entries()) {
      const run = async page => {
        await page.goto(project(`scenes?sceneId=${sceneId}`));
        await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
        if (i === 0) { await mark(page); await caption(page, captions.scenes1); }
        await showRight(page, 'Show Scene environment');
        await click(page, page.getByRole('button', { name: 'Choose environment views', exact: true }));
        const picker = page.getByRole('dialog', { name: 'Environment for drafting', exact: true });
        const environment = picker.getByRole('combobox', { name: 'Environment', exact: true });
        await picker.waitFor();
        const choices = (await environment.locator('option').evaluateAll(list => list.map(o => o.value))).filter(Boolean);
        if (!await environment.inputValue() && choices.length > 0)
          await environment.selectOption(choices.includes(state.environment?.id) ? state.environment.id : choices[0]);
        if (await environment.inputValue()) {
          await click(page, picker.getByRole('button', { name: 'Browse views', exact: true }));
          const cards = picker.locator('.environment-view-card');
          await cards.first().waitFor();
          for (let c = 0; c < Math.min(await cards.count(), 4); c++) {
            const box = cards.nth(c).getByRole('checkbox');
            if (!await box.isChecked()) { if (i === 0) await click(page, box); else await box.check(); }
          }
          await click(page, picker.getByRole('button', { name: 'Save scene setup', exact: true }));
        } else {
          console.warn('No location to inspect: drafting this scene without environment views.');
          await picker.getByRole('button', { name: 'Cancel', exact: true }).click().catch(() => page.keyboard.press('Escape'));
        }
        await picker.waitFor({ state: 'hidden' });
        await click(page, page.locator('.scene-drafting-tools').getByRole('button', { name: 'Draft shots', exact: true }));
        const composer = page.locator('.ai-assist-dialog').filter({ has: page.getByRole('heading', { name: 'Draft shots', exact: true }) });
        await composer.waitFor();
        await beat(500);
        // Drafting looks at the environment views, so a text-only model is swapped for a vision model here.
        if (/vision model/i.test(await composer.textContent())) await chooseVisionModel(page, composer);
        console.log(`  scene ${i + 1} of ${state.scenes.length}: environment set, starting the draft`);
        // A click can land before Blazor wires the button and do nothing: retry until the composer closes,
        // then make sure the draft really started, so a lost click fails instead of waiting forever.
        const draft = composer.getByRole('button', { name: 'Draft shots', exact: true });
        await click(page, draft);
        for (let attempt = 0; attempt < 5 && await composer.isVisible(); attempt++) { await beat(2000); if (await composer.isVisible()) await draft.click({ timeout: 5000 }).catch(() => {}); }
        const started = await page.locator('.scene-drafting-tools').filter({ hasText: /Drafting|Queued|Waiting|Review/i }).waitFor({ timeout: 30000 }).then(() => true, () => false);
        if (!started) throw new Error(`The draft for scene ${i + 1} did not start.`);
        await beat(i === 0 ? 3000 : 500);
      };
      if (i === 0) { await record('scenes', run); started = Date.now(); } else await work(run);
      // Only one scene drafts at a time, so wait for this one before starting the next.
      await waitFor(null, project(`scenes?sceneId=${sceneId}`), async page => {
        await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
        await showRight(page, 'Show Scene environment');
        const review = page.locator('.shot-planning-dialog');
        if (!await review.isVisible()) await page.locator('.scene-drafting-tools .request-action-button').click({ timeout: 2000 }).catch(() => {});
        return review.getByRole('button', { name: 'Add reviewed shots' }).isEnabled({ timeout: 1500 }).catch(() => false);
      });
    }
    await laterCard(started, `${writer} broke every scene into shots`);
  },

  async coverage() {
    for (const [i, sceneId] of state.scenes.entries()) {
      const add = async page => {
        await page.goto(project(`scenes?sceneId=${sceneId}`));
        await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
        await showRight(page, 'Show Scene environment');
        const review = page.locator('.shot-planning-dialog');
        if (!await review.isVisible()) await page.locator('.scene-drafting-tools .request-action-button').click();
        await review.getByRole('button', { name: 'Add reviewed shots' }).waitFor();
        if (i === 0) { await mark(page); await caption(page, captions.scenes2); await glide(page, review); await wheel(page, 8, 4000, 1600); }
        await click(page, review.getByRole('button', { name: 'Add reviewed shots' }));
        await review.waitFor({ state: 'hidden' });
        await beat(i === 0 ? 1500 : 300);
      };
      if (i === 0) await record('coverage', add); else await work(add);
    }
  },

  async shots() {
    const all = await shotList();
    // --film-shots 2,8,11 keeps those shots (numbered from 1 in list order) for references, prompts and takes.
    state.film = args['film-shots'] ? String(args['film-shots']).split(',').map(n => all[Number(n.trim()) - 1]).filter(Boolean) : all;
    const list = state.film;
    state.featured = list.find(s => featuredPattern.test(s.title)) ?? list[0];
    console.log(`${all.length} shots, ${list.length} in the film; featuring "${state.featured.title}"`);
    // Ask for reference suggestions for every film shot; the queue runs them one after another.
    for (const shot of list) {
      const featured = shot.id === state.featured.id;
      const run = async page => {
        await openShot(page, shot.id);
        if (featured) { await mark(page); await caption(page, captions.shots1); }
        const editor = await openReferences(page);
        const assistant = await expandedAssistant(editor);
        await click(page, assistant.getByRole('button', { name: 'Suggest references', exact: true }));
        await beat(featured ? 2500 : 300);
        await closeReferences(page, editor);
      };
      if (featured) await record('shots', run); else await work(run);
    }
    const started = Date.now();
    for (const shot of list) {
      await waitFor(null, project(`shots?shotId=${shot.id}`), async page => {
        await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
        const editor = await openReferences(page);
        const assistant = await expandedAssistant(editor);
        await beat(800);
        const saved = assistant.getByRole('button', { name: 'Review saved suggestion', exact: true });
        const done = await saved.isVisible().catch(() => false) && await saved.isEnabled().catch(() => false);
        await closeReferences(page, editor);
        return done;
      }, { quiet: true });
    }
    await laterCard(started, `${writer} chose references for every shot`);
  },

  async references() {
    for (const shot of state.film ?? state.shots) {
      const featured = shot.id === state.featured.id;
      const apply = async page => {
        await openShot(page, shot.id);
        const editor = await openReferences(page);
        const assistant = await expandedAssistant(editor);
        await assistant.getByRole('button', { name: 'Review saved suggestion', exact: true }).click();
        const add = assistant.getByRole('button', { name: /^(Add checked suggestions|Stage replacement selection)$/ });
        const suggested = await add.waitFor({ timeout: 8000 }).then(() => add.isEnabled(), () => false);
        if (featured) { await mark(page); await caption(page, captions.shots1); }
        if (suggested) {
          if (featured) { await glide(page, assistant.locator('.asset-pick-choices')); await beat(2500); }
          await click(page, add);
        } else {
          console.warn(`  No usable suggestion for "${shot.title}": adding the approved references by hand.`);
          await addReferencesByHand(page, editor);
        }
        await beat(600);
        await click(page, editor.getByRole('button', { name: 'Apply changes', exact: true }));
        await editor.waitFor({ state: 'hidden' }).catch(() => {});
        if (featured) { await glide(page, page.locator('.compact-reference').first()); await beat(2500); }
      };
      if (featured) await record('references', apply); else await work(apply);
    }
  },

  async prompts() {
    for (const shot of state.film ?? state.shots) {
      const featured = shot.id === state.featured.id;
      const run = async page => {
        await openShot(page, shot.id);
        if (featured) { await mark(page); await caption(page, captions.prompts1); }
        await click(page, page.locator('.workspace-right').getByRole('button', { name: 'Prompt', exact: true }));
        const setup = page.locator('.shot-setup-dialog');
        await setup.locator('[data-prompt-ready="true"]').waitFor();
        await openAssist(page, page.getByRole('button', { name: 'Prompt assistance', exact: true }));
        const composer = page.locator('.ai-assist-dialog').filter({ has: page.getByLabel('Direction for AI', { exact: true }) });
        await click(page, composer.getByRole('button', { name: 'Compose prompt', exact: true }));
        await composer.waitFor({ state: 'hidden' });
        await beat(featured ? 2500 : 300);
        await setup.getByRole('button', { name: 'Done', exact: true }).click().catch(() => {});
      };
      if (featured) await record('prompts', run); else await work(run);
    }
    await waitFor(`${writer} composed a prompt for every shot`, project('shots'), async page => {
      await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
      await page.getByLabel('Filter shots by prompt status', { exact: true }).selectOption('missing');
      await beat(800);
      // Only the film's shots get prompts; the rest of the coverage stays without one.
      const filmIds = new Set((state.film ?? state.shots).map(s => s.id));
      const missingIds = (await page.locator('.shot-outline-item .shot-list-row').evaluateAll(rows => rows.map(r => r.getAttribute('data-shot-id')))).filter(id => filmIds.has(id));
      if (missingIds.length === 0) return true;
      // A composed prompt can wait under Review changes instead of applying itself: apply any that are ready.
      for (const id of missingIds) {
        await openShot(page, id);
        await page.locator('.workspace-right').getByRole('button', { name: 'Prompt', exact: true }).click({ timeout: 5000 }).catch(() => {});
        const setup = page.locator('.shot-setup-dialog');
        const review = setup.getByRole('button', { name: 'Review changes', exact: true });
        if (await review.isVisible({ timeout: 3000 }).catch(() => false)) {
          await review.click();
          const apply = page.getByRole('button', { name: /^(Apply changes|Apply anyway)$/ }).last();
          if (await apply.isVisible({ timeout: 10000 }).catch(() => false)) { await apply.click(); console.log('  applied a composed prompt that waited for review'); await beat(1500); }
        }
        await setup.getByRole('button', { name: 'Done', exact: true }).click({ timeout: 3000 }).catch(() => page.keyboard.press('Escape'));
      }
      await page.goto(project('shots'));
      return false;
    });
  },

  async 'prompt-review'() {
    await record('prompt-review', async page => {
      await openShot(page, state.featured.id);
      await mark(page);
      await caption(page, captions.prompts1);
      await click(page, page.locator('.workspace-right').getByRole('button', { name: 'Prompt', exact: true }));
      const setup = page.locator('.shot-setup-dialog');
      await setup.locator('[data-prompt-ready="true"]').waitFor();
      await glide(page, setup);
      await wheel(page, 6, 4000, 900);
      await setup.getByRole('button', { name: 'Done', exact: true }).click().catch(() => page.keyboard.press('Escape'));
      await beat(800);
    });
  },

  async takes() {
    // One Quick take per film shot, from each shot's own Generate takes; the featured one on camera.
    const film = state.film ?? state.shots;
    for (const shot of [state.featured, ...film.filter(s => s.id !== state.featured.id)]) {
      const featured = shot.id === state.featured.id;
      const run = async page => {
        await openShot(page, shot.id);
        if (featured) { await mark(page); await caption(page, captions.takes1); }
        const tools = page.locator('.workspace-right');
        await quick(tools.getByLabel('Resolution', { exact: true }));
        await tools.getByLabel('Takes', { exact: true }).selectOption('1').catch(() => {});
        await beat(featured ? 1500 : 800);
        // A click can be lost while the page re-renders: confirm the shot shows Queued or Generating, else retry.
        const row = page.locator(`.shot-list-row[data-shot-id="${shot.id}"]`);
        const generate = page.getByRole('button', { name: 'Generate takes', exact: true });
        let queued = false;
        for (let attempt = 0; attempt < 4 && !queued; attempt++) {
          if (attempt === 0) await click(page, generate); else { console.warn(`  retrying Generate takes for "${shot.title}"`); await generate.click({ timeout: 10000 }).catch(() => {}); }
          queued = await row.filter({ hasText: /Generating|Queued|[1-9]\d* takes?/ }).waitFor({ timeout: 15000 }).then(() => true, () => false);
        }
        if (!queued) throw new Error(`Generate takes did not start for "${shot.title}".`);
        console.log(`  queued a take for "${shot.title}"`);
        await beat(featured ? 3000 : 500);
      };
      if (featured) await record('takes', run); else await work(run);
    }
    const filmIds = new Set(film.map(s => s.id));
    await waitFor('MiniMax H3 rendered a take for every shot in the film', project('shots'), async page => {
      await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
      const rows = await page.locator('.shot-list-row').evaluateAll(list => list.map(r => ({ id: r.getAttribute('data-shot-id'), text: r.textContent })));
      const mine = rows.filter(r => filmIds.has(r.id));
      return mine.length === film.length && mine.every(r => /[1-9]\d* takes?/.test(r.text) && !/Generating|Queued/.test(r.text));
    });
    // Select the first take of every film shot, off camera, so refinement and the cut have a take to use.
    for (const shot of film) await work(async page => {
      await openShot(page, shot.id);
      await page.locator('[data-workspace-group=center]').getByRole('tab', { name: /^Takes/ }).click();
      const card = page.locator('.unified-take-card').first();
      await card.waitFor({ timeout: 15000 });
      const use = card.getByRole('button', { name: 'Use this take', exact: true });
      if (await use.isVisible().catch(() => false)) { await use.click(); await page.locator('.unified-take-card.is-selected').first().waitFor({ timeout: 10000 }).catch(() => {}); }
    });
  },

  async 'takes-review'() {
    await record('takes-review', async page => {
      await openShot(page, state.featured.id);
      await mark(page);
      await caption(page, captions.takes2);
      await click(page, page.getByRole('button', { name: 'Review take', exact: true }).first());
      const play = page.locator('.take-player').getByRole('button', { name: 'Play', exact: true });
      await play.waitFor();
      await click(page, play);
      await beat(8000);
      await page.keyboard.press('Escape');
      await beat(600);
      // Scroll the shot list so the thumbnails show the same room and cast across shots.
      await glide(page, page.locator('.shot-list-row').first());
      await wheel(page, 8, 4000, 1200);
      // Show the overnight path without queueing anything.
      await caption(page, captions.takes3);
      await click(page, page.getByRole('button', { name: 'Bulk operations', exact: true }));
      await click(page, page.getByRole('menuitem', { name: 'Refine selected takes…', exact: true }));
      const refine = page.getByRole('dialog').filter({ hasText: 'Shared settings' }).last();
      await refine.waitFor({ timeout: 10000 }).catch(() => {});
      await glide(page, refine.getByText('Use refined frames from earlier clips').first());
      await beat(4500);
      await page.keyboard.press('Escape');
      await beat(800);
    });
  },

  async refine() {
    await record('refine', async page => {
      await page.goto(project(`shots?shotId=${state.featured.id}&view=Takes`));
      await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
      await page.locator('.unified-take-card').first().waitFor({ timeout: 15000 });
      await mark();
      await caption(page, captions.refine1);
      await click(page, page.getByRole('button', { name: 'Select multiple', exact: true }));
      await click(page, page.getByRole('checkbox', { name: 'Select take 1', exact: true }));
      await click(page, page.getByRole('button', { name: 'Refine selected…', exact: true }));
      const dialog = page.getByRole('dialog', { name: 'Refine from video', exact: true });
      // "Queue refinement" for one clip, "Queue 2 refinements" for more.
      const queue = dialog.getByRole('button', { name: /^Queue (\d+ )?refinements?$/ });
      await queue.waitFor({ timeout: 60000 });
      for (let i = 0; i < 60 && !await queue.isEnabled(); i++) await beat(1000);
      await glide(page, dialog.locator('.refine-row').first());
      await beat(2500);
      const now = dialog.getByRole('radio', { name: 'Start now', exact: true });
      if (await now.count() && !await now.isChecked()) await click(page, now);
      await click(page, queue);
      await dialog.waitFor({ state: 'hidden', timeout: 30000 });
      await beat(2000);
    });
    await waitFor('MiniMax H3 refined the take at full size', project(`shots?shotId=${state.featured.id}&view=Takes`), async page => {
      await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
      const batches = page.getByRole('dialog', { name: 'Refinement batches', exact: true });
      if (!await batches.isVisible()) await page.locator('.shots-library-tools').getByRole('button', { name: 'Refinement batches', exact: true }).click({ timeout: 3000 }).catch(() => {});
      const start = batches.getByRole('button', { name: /^(Start batch|Resume batch)$/ });
      if (await start.count() && await start.first().isEnabled().catch(() => false)) await start.first().click().catch(() => {});
      const stage = await batches.locator('.refinement-batch').first().locator('.refinement-item').first().getAttribute('data-stage', { timeout: 3000 }).catch(() => '');
      if (/Failed|Cancelled/i.test(stage)) throw new Error(`The refinement ${stage.toLowerCase()}.`);
      return stage === 'Completed';
    });
  },

  async compare() {
    await record('compare', async page => {
      await page.goto(project(`shots?shotId=${state.featured.id}&view=Takes`));
      await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
      await page.locator('.shots-library-tools').getByRole('button', { name: 'Refinement batches', exact: true }).click();
      const batches = page.getByRole('dialog', { name: 'Refinement batches', exact: true });
      const item = batches.locator('.refinement-batch').first().locator('.refinement-item').first();
      await item.waitFor();
      await mark();
      await caption(page, captions.compare1);
      await click(page, item.getByRole('button', { name: 'Compare', exact: true }));
      const final = item.getByLabel(/^Refined final for /);
      await final.waitFor({ timeout: 15000 });
      await beat(1000);
      // Play the final; the source follows it.
      await glide(page, final);
      await final.evaluate(v => { v.muted = false; return v.play?.(); }).catch(() => {});
      await beat(9000);
      await click(page, item.getByRole('button', { name: 'Use this take', exact: true }));
      await beat(1500);
    });
  },

  async cut() {
    await record('cut', async page => {
      await page.goto(project('cut'));
      await page.locator('.cut-heading[data-interactive="true"]').waitFor();
      await mark(page);
      await caption(page, captions.cut1);
      await click(page, page.getByRole('button', { name: 'Choose takes…', exact: true }).first());
      const chooser = page.locator('.cut-chooser');
      await chooser.waitFor();
      await beat(2000);
      await click(page, chooser.getByRole('button', { name: /Apply changes/ }));
      await chooser.waitFor({ state: 'hidden' });
      await page.locator('.cut-player .cut-paused-frame:not([hidden])').waitFor({ timeout: 30000 }).catch(() => {});
      await click(page, page.getByRole('button', { name: 'Play from beginning', exact: true }));
      await page.waitForFunction(() => document.querySelector('.cut-player')?.getAttribute('data-playing') === 'true', null, { timeout: 15000 }).catch(() => {});
      await beat(6000);
      await click(page, page.getByRole('button', { name: 'Export MP4…', exact: true }));
      await page.locator('.cut-export-dialog').waitFor();
      await beat(1500);
    });
    await work(async page => {
      await page.goto(project('cut'));
      await page.locator('.cut-heading[data-interactive="true"]').waitFor();
      await page.getByRole('button', { name: 'Export MP4…', exact: true }).click();
      const download = page.waitForEvent('download', { timeout: waitLimit });
      await page.locator('.cut-export-dialog').getByRole('button', { name: 'Export', exact: true }).click();
      const file = await download;
      state.film = path.join(out, 'film.mp4');
      await file.saveAs(state.film);
      console.log(`Exported the cut to ${state.film}`);
    });
  },
};

// --- app helpers ---------------------------------------------------------------------------------

function project(rest) { state.route ??= args.route; if (!state.route) fail('No project yet: run the create phase first.'); return `${url}/projects/${state.route}/${rest}`; }

async function ready(page, studio, settle = 700) {
  await page.locator(`.studio-workspace[data-studio="${studio}"][data-ready="true"]`).waitFor({ timeout: 45000 });
  if (settle) await beat(settle);
}

async function chooseTextModel(page, pattern = textModel) {
  await page.getByRole('button', { name: 'Text model options', exact: true }).first().click();
  const dialog = page.getByRole('dialog', { name: 'Text model', exact: true });
  const select = dialog.getByRole('combobox', { name: 'Text model', exact: true });
  // The list fills in after the dialog opens.
  await select.locator('option').first().waitFor({ state: 'attached', timeout: 15000 }).catch(() => {});
  await beat(500);
  const options = await select.locator('option').allTextContents();
  const choice = options.find(o => pattern.test(o) && !/^Use project default/i.test(o)) ?? options.find(o => pattern.test(o));
  if (!choice) throw new Error(`No text model matches ${pattern}. Available: ${options.join(' | ')}`);
  await select.selectOption({ label: choice });
  const projectDefault = dialog.getByRole('button', { name: 'Set as project default', exact: true });
  if (await projectDefault.isVisible().catch(() => false)) await projectDefault.click();
  else await dialog.getByRole('button', { name: 'Done', exact: true }).click();
  console.log(`Text model: ${choice}`);
}

async function shotList() {
  const list = await work(async page => {
    await page.goto(project('shots'));
    await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
    return page.locator('.shot-outline-item .shot-list-row').evaluateAll(rows => rows.map(r => ({ id: r.getAttribute('data-shot-id'), title: r.querySelector('.shot-list-title')?.textContent?.trim() ?? '' })));
  });
  if (list.length === 0) throw new Error('No shots in the project.');
  state.shots = list;
  return list;
}

async function openShot(page, id) {
  await page.goto(project(`shots?shotId=${id}`));
  await page.locator('.shots-heading[data-interactive="true"]').waitFor({ timeout: 30000 });
  await page.locator(`.shot-list-row[data-shot-id="${id}"][aria-pressed="true"]`).waitFor({ timeout: 15000 }).catch(() => {});
  await beat(500);
}

async function openReferences(page) {
  await page.getByRole('button', { name: /^(?:Edit|Manage) references…?$/ }).first().click();
  const editor = page.locator('.shot-reference-dialog');
  await editor.waitFor();
  return editor;
}
async function closeReferences(page, editor) {
  if (!await editor.isVisible()) return;
  const cancel = editor.getByRole('button', { name: 'Cancel', exact: true });
  if (await cancel.isVisible().catch(() => false)) await cancel.click(); else await page.keyboard.press('Escape');
  await editor.waitFor({ state: 'hidden', timeout: 5000 }).catch(() => {});
}
// The reference assistant renders its saved request only while expanded.
async function expandedAssistant(editor) {
  const assistant = editor.locator('.asset-pick-assistant');
  if (!await assistant.evaluate(d => d.open)) await assistant.locator(':scope > summary').click();
  return assistant;
}

// Fallback for a failed suggestion: every approved reference image, and the location's reel.
async function addReferencesByHand(page, editor) {
  const approvedIndex = () => editor.getByRole('button', { name: 'Add image', exact: true }).evaluateAll(buttons => buttons.findIndex(button => {
    let card = button.parentElement;
    for (let i = 0; i < 5 && card && !card.querySelector('img'); i++) card = card.parentElement;
    return /(^|\s)Reference(\s|$)/.test(card?.innerText ?? '');
  }));
  for (let added = 0; added < 6; added++) {
    const index = await approvedIndex();
    if (index < 0) break;
    await click(page, editor.getByRole('button', { name: 'Add image', exact: true }).nth(index));
    await beat(400);
  }
  const reels = editor.getByRole('button', { name: 'Add reel', exact: true });
  const names = await reels.evaluateAll(buttons => buttons.map(button => button.parentElement?.parentElement?.innerText ?? ''));
  const reelIndex = names.findIndex(text => state.environment?.name && text.includes(state.environment.name));
  if (reelIndex >= 0) await click(page, reels.nth(reelIndex));
}

async function openAssist(page, opener) {
  const assist = page.locator('.ai-assist-dialog');
  for (let attempt = 0; attempt < 10 && !await assist.isVisible(); attempt++) {
    await opener.click({ timeout: 2000 }).catch(() => {});
    await assist.waitFor({ timeout: 2000 }).catch(() => {});
  }
  await assist.waitFor();
  await beat(500);
  if (/vision model/i.test(await assist.textContent())) await chooseVisionModel(page, assist);
  return assist;
}

// Some assistants read pictures and refuse a text-only model; switch the project default to a vision model.
async function chooseVisionModel(page, assist) {
  await assist.locator('.model-chip').first().click();
  const select = page.getByRole('combobox', { name: 'Text model', exact: true });
  await select.locator('option').first().waitFor({ state: 'attached', timeout: 15000 }).catch(() => {});
  await beat(500);
  const options = await select.locator('option').allTextContents();
  const choice = options.find(o => visionModel.test(o) && !/^Use project default/i.test(o));
  if (!choice) throw new Error(`No vision model matches ${visionModel}. Available: ${options.join(' | ')}`);
  await select.selectOption({ label: choice });
  await page.getByRole('button', { name: 'Set as project default', exact: true }).click();
  await page.getByRole('dialog', { name: 'Text model', exact: true }).waitFor({ state: 'hidden' }).catch(() => {});
  console.log(`Vision model: ${choice}`);
}

async function showRight(page, label) {
  const toggle = page.getByRole('button', { name: label, exact: true });
  if (await toggle.isVisible().catch(() => false)) await toggle.click();
}

async function quick(select) {
  const options = await select.locator('option').evaluateAll(list => list.map(o => ({ value: o.value, text: o.textContent })));
  const option = options.find(o => /^\s*Quick/i.test(o.text));
  if (option) await select.selectOption(option.value);
}

// --- server and checkpoints ----------------------------------------------------------------------

async function freshLibrary() {
  await removeInside(library);
  await removeInside(checkpoints);
  await mkdir(library, { recursive: true });
  if (args.serve === 'web') {
    const seed = path.resolve(args.seed ?? path.join(repo, 'App_Data'));
    for (const name of ['ai-settings.json', 'generation-setups.json'])
      if (existsSync(path.join(seed, name))) await copyFile(path.join(seed, name), path.join(library, name));
    console.log(`Scratch library seeded with the AI settings from ${seed}`);
  }
}

async function startServer() {
  const port = new URL(url).port || '80';
  const env = { ...process.env };
  let command, list, cwd;
  if (args.serve === 'mock') {
    command = 'dotnet';
    list = [path.join(repo, 'tests/Lumibelle.BrowserHost/bin/Release/net10.0/Lumibelle.BrowserHost.dll'), '--urls', url];
    cwd = path.join(repo, 'tests/Lumibelle.BrowserHost');
    env.LUMIBELLE_BROWSER_DATA = library;
  } else {
    command = path.join(repo, 'src/Lumibelle.Web/bin/Release/net10.0', process.platform === 'win32' ? 'Lumibelle.Web.exe' : 'Lumibelle.Web');
    list = ['--environment', 'Development', '--urls', url, `--Lumibelle:DataDirectory=${library}`];
    cwd = path.join(repo, 'src/Lumibelle.Web');
  }
  if (!existsSync(list[0].endsWith('.dll') ? list[0] : command)) fail(`Build it first: ${args.serve === 'mock' ? 'dotnet build tests/Lumibelle.BrowserHost -c Release' : 'dotnet build src/Lumibelle.Web -c Release'}`);
  if (await reachable()) fail(`Something already answers at ${url}. Stop it or pass another --url.`);
  server = spawn(command, list, { cwd, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let log = '';
  server.stdout.on('data', d => log = (log + d).slice(-20000));
  server.stderr.on('data', d => log = (log + d).slice(-20000));
  for (let i = 0; i < 120; i++) {
    if (await reachable()) { console.log(`Lumibelle (${args.serve}) at ${url}, port ${port}`); return; }
    if (server.exitCode !== null) break;
    await beat(500);
  }
  fail(`Lumibelle did not start.\n${log}`);
}

async function stopServer() {
  if (!server) return;
  const pid = server.pid;
  if (process.platform === 'win32') spawnSync('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true });
  else server.kill('SIGTERM');
  for (let i = 0; i < 40 && await reachable(); i++) await beat(250);
  server = null;
}

async function reachable() {
  try { const response = await fetch(`${url}/`, { signal: AbortSignal.timeout(2000) }); return response.status < 500; } catch { return false; }
}

// Saves the library as it is before a phase. The server stops so SQLite and JSON files are consistent.
async function checkpoint(phase) {
  const started = Date.now();
  await stopServer();
  const target = path.join(checkpoints, phase);
  await removeInside(target);
  await mirror(library, target);
  await startServer();
  console.log(`  checkpoint ${phase} (${minutes(Date.now() - started)})`);
}

async function restore(phase) {
  console.log(`Restoring the library from checkpoint "${phase}"`);
  await stopServer();
  await removeInside(library);
  await mirror(path.join(checkpoints, phase), library);
}

// Media never changes after it is written, so it is hardlinked; everything else (SQLite, JSON) is copied.
const MEDIA = /\.(mp4|webm|mkv|mov|png|jpe?g|webp|gif|wav|flac|mp3|ogg|npz|npy|latent|safetensors|zst|zip|bin)$/i;
async function mirror(source, target) {
  await mkdir(target, { recursive: true });
  for (const entry of await readdir(source, { withFileTypes: true })) {
    const from = path.join(source, entry.name), to = path.join(target, entry.name);
    if (entry.isSymbolicLink()) continue; // never follow links out of the scratch folders
    if (entry.isDirectory()) await mirror(from, to);
    else if (MEDIA.test(entry.name)) await link(from, to).catch(() => copyFile(from, to));
    else await copyFile(from, to);
  }
}

// Deletes only folders inside the output folder.
async function removeInside(folder) {
  const resolved = path.resolve(folder);
  if (!resolved.startsWith(out + path.sep)) throw new Error(`Refusing to delete ${resolved}: it is outside ${out}.`);
  await rm(resolved, { recursive: true, force: true, maxRetries: 5, retryDelay: 300 });
}

// --- recording ----------------------------------------------------------------------------------

async function record(name, run) {
  if (rejoining) { console.log(`  skipping ${name}: already recorded before the wait`); return; }
  const context = await browser.newContext({ viewport: { width, height }, recordVideo: { dir: path.join(out, 'raw'), size: { width, height } }, colorScheme: 'light', acceptDownloads: true });
  await context.addInitScript(overlay);
  const page = await context.newPage();
  const clip = { name, started: Date.now(), lead: null };
  current = clip;
  try { await run(page); }
  catch (error) { await page.screenshot({ path: path.join(out, `${name}-failed.png`) }).catch(() => {}); throw new Error(`${name}: ${error.message}`); }
  finally {
    const video = page.video();
    await context.close();
    const number = ++sequence;
    const target = path.join(out, 'raw', `${String(number).padStart(3, '0')}-${name}.webm`);
    await copyFile(await video.path(), target); await rm(await video.path(), { force: true });
    const lead = clip.lead ?? 0;
    console.log(`  recorded ${name}: ${((Date.now() - clip.started) / 1000).toFixed(1)}s, trimming ${lead.toFixed(1)}s`);
    state.clips.push({ step, sequence: number, kind: 'webm', file: target, lead });
    state.kickedAt = Date.now(); // a wait that follows counts from here, also after a restart
    await save();
    current = null;
  }
}

// Unrecorded work: the same UI, off camera.
async function work(run) {
  const context = await browser.newContext({ viewport: { width, height }, colorScheme: 'light', acceptDownloads: true });
  const page = await context.newPage();
  try { return await run(page); }
  catch (error) { await page.screenshot({ path: path.join(out, `${step}-work-failed.png`) }).catch(() => {}); throw error; }
  finally { await context.close(); }
}

// Waits off camera until check(page) is true, reloading now and then, and adds a badge with the elapsed time.
// With softAfter, giving up after that long is not an error: the next phase has a fallback.
async function waitFor(label, target, check, { quiet = false, softAfter = null } = {}) {
  let started = Date.now();
  if (rejoining) {
    rejoining = false;
    started = startedAt() ?? state.kickedAt ?? started;
    console.log(`  rejoining the wait, which started ${minutes(Date.now() - started)} ago`);
  }
  // A labelled wait also keeps a screenshot of the live view every so often, for a time-lapse under its badge.
  const frames = [];
  const every = Number(args['timelapse-every'] ?? 15) * 1000;
  let shot = 0;
  await work(async page => {
    let loaded = 0;
    for (;;) {
      if (Date.now() - loaded > 180000) { await page.goto(target); loaded = Date.now(); }
      if (await check(page)) return;
      if (label && every > 0 && Date.now() - shot >= every) {
        shot = Date.now();
        const file = path.join(out, 'raw', `wait-${sequence + 1}-${String(frames.length).padStart(4, '0')}.jpg`);
        if (await page.screenshot({ path: file, type: 'jpeg', quality: 80 }).then(() => true, () => false)) frames.push(file);
      }
      if (softAfter && Date.now() - started > Math.min(softAfter, waitLimit)) { console.warn(`  stopped waiting after ${minutes(Date.now() - started)}: ${label ?? target}`); return; }
      if (Date.now() - started > waitLimit) throw new Error(`Gave up waiting after ${minutes(waitLimit)}: ${label ?? target}`);
      await beat(5000);
    }
  });
  if (!quiet) console.log(`  waited ${minutes(Date.now() - started)}${label ? `: ${label}` : ''}`);
  if (label) await laterCard(started, label, frames);
}

// A wait becomes a badge drawn over the frozen last frame of the clip before it, so whatever was on screen
// stays readable. Only the text is stored; assembly draws it.
async function laterCard(started, label, frames = []) {
  state.clips.push({ step, sequence: ++sequence, kind: 'badge', title: laterText(Date.now() - started), subtitle: label, elapsed: Date.now() - started, frames });
}

async function card(title, subtitle, seconds, large = false) {
  const number = ++sequence;
  const file = path.join(out, 'raw', `${String(number).padStart(3, '0')}-card.png`);
  await work(async page => {
    await page.setContent(`<!doctype html><html><body style="margin:0;background:#141210;color:#f4efe6;font-family:'Segoe UI',system-ui,sans-serif;display:grid;place-items:center;height:100vh">
      <div style="text-align:center;max-width:72vw"><div style="font-size:${large ? 96 : 64}px;font-weight:600;letter-spacing:.01em">${esc(title)}</div>
      ${[subtitle].flat().filter(Boolean).map((line, i) => `<div style="font-size:${i === 0 ? 34 : 26}px;opacity:${i === 0 ? .85 : .6};margin-top:${i === 0 ? 24 : 12}px">${esc(line)}</div>`).join('')}</div></body></html>`);
    await page.screenshot({ path: file });
  });
  const clip = { step, sequence: number, kind: 'still', file, seconds };
  state.clips.push(clip);
  return clip;
}

async function mark() { if (current && current.lead === null) current.lead = Math.max(0, (Date.now() - current.started) / 1000 - 0.3); }

// Fake cursor and caption band. Runs in every page of a recorded context before any script of the app.
function overlay() {
  const install = () => {
    if (document.getElementById('demo-cursor')) return;
    const style = document.createElement('style');
    style.textContent = `
      #demo-cursor{position:fixed;left:0;top:0;width:22px;height:30px;z-index:2147483646;pointer-events:none;transform:translate(-3px,-2px);transition:transform .08s linear}
      #demo-cursor svg{filter:drop-shadow(0 1px 2px rgba(0,0,0,.5))}
      #demo-cursor.down svg{transform:scale(.88);transform-origin:4px 3px}
      #demo-caption{position:fixed;left:50%;bottom:6%;transform:translateX(-50%);max-width:70vw;padding:14px 28px;border-radius:12px;
        background:rgba(20,18,16,.86);color:#f4efe6;font:500 28px/1.35 "Segoe UI",system-ui,sans-serif;text-align:center;
        z-index:2147483647;pointer-events:none;opacity:0;transition:opacity .35s ease;box-shadow:0 8px 32px rgba(0,0,0,.35)}
      #demo-caption.show{opacity:1}`;
    document.head.appendChild(style);
    const cursor = document.createElement('div'); cursor.id = 'demo-cursor';
    cursor.innerHTML = '<svg viewBox="0 0 22 30" width="22" height="30"><path d="M3 2 L3 23 L8.5 18 L12.5 27 L16 25.5 L12 16.5 L19 16.5 Z" fill="#fff" stroke="#111" stroke-width="1.5" stroke-linejoin="round"/></svg>';
    const band = document.createElement('div'); band.id = 'demo-caption';
    document.body.append(cursor, band);
    window.addEventListener('mousemove', e => { cursor.style.transform = `translate(${e.clientX - 3}px,${e.clientY - 2}px)`; }, true);
    window.addEventListener('mousedown', () => cursor.classList.add('down'), true);
    window.addEventListener('mouseup', () => cursor.classList.remove('down'), true);
    // A caption fades after a few seconds so it never hides the controls underneath for long.
    let timer = null;
    window.__demoCaption = text => { clearTimeout(timer); if (!text) { band.classList.remove('show'); return; } band.textContent = text; band.classList.add('show'); timer = setTimeout(() => band.classList.remove('show'), 4200); };
  };
  if (document.body) install(); else document.addEventListener('DOMContentLoaded', install);
}

async function caption(page, text) { await page.evaluate(t => window.__demoCaption?.(t), text); await beat(text ? 900 : 300); }
async function glide(page, locator, steps = 24) {
  const target = locator.first();
  // Short timeouts: a missing target is only a missed cursor move, not a reason to stall the recording.
  await target.scrollIntoViewIfNeeded({ timeout: 3000 }).catch(() => {});
  const box = await target.boundingBox({ timeout: 3000 }).catch(() => null);
  if (!box) return;
  await page.mouse.move(box.x + Math.min(box.width / 2, 160), box.y + Math.min(box.height / 2, 60), { steps });
}
async function click(page, locator) { await glide(page, locator); await beat(200); await locator.first().click(); await beat(350); }
async function type(page, locator, text, delay = 35) { await click(page, locator); await locator.first().fill(''); await locator.first().pressSequentially(text, { delay }); await beat(300); }
async function wheel(page, ticks, total, distance) { for (let i = 0; i < ticks; i++) { await page.mouse.wheel(0, distance / ticks); await beat(total / ticks); } }
function beat(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

// --- ffmpeg -------------------------------------------------------------------------------------

async function assemble() {
  const title = await stillFile(captions.title[0], captions.title.slice(1), 'title');
  const end = await stillFile(captions.end[0], captions.end.slice(1), 'end');
  // Job-starting clips are left out by default: their wait badge and the review clip after them tell the story.
  const omit = new Set(String(args.omit ?? 'create,script,assets,reel-render,keyframes,shots,prompts,takes').split(',').map(s => s.trim()).filter(Boolean));
  const speed = Number(args.speed ?? 1.75);
  // --speed-for compare=1,switch=1.5 sets the speed of single phases, for clips that play video or need reading.
  const speeds = Object.fromEntries(String(args['speed-for'] ?? '').split(',').filter(Boolean).map(p => p.split('=')).map(([k, v]) => [k.trim(), Number(v)]));
  const recorded = [...state.clips]
    .filter(c => c.kind !== 'webm' || !omit.has(c.step))
    .sort((a, b) => order(a.step) - order(b.step) || a.sequence - b.sequence)
    .map(c => c.kind === 'still' || c.kind === 'badge' ? { ...c, seconds: Number(args['card-seconds'] ?? 2) } : c);
  // The first long wait says why it was long, once.
  const note = args['slow-note'] ?? 'A local Gemma 4 31B writes at about 7 tokens per second. A hosted model would be much faster.';
  const slow = recorded.find(c => c.kind === 'badge' && (c.elapsed ? c.elapsed >= 20 * 60000 : /hour/.test(c.title)));
  if (slow && note) Object.assign(slow, { note, seconds: slow.seconds + 1 });
  const list = [{ kind: 'still', file: title, seconds: 4, step: 'title' }];
  const coldOpen = Number(args['cold-open'] ?? 5);
  if (state.film && coldOpen > 0) list.push({ kind: 'film', file: state.film, start: 0, seconds: coldOpen, step: 'cold open' });
  list.push(...recorded);
  if (state.film) list.push({ kind: 'film', file: state.film, tail: Number(args['film-tail'] ?? 15), step: 'film' });
  list.push({ kind: 'still', file: end, seconds: 6, step: 'end' });
  const video = `scale=${width}:${height}:force_original_aspect_ratio=decrease,pad=${width}:${height}:(ow-iw)/2:(oh-ih)/2,fps=30,format=yuv420p`;
  const encode = ['-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18', '-c:a', 'aac', '-b:a', '160k', '-ar', '48000', '-ac', '2'];
  await mkdir(path.join(out, 'parts'), { recursive: true });
  const parts = [];
  let reused = 0;
  let previous = null; // the last UI recording before a badge
  for (const clip of list) {
    // UI recordings play faster; the film keeps its real speed and sound.
    const rate = clip.kind === 'webm' ? speeds[clip.step] ?? speed : 1;
    const filter = rate !== 1 ? `setpts=PTS/${rate},${video}` : video;
    // A part is named by what it was made from, so an unchanged clip is never encoded twice.
    const source = clip.file ? await stat(clip.file) : null;
    const behind = clip.kind === 'badge' && previous ? await stat(previous.file) : null;
    const key = createHash('sha1').update(JSON.stringify([clip.kind, clip.file, source?.size, source?.mtimeMs, clip.lead, clip.seconds, clip.start, clip.tail, filter,
      clip.title, clip.subtitle, clip.note, clip.frames?.length, previous && clip.kind === 'badge' ? [previous.file, behind.mtimeMs] : null, BADGE_VERSION])).digest('hex').slice(0, 16);
    const target = path.join(out, 'parts', `${key}.mp4`);
    if (clip.kind === 'webm') previous = clip;
    if (existsSync(target)) reused++;
    else if (clip.kind === 'badge') {
      const badge = await badgeFile(clip.title, clip.subtitle, clip.note);
      const frames = (clip.frames ?? []).filter(f => existsSync(f));
      // With screenshots of the live view, play them as a time-lapse; otherwise freeze the last frame of the
      // clip before, or, with nothing before, sit on the dark background.
      const lapse = frames.length >= 4 ? Math.max(clip.seconds, Number(args['timelapse-seconds'] ?? 3)) : 0;
      const seconds = String(lapse || clip.seconds);
      let background;
      if (lapse) {
        const list = path.join(out, 'parts', `${key}-frames.txt`);
        const each = lapse / frames.length;
        await writeFile(list, frames.map(f => `file '${f.replaceAll(path.sep, '/')}'\nduration ${each.toFixed(4)}`).join('\n') + `\nfile '${frames.at(-1).replaceAll(path.sep, '/')}'`);
        background = ['-f', 'concat', '-safe', '0', '-i', list];
      }
      else background = previous ? ['-loop', '1', '-t', seconds, '-i', await lastFrame(previous)] : ['-f', 'lavfi', '-t', seconds, '-i', `color=c=0x141210:s=${width}x${height}`];
      await ffmpeg([...background, '-loop', '1', '-t', seconds, '-i', badge, '-f', 'lavfi', '-t', seconds, '-i', 'anullsrc=r=48000:cl=stereo',
        '-filter_complex', `[0:v]scale=${width}:${height}:force_original_aspect_ratio=decrease,pad=${width}:${height}:(ow-iw)/2:(oh-ih)/2[bg];[1:v]format=rgba,fade=t=in:st=0:d=0.3:alpha=1[badge];[bg][badge]overlay=0:0,fps=30,format=yuv420p[v]`,
        '-map', '[v]', '-map', '2:a', ...encode, target]);
    }
    else if (clip.kind === 'webm') await ffmpeg(['-ss', clip.lead.toFixed(2), '-i', clip.file, '-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo', '-shortest', '-vf', filter, ...encode, target]);
    else if (clip.kind === 'still') await ffmpeg(['-loop', '1', '-t', String(clip.seconds), '-i', clip.file, '-f', 'lavfi', '-t', String(clip.seconds), '-i', 'anullsrc=r=48000:cl=stereo', '-vf', filter, ...encode, target]);
    else {
      const trim = clip.tail ? ['-sseof', `-${clip.tail}`] : ['-ss', String(clip.start), '-t', String(clip.seconds)];
      await ffmpeg([...trim, '-i', clip.file, '-vf', filter, ...encode, target]);
    }
    parts.push({ target, step: clip.step, kind: clip.kind, seconds: await duration(target) });
  }
  const concat = path.join(out, 'parts', 'concat.txt');
  await writeFile(concat, parts.map(p => `file '${p.target.replaceAll(path.sep, '/')}'`).join('\n'));
  const final = path.join(out, 'demo.mp4');
  await ffmpeg(['-f', 'concat', '-safe', '0', '-i', concat, '-c', 'copy', '-movflags', '+faststart', final]);
  const seconds = await duration(final);
  // Where the time goes, so the next pass knows what to cut.
  console.log('\nPart            Kind    Seconds');
  for (const part of parts) console.log(`${String(part.step).padEnd(16)}${part.kind.padEnd(8)}${part.seconds.toFixed(1).padStart(7)}`);
  console.log(`\nWrote ${final} (${seconds.toFixed(1)}s, ${((await stat(final)).size / 1e6).toFixed(1)} MB; ${reused} of ${parts.length} parts reused)`);
  if (omit.size) console.log(`Left out: ${[...omit].join(', ')} (--omit). UI speed ${speed}x (--speed).`);
  if (seconds > 120) console.warn('Over two minutes: add phases to --omit, raise --speed, shorten --film-tail, or trim a phase and --retake it.');
}

const BADGE_VERSION = 2; // bump when the badge design changes, so cached parts are redrawn

// A transparent full-frame PNG with the elapsed-time badge at the top, clear of the captions at the bottom.
async function badgeFile(title, subtitle, note) {
  const file = path.join(out, 'parts', `badge-${createHash('sha1').update(JSON.stringify([title, subtitle, note, width, height, BADGE_VERSION])).digest('hex').slice(0, 16)}.png`);
  if (existsSync(file)) return file;
  await work(async page => {
    await page.setContent(`<!doctype html><html><body style="margin:0;background:transparent;width:${width}px;height:${height}px;font-family:'Segoe UI',system-ui,sans-serif">
      <div style="position:absolute;top:${Math.round(height * 0.06)}px;left:50%;transform:translateX(-50%);display:flex;align-items:center;gap:20px;
        padding:18px 34px 18px 26px;border-radius:18px;background:rgba(20,18,16,.9);color:#f4efe6;box-shadow:0 10px 40px rgba(0,0,0,.4);white-space:nowrap">
        <svg width="44" height="44" viewBox="0 0 24 24" fill="none" stroke="#e8b866" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="13" r="8"/><path d="M12 9v4l2.5 2.5M9.5 2.5h5"/></svg>
        <div><div style="font-size:34px;font-weight:600;line-height:1.15">${esc(title)}</div>
        <div style="font-size:24px;opacity:.8;margin-top:4px">${esc(subtitle ?? '')}</div>${note ? `<div style="font-size:20px;opacity:.6;margin-top:8px;font-style:italic">${esc(note)}</div>` : ''}</div></div></body></html>`);
    await page.screenshot({ path: file, omitBackground: true });
  });
  return file;
}

// The final frame of a recording as it appears in the demo, after its trim.
async function lastFrame(clip) {
  const source = await stat(clip.file);
  const file = path.join(out, 'parts', `frame-${createHash('sha1').update(JSON.stringify([clip.file, source.mtimeMs])).digest('hex').slice(0, 16)}.png`);
  if (!existsSync(file)) await ffmpeg(['-sseof', '-0.5', '-i', clip.file, '-update', '1', '-frames:v', '999', file]);
  return file;
}

async function stillFile(title, subtitle, name) {
  const clip = await card(title, subtitle, 0, true);
  state.clips.splice(state.clips.indexOf(clip), 1);
  const file = path.join(out, 'raw', `${name}.png`);
  await copyFile(clip.file, file);
  return file;
}

function ffmpeg(list) {
  return new Promise((resolve, reject) => {
    const child = spawn('ffmpeg', ['-y', '-hide_banner', '-loglevel', 'error', ...list], { stdio: ['ignore', 'inherit', 'inherit'], windowsHide: true });
    child.on('exit', code => code === 0 ? resolve() : reject(new Error(`ffmpeg exited with ${code}`)));
  });
}
function duration(file) {
  return new Promise((resolve, reject) => {
    let text = '';
    const child = spawn('ffprobe', ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', file], { windowsHide: true });
    child.stdout.on('data', d => text += d); child.on('exit', code => code === 0 ? resolve(Number(text)) : reject(new Error('ffprobe failed')));
  });
}

// --- small things -------------------------------------------------------------------------------

function startedAt() {
  if (!args['started-at']) return null;
  const value = String(args['started-at']);
  if (/^\d{1,2}:\d{2}$/.test(value)) { const [h, m] = value.split(':').map(Number); const today = new Date(); today.setHours(h, m, 0, 0); return today.getTime(); }
  const time = Date.parse(value);
  if (Number.isNaN(time)) fail(`--started-at: cannot read "${value}"`);
  return time;
}

function laterText(ms) {
  const m = Math.round(ms / 60000);
  if (m < 1) return 'Moments later';
  if (m < 60) return m === 1 ? '1 minute later' : `${m} minutes later`;
  const h = Math.floor(m / 60), rest = m % 60;
  return `${h === 1 ? '1 hour' : `${h} hours`}${rest ? ` ${rest} minute${rest === 1 ? '' : 's'}` : ''} later`;
}
function minutes(ms) { return ms < 60000 ? `${Math.round(ms / 1000)}s` : `${(ms / 60000).toFixed(1)} min`; }
function parse(list) {
  const result = {};
  for (let i = 0; i < list.length; i++) {
    if (!list[i].startsWith('--')) continue;
    const key = list[i].slice(2), next = list[i + 1];
    if (next === undefined || next.startsWith('--')) result[key] = true; else { result[key] = next; i++; }
  }
  return result;
}
function esc(text) { return String(text).replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]); }
function fail(message) { console.error(message); process.exit(1); }

await main();
