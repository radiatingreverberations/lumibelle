# H3 generation presets and frame storage

Shots offers Standard 20-step, Beta 20-step, Euler beta 20-step, Larry 6-step,
PDD 8-step, Turbo 4-step and HyperFlow 8-step, in that order. Standard remains
the default. Resolution, duration, references, optional character/style LoRAs, and
dense attention remain independent. New jobs never apply the old global Sol
preference. Acceleration weights are excluded from the optional LoRA library.

The Larry, PDD and Spectrum recipes come from the committed
[11 September comparison](acceleration-benchmark-2026-09-11.md), whose workflows
remain the reference for their explicit values. Larry was acceptable in both
tested scenes; PDD distorted dialogue; Spectrum and the existing Turbos had
motion-scene problems. This single-seed comparison ran on the **base** pruned
W4A8 Ref2VA model, with 141 frames, Upscaled preview, and one 20 GiB RTX 4000
Ada. Finetunes such as Singularity, additional LoRAs, other GPUs, Native output,
and longer clips are available but were not verified by that comparison. Larry,
PDD and HyperFlow were trained against the base model; on a finetune they run
outside the conditions they were distilled for.

## Retired presets

Spectrum, Turbo 8-step and Turbo 4-step at 0.75 were retired on 2026-10-06 to
shorten the list. Spectrum and Turbo 8-step had motion-scene problems in the
11 September comparison, and Turbo 8-step was slower than Turbo 4-step. Turbo
0.75 was never benchmarked, and the strength advice behind it is not on the
Singularity model card, which recommends the Ref2V Turbo 4-step v0.1 LoRA
without giving a strength.

Retired presets are no longer offered for new choices. Their keys and recipes are
unchanged, so shots, named setups and queued requests that already use them keep
generating as before. A picker still lists the current preset when it is retired,
marked **Retired**, and **Video models** keeps their setup under **Retired presets**.

## Scheduler and strength variants

These three presets use native ComfyUI nodes only and have not been benchmarked
in Lumibelle. Each isolates one change from an existing recipe, so a paired-seed
comparison against its parent attributes any difference to that change.

| Preset | Parent | Change | Rationale |
| --- | --- | --- | --- |
| Beta · 20 steps | Standard | `simple` → `beta` scheduler | Reportedly suggested by the ComfyUI R2V template notes for reference-heavy prompts; not independently checked. |
| Euler beta · 20 steps | Beta | `res_multistep` → `euler` sampler | Preferred to Standard in one community Ref2VA comparison, which also used Sol-Attn and EasyCache and changed both sampler and scheduler. |
| Turbo · 4 steps · 0.75 (retired) | Turbo · 4 steps | LoRA strength 1.0 → 0.75 | Added for a suggested 0.75–1.0 strength with Singularity. That suggestion is not on the Singularity model card (checked 2026-10-06). |

The Turbo variant reuses the LoRA selected for Turbo 4-step. All three keep
BasicGuider, shifts 12/3 in the model, full denoising and `BasicScheduler` on
the unpatched model. Their sampler, scheduler, steps and strength are captured
in `h3-presets-v1`, so queued requests keep them. Refine and Rework keep their
Standard sampling. Samplers that need extra packages, such as RES4LYF's
`res_2m`/`res_2s` and the `bong_tangent` scheduler, are deliberately not offered.

## Installing experimental presets

| Preset | Tested package revision | Checkpoint |
| --- | --- | --- |
| Larry | [4274783a23afcfdbea3b4876cb79effd6c510785](https://github.com/Larryvrh/ComfyUI-MiniMax-H3-Turbo/tree/4274783a23afcfdbea3b4876cb79effd6c510785) | `models/loras/minimax_h3_turbo_v4_step600_ema.safetensors` |
| PDD | [311a65dd53832d8a5f8177a9d5fb923c09e35a90](https://github.com/Jalen-Brunson/ComfyUI-MiniMax-H3-PDD-Acc/tree/311a65dd53832d8a5f8177a9d5fb923c09e35a90) | `models/pdd_acc/MiniMax-H3-Ref2VA-Acc-8Step.safetensors` |
| Spectrum | [455bd357cb45637c8e852f7f448dc57b52de94f8](https://github.com/xmarre/ComfyUI-Spectrum-MiniMax-H3/tree/455bd357cb45637c8e852f7f448dc57b52de94f8) | None |

Install the complete package and its dependencies in the running ComfyUI Python
environment. Docker installations need persistent custom-node and model mounts.
Restart ComfyUI, refresh Video models, choose the exact installed checkpoint
(including subfolders), then Save. Lumibelle neither installs nor downloads files.
Unavailable selections remain visible and block generation rather than falling
back. Readiness validates advertised contracts and file catalogs, not weight
contents, real accelerator execution, or available GPU capacity.

Larry uses its dedicated loader and sampler, strength 1, six simple-schedule
steps, and `low_vram=false`. PDD uses its loader/head bank, eight evaluations,
supplied sigmas, Euler, shifts 12/3, strengths 1/1, and strict grid/partition
checks. Spectrum wraps the prepared model for both guider and scheduler, with
audio blend zero, video blend 0.5, smoothing replay, and system-RAM history and
replay storage. Every supported input is serialized explicitly in `h3-presets-v1`.

## Queue and storage compatibility

New requests capture a versioned preset, exact checkpoint paths, sampling and
attention settings, and an output policy. Recovery, retries, and added candidates
reuse that snapshot. The captured requirements are checked again before remote
submission. Existing shots without a preset resolve their old Standard/Turbo
fields; absent optional fields do not change legacy fingerprints. Existing
queued snapshots keep their Sol, compression, and required-archive behavior.

**Save lossless frames** is per shot and off by default. Enabling it uses Fast
lossless WebP. Disabling it omits encoding, download, and validation of archives;
MP4 dimensions, logical frame count, 24 fps, and audio remain validated. Archive
timing reads **Not requested**. Refinement packages are independent; existing
refinement workflows retain their own archive and capture requirements.

Playback and saving to Assets share `TakeFrameReader`. It reads an expected
lossless archive, or extracts the requested zero-based MP4 frame with FFmpeg.
A missing expected archive remains an error. MP4 extraction decodes to the exact
frame index, returns one PNG, is limited to two concurrent processes and two
minutes per extraction, and deduplicates concurrent readers. The last cancelled
reader cancels extraction. No complete archive or persistent frame cache is built.
Saved Assets retain frame index, timestamp, take identity, and lossless/compressed
source provenance. Compressed sources are labelled in the player and inspector.

## CPU validation

The preset contract fixture records `INPUT_TYPES` and `RETURN_TYPES` from the
pinned packages above; the recipe fixture records inputs from the committed
benchmark workflows. CPU tests cover graph combinations, strict contracts,
LoRA ordering, snapshots, recovery, and continuation. Synthetic H.264 video
tests exact frame extraction, publication, asset provenance, cancellation, and
trash/restore. Browser tests use mocked catalogs and generated fixture media.
GPU benchmark execution remains manual and outside CI.
