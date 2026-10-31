# Refine from video

Choose **Refine from video…** on a take card or in take review, or select several
takes or reels and choose **Refine selected…**. One dialog serves a single clip
and a batch. Each clip starts from its captured scene, references and authored
dialogue. While the dialog prepares its rows, each visible MP4 is copied into
project reference storage independently of its take or reel.
Trim a clip longer than 15 seconds first; the visible clip must be at least two
seconds and fit the existing reference-video limits.
When a trim falls between H3 reference-frame boundaries, the dialog states how
much of the clip can be submitted. Review action and dialogue timing accordingly.

The default result uses Standard sampling, native resolution and one random seed.
Latents are not saved by default; they only serve the optional latent upscaling path. The ordinary configured H3 model supplies the final. The
prepared guide contains video only, and its soundtrack is not connected to
audio conditioning. Other enabled voice references and the authored dialogue
remain active. Review lip synchronization: visible mouth movements in the draft
do not guarantee alignment with newly generated speech.

Edit **Base prompt** and **Motion-reference instructions** separately. Shared
settings apply to every row; **Edit** overrides them for one clip. The preview
shows the actual combined prompt. Instructions are rendered once into the
existing `retention_analysis` section. Clearing instructions removes that added
text. A new promotion from an earlier promoted take replaces its managed guide
and instructions.

## Background preparation and batches

**Queue refinements** freezes each clip's reviewed settings, prompt, references
and a random seed in a saved batch (`refinement-batches.json` in the project).
Only then does background preparation begin. A bounded worker (two clips at a
time) runs the FFmpeg and picture preparation outside the single ComfyUI render
slot. Each clip records **Preparing media**, then **Ready**. After a restart,
unfinished preparation is rebuilt from the frozen settings. A clip is never
submitted twice: its job ID is fixed when it is queued, and enqueueing is
idempotent by ID and fingerprint. Preparation uses the guidance reviewed at
queue time, so later library or source edits do not change queued inputs.

A started batch submits one ready clip at a time as an ordinary generation job,
in batch order: a clip still in preparation holds back later ready clips, unless
it is waiting for another clip's refined take. **Waiting**, **Generating** and
**Completed** come from that job. The existing
multi-take batch still describes variations of one request; a refinement batch
only groups separate requests. If another request already holds the same shot
or reel, the clip waits instead of failing. Uncertain ComfyUI submissions keep
their existing recovery handling. **Retry** requeues a job that needs attention
with its saved request and seed. A cancelled clip is prepared again from its
frozen settings and seed under a new job ID. **Retry failed clips** applies the
same retry to every failed clip in one saved update, so they keep their batch
order. Changing resolution, prompt or seed
always creates a new request.

With **Use refined frames from earlier clips**, each earlier clip's request lists
the output frames later clips reference (`outputPolicy.keptFrames`, mapped from
the draft's visible frames onto the final's timeline). When its full lossless
archive is off, the graph picks each listed frame from the decoded images with
stock `ImageFromBatch` and saves it with `SaveImage`. The take stores these
PNGs as kept frames, and reading that frame returns the exact PNG instead of a
decoded MP4 still. ComfyUI without those nodes keeps the earlier MP4 behaviour.

Use **Manage references** to resolve reference limits or adjust the other inputs.
The managed motion guide remains video-only. The existing native Ref2VA contract
has no motion-strength parameter, so there is no strength slider.

Each clip creates an independent take in the same shot. Review it before selecting
it for production; **Use this take** on the batch page selects it. The original take, coverage, prompt and shared generation
setup are not overwritten. [Saved generation settings](generation-recipes.md)
explain captured inputs, native ComfyUI execution and retries.

Reels offer the same action in their menu and details. Their dialog defaults to
Detail (approximately 0.7 MP) and saves lossless frames for later keyframe use.
When the source reel already has picked keyframes, **Keep the picked keyframes**
is on instead: the final keeps exactly those frames as lossless PNGs (no full
archive) and the new reel gets the same keyframes, with their names, notes and
crops, at the same moments.
**Prefer this reel** records the final as the preferred version
(`replacedByReelId` on the source reel); shots keep their reel until replaced.
Native, 1080p-class and 1440p-class are also available. Presets smaller than the
source are hidden; larger sources default to the first suitable size.

## Feasibility gate and benchmark order

The first experiment compares existing Larry drafts and Standard promoted finals
against Standard Quick → Refine controls, on dialogue and substantial movement.
Review action order/timing, identity and positioning, camera movement, exact
dialogue/lip synchronization, and improved visual quality without scene changes.
The gate remains unresolved until the user accepts a promoted final for each
scene. There are at most two promotion attempts per scene.

Reserve 60 minutes of the approximately 120-minute GPU budget for this experiment.
Time spent preparing/exporting files and human review is recorded separately from
GPU execution, and contributes to elapsed time to an accepted final. Record
rejected drafts and unsuccessful promotions, not only successful latency.
Draft/promotion attempts use the reserved hour. Required baseline controls may
finish against the shared 120-minute total, reducing the remaining candidate budget.

After acceptance, prioritize 10Eros Beta7 Compact, then SMD Ref2VA 4-step. FastH3
Omni, PTD + RefAsymOPD, and runtime cache/attention experiments follow only when
earlier results justify them. 10Eros HQ is optional. Weight-file sizes are not
VRAM requirements. Retired-preset removal and migration belong to that later
phase. The [30 October trial](promotion-feasibility-2026-10-30.md) passed the
original gate and advanced to Compact and SMD; the user extended that trial's
budget by one hour. Removed recipes now migrate editable setups to Standard,
while saved takes keep their historical evidence.

Both primary candidate finals were accepted. GPU benchmarking then ended at the user's request; remaining preset implementation continued using CPU and contract checks.
