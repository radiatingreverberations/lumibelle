# H3 generation presets and frame storage

Standard remains the default. Current choices are Standard, Beta, Euler beta,
Larry, PDD, Turbo 4-step, **10Eros Compact**, **SMD Ref2VA 4-step**, **FastH3 Omni 8-step** and **PTD + RefAsymOPD 6-step**. Resolution, references,
authored dialogue, voice inputs and optional identity/style LoRAs stay editable.
New requests save [generation settings and owned inputs](generation-recipes.md).

## Drafts and fresh finals

Use **Refine from video…** on a saved take, or on several selected takes, to open a separate editable
Standard draft. It defaults to native resolution, one take and a random seed,
using the ordinary H3 model. The independently copied guide is video-only;
authored dialogue and other enabled voice references remain active. This is an
video-conditioning workflow, with no reconstruction or motion-strength
promise. See [promotion](motion-reference-promotion.md) and the
[30 October trial](promotion-feasibility-2026-10-30.md).

## Complete checkpoints and adapters

10Eros Compact is a complete **Beta7** checkpoint with embedded Turbo, not an
adapter. Its separate model selection leaves the ordinary H3 model available for
Standard finals. Install the [published mixed W4/W6/INT8 Compact file](https://huggingface.co/LokkenJP/TenEros_Max_optimized_learned)
in `models/diffusion_models`, then refresh Video models and select it in the
Compact row. The captured recipe uses eight Euler/simple steps, shifts 12/3,
BasicGuider, Comfy Kitchen attention, and the companion VSA settings: keep 10%,
dense blocks `0,1`, full denoising range, minimum 12288 tokens, 256 extra tokens,
`exact_kv_and_rows`, verbose false. No additional acceleration adapter is loaded.
Native mixed quantization and these node contracts are required. The published
checkpoint filename is saved at capture. Its 14.65 GB storage size does not specify
required VRAM. The bounded trial observed no coarse VSA gates; actual attention
kernel execution remains unobserved. HQ is optional and was not tested.

SMD is an adapter with a separate official pruned INT8 Ref2VA base selection. It
uses the [published 4-step adapter](https://huggingface.co/zjwfufu/SMD-MiniMax-H3-Ref2VA-4step-ComfyUI),
strength 1, Euler, BasicGuider and `ManualSigmas` values
`1, 0.9677419355, 0.9, 0.6666666667, 0`. Video/audio shifts are 12/3.
The selected base and adapter filenames are saved at capture. It uses the
configured shared encoder and VAEs; the bounded trial retained the W4A8 encoder,
which differs from the publisher's NVFP4-AWQ compact profile. No Lightning/Turbo
adapter is stacked. File storage does not establish runtime memory requirements.


FastH3 Omni selects its own native ComfyUI OmniRef INT8 ConvRot checkpoint. It
uses eight Euler/simple steps, BasicGuider, shifts 12/3 and Comfy Kitchen attention.
VSA keeps 10%, with no dense-block exclusions, start/end 0/1, minimum 12288 tokens,
256 extra tokens and exact conditioning/audio rows. The older T2V/FL2VA FastH3
checkpoint is a different model. [Native ComfyUI release](https://huggingface.co/FastVideo/FastH3-Omni-Comfy).

PTD + RefAsymOPD selects its own official pruned INT8 base and six-hop compact INT8
adapter in `models/ptd`. The pinned [compact package](https://github.com/Nynxz/ComfyUI-PTD-Compact/tree/f9eb8914a9ee39d69ac097979d49e86bb423380d)
includes the PTD runtime. The graph uses upstream `PTDH3CompactLoader`,
`PTDH3CompactSampler`, `PTDH3CompactSigmas` and BasicGuider. It uses the adapter's
corrected six-row clock (`pdd_num_steps=6`, `pdd_block_size=1`), rather than a
standard scheduler or a model-only LoRA loader. The loader selects
`exact (PTD reference)` kernel arithmetic. Allocation compilation and resampling
fallback follow the upstream sampler's policy; weight streaming stays enabled.
Do not substitute the W6A8
base, whose quantized output head uses a different layout.

Preset names and descriptions present model/recipe facts. They do not attach
quality ratings or experimental badges. GPU benchmark history and limits belong
in the dated findings; FastH3/PTD were integrated with CPU/contract validation
without additional GPU renders, at the user's request.

Standard partial-denoise refinement is unavailable for baked accelerated
checkpoints. Use motion-reference promotion for a fresh Standard final.

Turbo 4-step uses the Ref2V v0.1 LoRA, strength 1. Larry uses its dedicated loader
and sampler, strength 1, six simple-schedule steps and `low_vram=false`. PDD uses
its loader/head bank, eight evaluations, supplied sigmas, Euler, shifts 12/3,
strengths 1/1 and strict grid/partition checks. Beta and Euler beta are 20-step scheduler/sampler variants. Optional character/style LoRAs remain ordered
separately; acceleration adapters cannot be selected as optional style LoRAs.

Larry and PDD setup links and exact files are listed in Video models. Their pinned
[11 September comparison](acceleration-benchmark-2026-09-11.md) remains dated
evidence: one seed, base W4A8 Ref2VA, 141 frames, Upscaled preview and a 20 GiB
RTX 4000 Ada. Larry was acceptable in both scenes; PDD distorted dialogue.
Those observations do not establish behavior on other models or scenes.

## Removed presets and migration

Spectrum, LightX2V 8-step, the 0.75-strength Turbo variant and standalone HyperFlow
are removed from selection, discovery, downloads and execution. Editable shots,
saved setups and reel drafts using them reset to Standard and display a migration
notice. Normalized edits persist on the next save. Saved takes, historical recipes
and receipts retain their original data. Old queued requests using a removed
recipe cannot silently execute a replacement: capture a new current request.
The [old HyperFlow document](hyperflow.md) is retained solely as historical evidence.

## Queue and storage

New requests freeze prompt bytes, owned prepared inputs, model/adapter filenames
and sampling/attention settings. Retries validate prepared inputs and retain each
attempt's submitted graph and ordinary ComfyUI history. Model contents and runtime
implementations are not audited. Existing historical records retain their data.

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
