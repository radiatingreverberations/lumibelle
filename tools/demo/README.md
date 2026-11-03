# Demo recorder

`record-demo.mjs` makes a short film in Lumibelle from a blank project and records a demo of it: a story goes in as source text, then outline, screenplay, assets, reference images, an environment reel with keyframes, shots, references, prompts, H3 takes and a cut. The clicks are recorded with a caption band and a visible cursor. The long AI waits happen off camera and appear as a badge with the real elapsed time, such as "12 minutes later", over the frozen last frame of the clip before, so what was on screen stays readable. The result is `demo.mp4`, with the title card, a cold open and the ending taken from the exported film.

It drives the UI through the same selectors as the browser tests, so a UI change that breaks a test can break the recorder too.

## Requirements

- Release builds: `dotnet build src/Lumibelle.Web -c Release` for real models, and `dotnet build tests/Lumibelle.BrowserHost -c Release` for the mocked rehearsal.
- FFmpeg and FFprobe on `PATH`, and Microsoft Edge (or pass `--channel chrome`).
- For a real run: ComfyUI and vLLM running as configured in your **AI settings**, with the models used here installed and tested.

## Rehearse against mocked models

The browser-test host answers every AI request instantly with canned results. Use it to check the flow after a UI change; the canned story has no location, so the reel and scene phases use their fallbacks.

```powershell
node tools/demo/record-demo.mjs --serve mock --url http://127.0.0.1:5197 --source story.txt `
  --text-model "Mock script writer" --vision-model "Alternate mock" --text-label "The mock model" --out artifacts/demo-mock
```

## Record with real models

```powershell
node tools/demo/record-demo.mjs --serve web --source story.txt --text-model gemma --text-label "Gemma 4"
```

`--serve web` starts the Release build on a scratch library in `artifacts/demo/library`. Only `ai-settings.json` and `generation-setups.json` are copied into it from `App_Data` (or `--seed`); your projects are never opened. The scratch instance uses its own port, so it can run beside your usual Lumibelle, but both share the ComfyUI and vLLM queues: leave them idle while recording.

## Checkpoints, resuming and retakes

Before every phase, the recorder stops its server, saves the scratch library to `checkpoints/<phase>` and starts it again. SQLite and JSON files are copied; media files are hardlinked, since Lumibelle never rewrites them, so checkpoints take little space.

| Want to | Run with |
| --- | --- |
| Continue after a failure | `--from <phase>`: restores that checkpoint and runs from there, keeping earlier clips. |
| Redo one section's recording | `--retake <phase>`: restores, re-records that phase only, keeps every other clip and reassembles. |
| Stop early | `--until <phase>` |
| Rejoin a job that is still running or paused | `--from <phase> --continue`: uses the library as it is, skips that phase's recording, waits for its job (resuming a paused screenplay batch) and keeps its clip. `--started-at HH:MM` gives the badge the real start time. |
| Change the text model for the phases still to run | `--set-model <regex>` with `--from`. |
| Look around afterwards | `--keep-serving` leaves the scratch Lumibelle running. |

Phases come in pairs: one starts an AI job and waits for it, the next reviews the result. Retaking a review phase calls no model at all.

| Starts a job | Reviews it |
| --- | --- |
| `source` (analysis) | `outline` |
| `script` (screenplay batch) | `screenplay` |
| `assets` (extraction) | `library` |
| `images` (reference batch) | `approve` |
| `reel` (camera move), `reel-render` (H3 reel), `keyframes` (view suggestions) | `picks` |
| `scenes` (shot drafting) | `coverage` |
| `shots` (reference suggestions) | `references` |
| `prompts` (composition) | `prompt-review` |
| `takes` (H3 takes) | `takes-review` |
| `refine` (one take refined from video at full size) | `compare` (draft and final side by side; the final goes into the cut) |

`create`, `switch` (the text model switched mid-project, recorded on its own) and `cut` stand alone.

## Fitting two minutes

`demo.mp4` is assembled from cached parts, so only clips that changed are encoded again, and `--assemble-only` rebuilds it in seconds without running any phase. Every assembly prints how long each part is.

- `--omit` lists phases whose clips stay out of the video. By default these are the job-starting phases `create,script,assets,reel-render,keyframes,shots,prompts,takes`; their elapsed-time badges stay, and the review clip after them shows the result. `--omit ""` keeps everything.
- `--speed` plays the UI recordings faster (default 1.75). The film keeps its real speed and sound.
- `--speed-for compare=1,switch=1.5` sets single phases' speeds, for clips that play video or need reading.
- `--card-seconds`, `--timelapse-seconds`, `--cold-open` (0 leaves it out) and `--film-tail` set the length of each badge (default 2), its time-lapse, the opening clip and the ending.
- `--slow-note` and `--end-models` change the note on the first long badge and the models line on the end card.

## Options

The header of `record-demo.mjs` lists them all. The most useful:

- `--shot <regex>` chooses the shot that is followed on camera through references, prompt and takes.
- `--environment <regex>` chooses the location the reel is made from (default: the living or drawing room).
- `--scenes 4,6,7,8` drafts shots for those scenes only, and `--film-shots 2,8,11` gives references, prompts and takes to those shots only.
- `--vision-model <regex>` is used where an assistant needs a model that reads images.
- `--wait-minutes` limits a single AI wait (default 240).
- `--film-tail` sets how many seconds of the finished film end the demo (default 15).
- `--headed` shows the browser while it works.

The submitted demo was assembled with:

```powershell
node tools/demo/record-demo.mjs --assemble-only --text-label "Gemma 4" `
  --omit create,outline,script,assets,library,approve,reel-render,keyframes,coverage,shots,references,prompts,prompt-review,takes,takes-review,refine `
  --speed 2 --speed-for compare=1,switch=1.5,scenes=3,cut=3,images=3 --card-seconds 1.3 --timelapse-seconds 2 --cold-open 0 --film-tail 16
```

Clicks right after a page loads can be lost before Blazor wires the button, so phases that start jobs confirm the job started and retry. Some assistants finish behind **Review changes** or **Apply anyway** rather than applying directly; the waits treat that as finished.

## Manual media

`record-manual-media.mjs` re-records the manual's clips and screenshot from a Lumibelle running on a copy of the finished demo library, so the manual shows the public-domain "The Open Window" project:

```powershell
node tools/demo/record-manual-media.mjs --url http://127.0.0.1:5192 --route the-open-window --out artifacts/manual-media
```

It records `shots-take-review`, `assets-create-prop` and `assets-edit-image` in parts at 1856×1028 (the prop and edit clips queue one Krea 2 job each and leave the wait out), and `ai-settings-comfyui-connection.png` at 2×. Join and scale the parts with lumibelle-web's `scripts/cut-clip.sh`, using `CLIP_CROP=1856:1028:0:0` since the recordings have no browser chrome, then publish them with DVC as that repository's README describes. Run it on a copy of the library: creating the prop and editing the room change the project.
