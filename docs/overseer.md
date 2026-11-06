# Overseer: design

**Status: design only. Nothing described here is implemented.** "Overseer" is a working name.

An overseer is an AI agent that reviews a project's shots and takes, finds continuity errors, proposes fixes, and carries them out once the director approves. This design comes from doing that job by hand on *The Open Window* (59 shots, about 6.5 minutes): a Claude Code session drove the real UI with Playwright scripts and read `project.sqlite` directly, and the director approved each wave of renders. The scripts and the notes from that film are kept outside the repository; this document records what they needed.

## Summary

- **One tool layer, two drivers.** A Core service layer exposes the operations the scripts needed. An MCP endpoint lets an outside agent such as Claude Code call it. A built-in guided review calls the same layer through Lumibelle's own AI connections. Both write proposals into the same plan.
- **The MCP server has to run inside the Lumibelle process.** The library lease, the AI queue's worker lock and the per-project save locks all belong to that process.
- **Approval is enforced in Lumibelle, not in the client.** No MCP tool changes a project or starts a GPU job. Tools can only add steps to a plan; the director approves the plan in the app.
- **The built-in overseer is a guided workflow, not an agent loop.** No provider path in Lumibelle has tool calling today, and all of them accept still images only. App code holds the loop and asks one narrow, checkable question at a time. The free-form agent is the MCP route, where the client brings its own loop.
- **Many useful checks need no model.** They come first in the build order.
- **Expect it to shorten the rounds, not to replace watching the cut.** On this film the director's 13 notes on two rough cuts were almost all problems the frame review had not raised, and the voices problem was only audible.
- **A Claude subscription can drive this from Claude Code.** Lumibelle itself must not sign in to Claude or hold its tokens. Details and quotes are in [Claude subscription](#7-using-a-claude-subscription).

## 1. Tool set

What the scripts did, which app service each tool should call instead, and what is missing. Paths are under `src/Lumibelle.Core/` unless noted.

Project data is split across two stores, which shapes every tool:

- `IShotStore` (`Services/Shots/FileShotStore.cs`) holds shots as coverage (title, scene, duration, dialogue, `SelectedTakeId`), takes and Trash.
- `IProductionStore` (`Services/Production/FileProductionStore.cs`) holds what the Shots studio edits: the prompt, references, character voices and shot frames (`ShotProductionContent`), plus per-setup output settings (`ProductionComposition`).

The merged "shot as the director sees it" exists only in UI state (`ProductionStudio.razor.cs`). The tool layer needs a Core read model that does that merge.

### Read

No model, no GPU, no change to the project.

| Tool | The scripts did this by | Should call | Missing |
| --- | --- | --- | --- |
| `list_shots`, `get_shot` | Reading `parts` rows from `project.sqlite` | `IShotStore.LoadAsync`, `IProductionStore.LoadAsync`, scenes from `IScriptStore.CaptureSourceAsync` and `ScriptStructure.Sections`; prompt status from `PromptReferenceFreshness.Compare` and `ProductionPolicy.PromptWarning` | The merged read model |
| `list_takes`, `get_take` | Reading take rows and their `snapshot` | `ShotDocument.Takes`; `VideoSnapshot` carries the prompt, voices, shot frames and references the take was rendered with; `TakeInputChanges.Compare` says what changed since | Nothing |
| `get_frames` (contact sheet, first and last frame, or one index) | `ffmpeg` on `video.mp4` | `IShotStore.OpenAsync(project, takeId, kind, frame)`, which reads through `TakeFrameReader`; sheets through `AssetImageEvaluator.ContactSheetAsync` | A sheet builder for takes with labels |
| `get_scene_sheet`, `get_neighbour_sheet` | `boundaries.py`: first and last frame of every selected take, scene by scene | Same frame reader | The two sheet layouts |
| `get_audio_levels` | `ffmpeg -af volumedetect,silencedetect` | `IProductionMediaTools` | No audio analysis exists in Core |
| `get_cut`, `locate_cut_time` | `notes_map.py`: summed take lengths to turn a timecode into a shot and frame | `ICutStore.LoadAsync`; clips carry their frame ranges | The timecode lookup |
| `list_assets`, `get_asset_image`, `find_reference_usage` | Scanning shots for an `assetId/imageId` | `IAssetStore`, then a scan of `ShotProductionContent` and reels | The usage scan for images. Reels have one in `ReelReplacement.PlanAsync` |
| `get_queue` | Reading `ai-jobs/queue.json` in a retry loop | `AiJobCoordinator.View` and `Changed` | Nothing |
| `run_checks` | Several one-off scripts | See [Checks that need no model](#4-checks-that-need-no-model) | The checks |

### Write

Changes a project. No GPU. Each of these becomes a plan step and runs only after approval.

| Step | The scripts did this by | Should call | Missing |
| --- | --- | --- | --- |
| Set a prompt and mark it reviewed | Pasting into the prompt editor, **Mark reviewed** | `IProductionStore.SaveAsync`, then `AcceptAsync` | Nothing. `AcceptAsync` already refuses an invalid prompt or missing media |
| Edit references: add, replace or remove a picture, remove a reel, set a voice, add or remove a shot frame | The **Manage references** dialog | `IProductionStore.SaveAsync` after `H3Policy.Validate` and `ReferenceVideos.Validate` | The apply logic is UI-only (`ShotReferenceEditor.Apply`, `ProductionStudio.ApplyReferences`): frame readability, stale-input checks, RefMod and keyframe preparation. It needs moving into a Core service that both the dialog and the tool layer call |
| Replace a picture in chosen shots | One shot at a time | `IProductionStore.ReplaceShotContentAsync` | Exists for reels only (`ReelReplacement`). Needs the same for images |
| Use a take | **Use this take** | `IShotStore.SaveAsync(..., "Choose production take")` | A small `SelectTakeAsync`; today the recipe is in `ProductionStudio.ToggleSelectedTake` |
| Trim a take | The trim editor | `IShotStore.TrimTakeAsync` | Nothing. It is idempotent by `ResultId` and makes a new take |
| Point the cut at the selected takes | **Choose takes…**, row by row | `ICutStore.SaveAsync` with clips from `CutClip.From` | The chooser logic is in `CutStudio.AddChosen` |

Exporting a cut (`ICutExporter.ExportAsync`) changes nothing in the project and uses the CPU, not the GPU. It can run without a plan step, and is logged.

There are no delete tools. The overseer cannot remove shots, takes, images or assets, and cannot empty Trash.

### GPU-spending

Each becomes a plan step with a stated cost, and runs only after approval.

| Step | The scripts did this by | Should call | Notes |
| --- | --- | --- | --- |
| Generate takes | Setting **Resolution** and **Takes**, **Generate takes**, then polling the queue file | `AiVideoJobCapture.CaptureCompositionWithPresetAsync` or `CaptureCompositionAsync`, then `AiJobCoordinator.EnqueueAsync` | The capture runs the same preflight as the button. A refusal such as "Refresh Video models…" comes back as a blocked step, not a retry loop |
| Edit an asset image | The image composer | `AiImageJobCapture.EditAsync`, then `EnqueueAsync` | The studio assembles the request in UI code. Set `ReferenceEditRequest.PendingReview` so the result waits for the director; standalone edits currently join the gallery at once |
| Refine takes | Not done on this film | `RefinementBatchWorkflow.CreateAsync` with `StartNow: false` | A held batch is already an approval point |
| Model review requests on ComfyUI | Not applicable | The AI queue | These use the same single ComfyUI lane as renders, so a built-in review also counts as GPU time |

## 2. MCP server

### Why it has to run in the app

A separate process cannot do this job against a library that is open:

- `WorkspaceOwnership` opens `.lumibelle.lock` in the data and project folders with `FileShare.None` before workers start (`Services/ApplicationPaths.cs`). A second owner fails with "This library is already open in another Lumibelle process".
- The AI queue has its own `worker.lock` (`Services/AI/FileAiJobStore.cs`), and `AiJobCoordinator` is a hosted service in the app. Only that process can run jobs.
- Each save is serialized by `ProjectFiles.LockAsync`, a `SemaphoreSlim` per path inside the process (`Services/Story/ProjectFiles.cs`). A second process would bypass it.
- The stores are singletons with a per-process document cache.

A second process started with `exclusive: false` could read, since reads never write to the database ([SQLite storage](sqlite-storage.md)). That covers too little to be worth a second design: it could not enqueue a job, and its writes would be unsafe.

So the MCP server is an endpoint in the running app, using the same service provider.

### Transport

- **Web host:** a Streamable HTTP endpoint at `/mcp`, mapped beside `/media` in `WebHosting.MapLumibelleWeb`. The official C# SDK package `ModelContextProtocol.AspNetCore` provides this; its current stable version is 2.3.1, released 2026-11-03, with a `net10.0` target ([NuGet](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore)).
- **Claude Code** connects to HTTP servers directly ([Claude Code MCP](https://code.claude.com/docs/en/mcp)):

  ```bash
  claude mcp add --transport http lumibelle http://localhost:5183/mcp --header "Authorization: Bearer <token>"
  ```

- **Desktop host:** it does not start Kestrel ([shared hosts](shared-hosts.md)), so there is no HTTP endpoint to add. It would need a loopback listener of its own. Leave the desktop host out of the first version.
- **Clients that only speak stdio:** a small relay executable that forwards stdio to the running app's endpoint. Not needed for Claude Code; build it only when a client needs it.

### Access

The web host has no sign-in today; it relies on `AllowedHosts` and a localhost address. An MCP endpoint is a larger surface than media URLs, so:

- Off by default. A switch in **AI settings → Connections** turns it on for this library.
- A bearer token generated there, stored through `ISecretProtector` like other credentials, and shown with the command above.
- `Origin` validation and localhost binding. The MCP specification requires the first and recommends the second and authentication for local servers ([transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)).
- Project text reaches the agent through tool results, and a project can be imported from a package someone else made. Because tools can only propose, text that tries to instruct the agent can at most produce a proposal the director sees.

### Tools

About twenty tools, named for the director's vocabulary. Shots are addressed by number and id together, because the notes say "#31" and the stores use Guids.

- **Read:** `list_projects`, `list_shots`, `get_shot`, `list_takes`, `get_take`, `get_frames`, `get_scene_sheet`, `get_neighbour_sheet`, `get_audio_levels`, `get_cut`, `locate_cut_time`, `list_assets`, `get_asset_image`, `find_reference_usage`, `run_checks`, `get_queue`. Marked `readOnlyHint`, which also lets Claude Code run them in parallel.
- **Propose:** `create_plan`, `add_finding`, `propose_step`, `revise_step`, `recommend_take`, `submit_plan`. These write only to the plan.
- **Follow:** `get_plan`, `wait_for_plan`. The second returns when the plan's state changes or after a short timeout, so no call stays open for a whole render.

There is no `execute` or `approve` tool. See [Approval](#5-approval-and-order).

An MCP prompt, `review`, carries the review procedure (run the checks, read each scene's sheet, compare prompts with the continuity sheet, propose). Claude Code lists MCP prompts as commands.

### Images in results

Claude Code shows PNG, JPEG, GIF and WebP tool results to the model inline, and applies its MCP output limit, 25,000 tokens by default, to tools that return images ([Claude Code MCP](https://code.claude.com/docs/en/mcp#images-in-tool-results)). So:

- One sheet per call, as JPEG, no larger than the review needs. The hand prototype used 480-pixel tiles, six to a sheet.
- A tool returns an image only when asked; `list_takes` returns ids and facts.
- How that limit counts image bytes is not documented, so measure it with real sheets in the first build before fixing sizes.

## 3. Built-in overseer

### What the code supports today

Checked in `Services/AI/`:

| Capability | State |
| --- | --- |
| Tool or function calling | None. `ChatOptions` is built in two places (`TextGenerationOptions.cs`) and never sets tools. The Claude Code provider starts the CLI with `--tools ""` and `--strict-mcp-config` and rejects a session that reports any tool or MCP server (`ClaudeCodeClient.cs`). The Codex provider disables its tool features and replaces the MCP table with an empty one (`CodexClient.cs`). The ComfyUI client rejects any message part that is not text or a PNG |
| Image input | All five backends accept still PNGs (`TextVisionPolicy.SupportsBackend`). Whether a given model accepts them comes from the provider's catalog, a saved model test, or a connection override; the code treats a model name as no evidence |
| Video or audio input | None. Video reaches a model only as sampled frames, and existing prompts say so ("You did not watch the video or hear audio", `AssetReelEvaluator.cs`) |
| JSON constrained by a schema | OpenAI-compatible connections only: native vLLM, or a standard server that passed the schema test (`AiTextJobCapture.Structured.cs`). OpenRouter, ComfyUI, Codex and Claude Code get the format in the prompt and are validated afterwards, with `AiJsonReply.Latest` reading the last complete JSON answer from a model that reasons aloud |
| ComfyUI text | One request per job through `TextGenerate`, no streaming, thinking disabled, up to 36 images letterboxed to at most 1,024 pixels a side (`ComfyTextVision.cs`). ComfyUI jobs of every kind share one lane with concurrency 1 (`AiJobCoordinator.cs`), so a review request waits behind renders and renders wait behind it. There is no logic to unload or swap models between jobs |
| Capacity | The ComfyUI model test measures memory per prompt and reply token and derives a prompt capacity for that model on that server (`ComfyTextCapacity.cs`). Text jobs estimate images at one token per 32×32 pixels for Qwen models and at most 280 for Gemma 4 ([development notes](development.md)) |
| Model quality | No test measures reasoning or instruction following. Tests cover speed, memory, image input, schema support and the system prompt |

The closest existing features are the asset evaluators. `AssetImageEvaluator` puts candidates on one contact sheet, asks for a fixed set of checks with `Pass`, `Concerns` or `CannotJudge`, parses strictly, binds the result to the sheet's hash, and never changes the library. `ScriptSequenceReview` requires quoted evidence and verifies each quote against the text before it accepts a finding. The guided review reuses both patterns.

### Guided workflow against a free-form loop

| | Free-form agent loop | Guided workflow |
| --- | --- | --- |
| Who decides the next step | The model | App code |
| Needs from the model | Tool calling, long context, planning over many turns | One image or one text, a few closed questions, short JSON |
| Fits the providers | Would need tool calling added to OpenRouter and OpenAI-compatible connections. ComfyUI text cannot do it. The Codex and Claude Code providers are built to forbid it | Works on every backend that passes the image test |
| Fits the queue | Poorly. A job is one captured request that can be replayed, and recovery may not run inference again | Each question is one ordinary job |
| When the model is weak | Errors compound: a wrong tool call or a lost thread spoils the run | An error stays in one answer, and `CannotJudge` is allowed |
| What it can find | Whatever the model thinks to look for | Only what a question asks about |

Recommendation: build the guided workflow inside Lumibelle, and do not build an agent loop there. A director who wants a free-form agent runs one as an MCP client, where the loop, the context management and the model are the client's. The two routes meet in the plan.

The guided workflow's limit is the last row. It checks a take against the continuity sheet and its neighbours. It will not notice that a story beat is impossible, which is where the strong model did better on this film (see [Limits](#6-what-it-cannot-judge)).

### The questions

Each stage is a new text job kind handled like `AssetImageEvaluation`: captured inputs, strict parsing, a saved result, nothing applied.

1. **Describe the chosen images.** One image per request: visible face, hair and costume for a character; fixed layout and changeable state for an environment. This is the raw material for the continuity sheet, and the director edits the result. Where it is stored belongs to the sheet's own design: `AssetImage.VisualDescription` has the right shape, but the code marks it retired and nothing reads it.
2. **Prompt against the sheet.** Text only, one shot per request: the prompt, the sheet entries for the subjects in that shot, and the scene's state. The answer lists conflicts and required details that are absent, each with the exact words quoted from the prompt. The app drops any finding whose quote is not in the prompt.
3. **Take against the sheet.** One contact sheet of the take and five to eight closed questions generated from the sheet: "What colour is Vera's blouse?", "Is the table lamp lit: yes, no, cannot tell?", "How many pairs of boots are visible?". The app compares answers with the expected values. The model is not told the expected value.
4. **Take against its neighbours.** One sheet holding the end of the previous shot, the start and end of this one, and the start of the next, with questions about what must match across the cut: who is seated where, the light, which side the window is on.
5. **Propose the fix.** For a finding the director keeps, ask for prompt edits as pairs of exact old text and new text. The app applies a pair only when the old text occurs exactly once, the rule the hand-written fixes used. A pair that fails is shown as failed, not guessed at.

### Making a weak model's answers usable

- **Gate by tests that exist.** A model must have passed the image test, as the evaluators already require (`requireTest`). Sheet sizes come from the measured capacity, so a request that will not fit is refused before it is queued.
- **Measure before trusting.** *The Open Window* has known answers: 22 shots retaken for stated reasons, and 13 director notes with causes. Turn those takes and their expected findings into a fixture, and report hits and false alarms per question type for a model before its findings are shown as anything but unverified. No such measurement exists yet, so this design makes no claim about how any self-hosted model will do.
- **Run reviews as a block.** On one GPU the review and the renders share the ComfyUI lane. Run the questions between render waves, started by the director with the request count shown first, as **Prepare run** and **Run** work for [writing probes](content-probes.md).
- **Keep stronger models on the same path.** The same jobs run through OpenRouter, an OpenAI-compatible server, Codex or the Claude Code provider. Nothing in the workflow is specific to a weak model, so a better self-hosted model later needs no redesign.

New `AiJobKind` values make the queue unreadable to older builds, as the probes document notes for its own kind.

## 4. Checks that need no model

These read stored data and media. They are fast, give the same answer every time, and several would have changed the course of this film.

### From project data

| Check | Uses | On this film |
| --- | --- | --- |
| A shot with no references, or a character in the shot with no picture | `ShotProductionContent`, `Shot.Characters` | #56 had none |
| A speaker with no voice; a voice left as None or unselected | `CharacterVoices.Validate`, dialogue speakers | All 59 shots, found only by ear |
| A character whose takes do not all use the same voice recording | `VideoSnapshot.Shot.CharacterVoices` | Checked by script after voices were added |
| A duration the refine step will reject | `H3Policy.Frames`, `MotionReferencePolicy` (2 to 15 seconds, 362 frames) | #57 at 14.5 s becomes 15.083 s |
| A stage direction stored as a spoken line: dialogue that is all parentheses or asterisks | Shot dialogue | #4, #28 |
| Stray JSON, "placeholder" or repeated sections in a prompt | `ProductionPolicy.ValidatePrompt` covers sections and labels; the rest is new | #38 |
| A prompt never reviewed, or reviewed before its references changed | `PromptReferenceFreshness.Compare` | |
| A selected take rendered from an older prompt or older references | `TakeInputChanges.Compare` | |
| A shot frame whose source take is no longer that shot's selected take, or is in Trash | `ShotContinuityFrame.TakeId`; the reference editor already shows this hint | |
| A cut clip that is not its shot's selected take; a shot missing from the cut | `ICutStore`, `Shot.SelectedTakeId` | After 46 reselections the cut still showed the old takes |
| A reference image that has been superseded, and everything made from it | See below | The room picture with three pairs of boots |
| The order shots must be rendered in, and any cycle | The shot-frame graph, see [Approval and order](#5-approval-and-order) | #9 before #7 before #11 |

**Superseded references.** An edited image records the image it was made from (`AssetEditMetadata.SourceImageId`), but nothing marks the old one as replaced. Add a "replaced by" mark the director sets. The check then lists:

- shots and reels that still reference the old image;
- takes whose snapshot used it;
- takes that took a shot frame from one of those takes, and so on down the chain.

The last item is what made the boots persist: a frame from an earlier take carried them into shots that no longer referenced the old picture.

### From media

| Check | How | Catches |
| --- | --- | --- |
| First and last frame of each selected take, scene by scene | `TakeFrameReader`, a sheet | A human or a model reads it. It found the top hat in #45 |
| Each take between its neighbours | Same | The check that caught the most on this film |
| Level per take against the film's median; silent stretches | `ffmpeg` `volumedetect` and `silencedetect` | A take much quieter than the rest; dialogue that never starts |
| A jump inside a take | `ffmpeg` scene-change score | A take that opens on another image and cuts to the real shot, as #16 did |
| A take that opens on one of its own references | Compare the first frames with each attached shot frame and environment picture at low resolution | #16 and #40 opened on the #14 and #38 wides; #52 opened on the afternoon room |
| Black or frozen frames; a take whose length differs from the shot's | `ffmpeg`, `VideoInfoAsync` | |
| A take whose brightness or colour balance is far from its scene's | Frame statistics; `ReelFrameSelector` already computes luminance | Possibly golden daylight between dusk shots |

The jump, opening-frame and colour checks need thresholds, and none has been tried. Tune them on this film's known cases and report them as "worth a look", never as errors. A dissolve from the wrong image will not show as a jump; the opening-frame comparison is meant for that case. The last row is a guess that statistics will separate dusk from daylight, and it may not.

Audio analysis and scene detection do not exist in Core today. Frame extraction, the video probe, the prompt and voice validators and the freshness comparison do.

## 5. Approval and order

**Rule: nothing that spends GPU time or changes a project runs without the director's approval, given in Lumibelle.**

### Why approval lives in the app

An MCP client has its own permission prompts, but Lumibelle cannot see whether they were shown. A client can be set to approve tools automatically, and another client may not prompt at all. So the server offers no tool that acts. Proposing is all a client can do.

Claude Code does honour a per-tool mark, `anthropic/requiresUserInteraction`, that forces its prompt on every call in every permission mode ([Claude Code MCP](https://code.claude.com/docs/en/mcp#require-approval-for-a-specific-tool)). It is specific to that client, and a terminal prompt cannot show a prompt diff beside frames. It could later back an optional "approve from the client" setting. It is not the gate.

### The plan

A plan belongs to one project and is stored with it, as a new document kind in `project.sqlite`. It holds:

- **Findings:** what is wrong, on which shot, with evidence: quoted prompt text, a frame, a check result, or the director's note and its timecode.
- **Steps:** one change each, linked to the findings it answers. Each step records the versions it was written against, the before and after values, who proposed it (the MCP client's name or the built-in model), and for GPU steps the take count, resolution and duration.
- **Waves:** steps grouped by what they depend on.
- **A log:** who approved what and when, and the job ids that ran.

### Reviewing a batch

One page lists the whole plan, grouped by wave:

- A prompt change is shown as a diff (`PromptComparison` exists for this).
- A reference change shows the pictures and frames added and removed.
- A render shows takes, resolution and seconds of footage, and an estimated time where the app has timings for that setup.
- The plan's total footage and take count are at the top, and are a cap: execution stops when it is reached.

The director can accept, reject or edit each step, then approves the rest together. A rejected step removes the steps that depend on it from this run and says so.

### Two gates

Which take to use cannot be known before the takes exist, so approval happens twice:

1. **Plan approval.** Covers the edits and the GPU budget for every wave.
2. **Picks.** When a wave's takes are ready they are shown between their neighbours. The overseer may recommend one with a reason; the director picks. A pick is a change to the project, so it is the director's click, or one **Accept recommendations** for the wave.

Later waves start as their picks arrive. Shots that depend on nothing unpicked run straight away, so the director is only waited on where a frame really depends on the choice.

### Dependency order

Shot B depends on shot A when B has a shot frame, or a start frame, taken from one of A's takes. `ShotContinuityFrame` holds a take id and a frame index, so the graph is in the data. Editing an asset image adds an edge from that edit to every shot that will use the new image.

- **Waves** are the graph sorted so that sources come first. On this film that gave #9, then #7, then #11; and #31 before #33, #34 and #37.
- **A frame from a take that does not exist yet** is written as a position (last frame, first frame, or a fraction of the take). It is resolved when the director picks the source take, and the pick page shows which shots will use the frame so it can be adjusted there.
- **Retaking a source affects its dependants.** The plan lists every shot that holds a frame from the old take. Whether to retake them is the director's decision; on this film removing the boots everywhere meant about 20 more shots, and the first pass tested three.
- **Cycles** can occur, since a frame may come from a later shot. They are reported and left for the director.
- **Refinement** has the same graph already (`RefinementBatchWorkflow.RefinedFrames.cs`), including waiting on a source item and cycle detection. Shots chained by frames should go into one refinement batch with **Use refined frames from earlier clips**, because the swap to a refined frame only happens inside a batch.

### Applying steps

- **Stale steps.** Before a step runs, its recorded versions are compared with the stores. If the shot changed since the proposal, the step is held and shows what changed, as `AssistedApply` does for assisted results, with **Apply anyway**.
- **Open tabs.** Saves use optimistic versions, and the stores raise no change events. A Shots tab with unsaved edits will get "Save failed · draft retained" after a plan step saves the same shot; a clean tab will not see the change until it reloads. Add a change event to the production and shot stores so clean editors reload. Until then the plan page should say which shots it is about to change.
- **Undo.** Edits go through the existing save paths, which keep prompt revisions and recovery versions. The plan keeps each step's before value so one step can be reverted. Takes are never removed.
- **The queue.** Renders are ordinary jobs, visible in AI activity with the plan named as their origin. One request per shot can be active at a time (the job lock key), so the executor feeds the queue as locks free. A job that needs attention holds its dependants and leaves the rest running. The director can pause the plan.

## 6. What it cannot judge

**It sees stills and it hears nothing.** No provider path in Lumibelle sends video or audio to a model. The hand review looked at six frames per take at 0.2 megapixels.

What that meant on this film:

- **Voices.** No shot had a voice reference, so each shot invented its voices. The director heard it on first viewing. No frame check could have. A data check would have caught the cause, which is why it is in the list above.
- **The director's notes.** After two rounds of frame review and three waves of retakes, the director's notes on rough cuts 2 and 3 listed 13 problems. The frame review had raised none of them first, apart from the extra boots, which were known and only partly fixed. Tracing the notes, the agent added four smaller problems of its own.
- **Most of the 13 were visible in stills.** The review missed them because they were not mismatches against a reference. They were errors of story logic across shots: a table gone because someone stands where it goes, two people looking away from the window they are meant to watch, brothers climbing in 40 seconds after their father, men who jump from across the room to behind a chair, a room that is suddenly empty.
- **Some started in the reference itself.** The room picture had three pairs of boots. A review that treats the references as the truth passes every shot that matches them.
- **Motion needs the cut.** A gate that swings through a man is only certain in motion.

What to expect, stated plainly:

- The overseer finds data errors reliably, and errors against the continuity sheet (identity, costume, light, a prop that is plainly wrong) often. On this film's first pass that kind of review led to 22 of 59 shots being retaken, and prompt changes fixed all 22.
- It will pass shots that are wrong in ways a viewer notices at once. The director still has to watch each cut with sound.
- So the director's notes are a first-class input. `locate_cut_time` turns "3:54, table disappears" into shot #31 and a sheet of the frames around it. Finding the cause, writing the fix and listing the dependants is the work the agent did well here, and it is tedious for a person.
- A guided review with a weak model will find less than the hand review did, and how much less is unknown until it is measured.

If a provider later accepts video or audio, that is a new input to add and to test per model, as image input is today. This design does not depend on it.

## 7. Using a Claude subscription

Checked on 2026-11-06 against Anthropic's published terms. This is a reading of those pages, not legal advice.

**What the terms say**

- OAuth sign-in "is intended exclusively for purchasers of Claude Free, Pro, Max, Ultra, Team, and Enterprise subscription plans and is designed to support ordinary use of Claude Code and other native Anthropic applications." ([Claude Code legal and compliance](https://code.claude.com/docs/en/legal-and-compliance#authentication-and-credential-use))
- "Anthropic does not permit third-party developers to offer Claude.ai login into their own applications, or to route requests through Free, Pro, Max, or Ultra plan credentials on behalf of their users. Moreover, developers may not collect, store, or intermediate Claude.ai credentials or session tokens." (same page)
- That restriction does not "prevent an end user from signing in to the unmodified Claude Code binary with their own Claude subscription, including where a platform hosts Claude Code". (same page)
- Running Claude Code in a product "requires agreeing to our Commercial Terms of Service", with two conditions: the binary is not modified and no built-in sign-in method is removed or restricted, and the product does not "pay for, resell, or intermediate Claude usage" for its users. (same page, "Can customers offer Claude Code in their products?")
- "Advertised usage limits for Pro, Max, and Ultra plans assume ordinary, individual usage of Claude Code and the Agent SDK." (same page)
- The Agent SDK is the same: "Unless previously approved, Anthropic does not allow third party developers to offer claude.ai login or rate limits for their products, including agents built on the Claude Agent SDK." ([Agent SDK overview](https://code.claude.com/docs/en/agent-sdk/overview))
- With a plan sign-in, "the Claude Agent SDK and `claude -p` still draw from your subscription limits." The same article says a previously announced change to this is paused. ([Claude Help Center](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan); its latest dated notice is 2026-10-07)
- The Consumer Terms forbid accessing the services "through automated or non-human means, whether through a bot, script, or otherwise", with this exception: "Except when you are accessing our Services via an Anthropic API Key or where we otherwise explicitly permit it". ([Consumer Terms](https://www.anthropic.com/legal/consumer-terms), effective 2025-10-08)

**What that means here**

| Route | Verdict |
| --- | --- |
| The director runs Claude Code, signed in with their plan, and Claude Code calls Lumibelle's MCP endpoint | Fine. This is ordinary use of Claude Code. Lumibelle is a tool server and never sees a Claude credential |
| Lumibelle runs the installed `claude` CLI for the guided review, as the [Claude Code provider](claude-code-provider.md) already does for text | The pages above describe this case and do not forbid it: the user signs in to the unmodified binary themself, Lumibelle reads no credentials, and usage is the user's own. Two cautions follow |
| Lumibelle offers "Sign in with Claude", stores a plan token, or calls Claude with plan credentials | Not permitted |
| Lumibelle calls Claude directly | Needs an API key from the Claude Console or a cloud provider. Lumibelle has no such connection today |

Cautions on the second route:

1. **The Commercial Terms line.** Lumibelle is distributed to other people, and the page says running Claude Code in a product requires agreeing to the Commercial Terms. Whether that applies to an app that starts a CLI its user installed is not something the page settles. This question already applies to the existing provider. The page's own route for it is Anthropic's sales contact.
2. **Volume.** A guided review of a 59-shot film is a few hundred requests. Plan limits assume ordinary individual use, so each run should be started by the director with the request count shown, and never scheduled. The Claude Code lane already pauses when a usage limit is reached.

Two further points:

- **Do not run an agent loop through the provider.** `claude -p --mcp-config` could point the CLI at Lumibelle's own endpoint. That would undo the provider's isolation, which gives the CLI no tools and rejects a session that has any. A director who wants Claude's agent loop runs Claude Code themself.
- **A change to watch.** The CLI's documentation says `--bare` "will become the default for `-p` in a future release", and that bare mode "doesn't use your subscription login" ([run Claude Code programmatically](https://code.claude.com/docs/en/headless)). The existing provider uses `-p` without `--bare`. When that default changes, plan sign-in through the provider will need retesting.

The same reasoning applies to Codex: Lumibelle uses the installed CLI's own sign-in and reads no tokens ([Codex provider](codex-provider.md)). Its terms were not checked for this design.

## 8. Build order

Each step is useful without the ones after it.

1. **Checks and sheets in Core, with a report page.** The data checks, audio levels, scene and neighbour sheets, and the cut-against-selection comparison. No model, no MCP, no plan. This is the smallest useful step: on this film it would have reported the missing voices on all 59 shots, #56's missing references, #57's duration and the cut ignoring 46 picks, before any render. Everything later reads its output.
2. **Read-only MCP endpoint.** The read tools, `run_checks` and `locate_cut_time`, behind the switch and token. Claude Code can then review a project and trace the director's notes without Playwright or reading the database. Nothing can change, so no approval machinery is needed yet. Measure image result sizes here.
3. **Plans for changes that cost no GPU.** The plan store, the review page, and steps for prompts, references, picks, trims and the cut. This is where the reference-apply logic moves from the dialog into Core, and where the store change events are added. Propose tools join the MCP endpoint.
4. **GPU steps.** Generate takes with waves and the pick gate; asset image edits that wait for review; the superseded-reference mark and its impact list.
5. **Guided review in the app.** The fixture from *The Open Window* first, then the prompt-against-sheet job (text only), then the take and neighbour jobs. This needs the continuity sheet, which is its own piece of work and also feeds prompt composition.
6. **Later.** Refinement batches from a plan; the desktop host; a stdio relay; approval from the client as an option; video or audio input if a provider offers it.

Steps 1 and 2 together replace the read half of the hand-run scripts. Steps 3 and 4 replace the write half.

## Decisions for the director

1. **Where approval happens.** This design puts it in Lumibelle only. Approving in the Claude Code conversation, as on this film, would be an option added later and would hold only for clients that honour the per-tool mark.
2. **Where plans live.** In the project database, so they travel with a package, or in application data, so they do not.
3. **The name.** "Overseer" in this document. The page the director uses is closer to "Continuity review".
4. **Whether to ask Anthropic** about the Commercial Terms line before the guided review uses the Claude Code provider at volume.

## Sources

Read on 2026-11-06:

- [Claude Code: legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)
- [Claude Code: run programmatically](https://code.claude.com/docs/en/headless)
- [Claude Code: MCP](https://code.claude.com/docs/en/mcp)
- [Agent SDK overview](https://code.claude.com/docs/en/agent-sdk/overview)
- [Claude Help Center: use the Claude Agent SDK with your Claude plan](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan)
- [Anthropic Consumer Terms of Service](https://www.anthropic.com/legal/consumer-terms)
- [MCP specification 2025-11-25: transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) and [tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)
- [ModelContextProtocol.AspNetCore on NuGet](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore)

In this repository: [Claude Code provider](claude-code-provider.md), [Codex provider](codex-provider.md), [LLM profiles](llm-profiles.md), [writing probes](content-probes.md), [SQLite storage](sqlite-storage.md), [shared hosts](shared-hosts.md), [prompt and reference freshness](production/prompt-reference-freshness.md), [development notes](development.md).
