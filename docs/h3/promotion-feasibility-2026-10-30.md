# Draft promotion and H3 acceleration trial - 2026-10-30

The original feasibility gate **passed**: the user accepted both the dialogue-heavy
and movement-heavy Larry draft to Standard promotions, each on its first attempt.
This supports trying video-only motion guidance; it does not promise reconstruction
or establish a general quality/speed improvement.

## Isolation and evidence

The dedicated experiment container uses port 18188, separate inputs/outputs/user
state and frozen custom-node dependencies. Production port 8188 is watched, and
only the owned trial is interrupted if production work arrives. Production was
not restarted or reconfigured. Models use the normal ComfyUI model folders on
`hddpool/encrypted/media`, as requested; no new external model store is used.

Hardware: RTX 4000 Ada, 20 GB, with about 31.35 GiB of WSL system RAM. The retained
image reports ComfyUI 0.43.0, PyTorch 2.14.1+cu130, Python 3.12.15 and comfy-kitchen
0.2.40. The ordinary transformer is pruned W4A8 mixed Ref2VA, with the configured
W4A8 Qwen encoder, INT8 video VAE and FP32 audio VAE. All primary comparisons use
124 frames (5.167 seconds), 24 fps, seed 20261030, 608x352 drafts and 1344x768 finals.

Ignored local evidence is under `artifacts/motion-reference-20261030`: frozen
requests/graphs, source workflow downloads, file hashes/quantization headers,
runtime/source identities, actual sigma receipts, output clips/latents, event and
memory samples, reviews, comparisons and the budget ledger. The original recorder
used by the trial is frozen there. Later recorder improvements have distinct
source identities and are validated separately against real ComfyUI CPU tensors;
they must not be substituted into retries of old captured graphs.

## Cases

Worker seconds include loading, conditioning, identity recording, sampling and
decoding. They conservatively charge occupied worker time, not CUDA kernel busy
time. Downloads and pre-submission preparation are separate.

| Case | Worker seconds | Observed sigma intervals | Peak GiB | Result |
| --- | ---: | ---: | ---: | --- |
| dialogue-draft | 338.48 | 6 | 19.38 | complete |
| dialogue-recorder-control | 37.72 | - | 19.71 | complete |
| dialogue-final-1 | 1140.21 | 20 | 19.34 | User accepted |
| motion-draft | 323.03 | 6 | 19.42 | complete |
| motion-final-1 | 1124.22 | 20 | 19.62 | User accepted |
| dialogue-control | 293.01 | 20 | 19.60 | complete |
| dialogue-refine | 507.17 | 7 | 19.50 | complete |
| motion-control | 287.52 | 20 | 19.59 | complete |
| motion-refine | 599.40 | 7 | 19.50 | complete |
| 10eros-draft | 304.68 | 8 | 19.60 | complete |
| 10eros-final-1 | 1201.69 | - | 19.36 | Watchdog after 19/20 steps; user requested retry |
| smd-draft | 338.96 | 4 | 19.37 | complete |
| 10eros-final-2 | 1138.20 | 20 | 19.36 | User accepted |
| smd-final-1 | 1127.94 | 20 | 19.36 | User accepted |

The initial 120-minute GPU allocation reserved 60 minutes for promotion. The two
original promotions, drafts and recorder comparison consumed 49.39 worker minutes.
Controls and candidate screening followed. The user authorized another 60 minutes
on 30 October, bringing the ceiling to approximately **180 worker minutes**.
Completed cases, including the failed attempt, total 146.04 worker minutes. Both candidate finals were explicitly accepted; the user then ended further GPU testing. Remaining budget is checked before each case; active cases retain their watchdog.
No automatic inference retry occurs. The Compact rerun is an explicit user request
and retains identical request, prompt, media, seed and sampling graph, with fresh
transport identity and a 30-minute watchdog. The failed first attempt is retained. The requested rerun completed in 1138.20 seconds; all 20 sigma intervals and native video dimensions were validated.

## Feasibility and controls

The speaking scene says "The package is here. We can leave now." and retains an
authored voice reference. The movement scene requests walking across the room,
stopping, turning and waving. The prepared draft guide has no audio stream and is
wired only to video conditioning; identity/style and other enabled voices remain
active. The user accepted action/timing, positioning, identity, camera, sound,
dialogue delivery/lip sync and sufficient visual improvement for the original two
promoted finals. This is subjective acceptance of two short scenes, not an exact
motion or identity metric.

Standard Quick to Refine controls completed for both scenes. Their decoded audio
is byte-identical before/after refinement. Dialogue control plus refinement cost
800.18 seconds; movement cost 886.92 seconds, compared with 1478.69 and 1447.24
seconds for each Larry draft plus accepted promotion. Controls have not received a
separate human quality ranking. The trial establishes feasibility, not that
promotion beats refinement in total latency.

The equivalent recorder-free graph produced identical decoded audio and close
video (45.54 dB average PSNR, 43.43 dB minimum per frame), with minor non-identical
video pixels. The difference was not isolated. Pure and ComfyUI CPU checks verify
that the original sigma tensor is forwarded unchanged. Bit-identical GPU video
equivalence is **not** established. Warm model/native conditioning reuse completed
the recorder-free case in 37.72 seconds; persistent caching has not been measured.

## Candidate recipes

**10Eros Beta7 Compact** loaded and rendered successfully. The published mixed
W4/W6/INT8 file is 14,647,885,469 bytes, SHA-256
`64BF10E2EF650C2484C60A0EB3BC495BBA707AADC50281361AD0DD8BBA7C7B1E`.
It is a complete checkpoint with embedded Turbo, with eight Euler/simple steps,
shifts 12/3 and no extra acceleration adapter. Companion settings are Comfy Kitchen
attention and VSA keep 10%, dense blocks `0,1`, full range, minimum 12288 tokens,
256 extra tokens, exact conditioning/audio rows and verbose false. The native log
reports absent coarse VSA gates and fine-stage execution without that branch.
Actual attention kernel selection/fallbacks remain unobserved. The file size is
checkpoint storage, not required VRAM. The observed near-20-GiB peak does not
justify HQ. [Published quantization notes](https://huggingface.co/LokkenJP/TenEros_Max_optimized_learned/blob/main/README.md).

**SMD Ref2VA 4-step** loaded and rendered successfully on the official pruned INT8
base, strength 1, Euler, BasicGuider and fixed sigmas
`1, 0.9677419355, 0.9, 0.6666666667, 0`. The actual float32 sigma receipt agrees
within float precision. The base SHA-256 is
`9255F52B6677845AD238F20DFAAFA94727053694127AB7F255C048F0F9365779`;
the 1,956,183,672-byte adapter is
`9C6F61221A1043EE709AF0D32AE083C17EFEB36950B4B08A945296B5B9C39353`.
The trial retains the existing W4A8 encoder, an explicit deviation from the
publisher's NVFP4-AWQ compact profile. No other acceleration adapter is stacked.
[Published SMD contract](https://huggingface.co/zjwfufu/SMD-MiniMax-H3-Ref2VA-4step-ComfyUI).

Cold loading and conditioning dominate these short cases. Larry entered sampling
at 237.0 seconds and reached its last step at 318.4; Compact at 194.7/285.4;
SMD at 208.4/320.1. These are different models and memory/cache states, not a
controlled per-step speed ranking. Fewer steps did not automatically reduce
end-to-end latency.

Both recipes have separate model selections and captured compatibility requirements.
Compact is distinct from adapters and cannot use Standard partial-denoise
refinement. Spectrum, LightX2V 8-step, strength 0.75 and standalone HyperFlow are
removed from current choices, discovery and execution. Editable setups migrate to
Standard with a notice; saved takes and historical receipts retain their evidence.
The dated September benchmark remains unchanged.

## Memory and elapsed workflow limits

`memoryPeaksByStage` separates preparation/loading, sampling and decode driver
memory and system RAM. Preparation includes conditioning and recorder work; it is
not an isolated loader allocation peak. Dynamic VRAM loading/offloading is present,
and system RAM is tight. These samples do not guarantee every transient peak or
establish another model's fit based on file size.

The retained `memory-phase-analysis.json` also splits sampler initialization and
the first completed step from later sampling, using the observed progress events.
Lazy GPU loading, compilation and first-step activations overlap, so this cannot
be labelled a pure cold-loader peak. These are the first uses of each checkpoint
in the trial process, with uncontrolled filesystem-cache state:

| Draft | Loading/conditioning GiB | Initialization + first step GiB | Later sampling GiB | Decode GiB |
| --- | ---: | ---: | ---: | ---: |
| Larry / ordinary W4A8 | 17.73 | 19.22 | 19.23 | 19.39 |
| Compact | 18.37 | 19.60 | 19.58 | 19.40 |
| SMD / official INT8 | 17.50 | 19.03 | 19.02 | 19.37 |

Compact's largest sampled system-RAM use was 29.39 GiB; SMD's was 29.46 GiB.
The earlier Larry record has free-RAM observations but lacks a total-RAM field,
so its derived RAM-use peak remains unavailable rather than being invented.

Time to accepted final is reported separately from worker time. The dialogue
acceptance occurred about 41m41s after its first frozen request (24m39s direct
draft/promotion worker time), or 69m46s after the implementation request. Initial
preparation and all human-review intervals were not independently timed. Those
wall times include development and waiting, so are not a clean creative-workflow
benchmark. Newer captures retain preparation start/end/duration and execution
start/end; acceptance remains an explicit user review, never inferred from render
completion. The original movement acceptance time is unavailable; its review record
contains the time it was recorded, not a claimed exact click time.

The user accepted both Compact and SMD promoted finals, then ended GPU testing
while requesting completion of the remaining implementation. FastH3 Omni and
PTD + RefAsymOPD are now integrated with native node contracts, CPU checks and
checksum-verified weights. No GPU renders were run for them; manual generation
and quality assessment remain with the user. The UI presents factual names,
step counts and requirements without experimental or quality badges.

The FastH3 file is the native OmniRef INT8 ConvRot checkpoint at revision
`82eff2a6eee6c962846879f4bab52610a4df24d3`, SHA-256
`851B2DC9670ED864B7F1E951AC9DA047D83B1C04DED9BFCC1A9E5BBA0EE33F80`.
PTD uses the six-hop INT8 RefAsymOPD adapter, SHA-256
`FC0809CDF5171FA904D3016B066E14956D0248ED592DAF4455ECE6752AD4BF5B`,
and ComfyUI-PTD-Compact revision `f9eb8914a9ee39d69ac097979d49e86bb423380d`.
Its JSON header was inspected without loading tensors: six hops, six PDD steps
and block size one. The package imports in a CPU-only process. The recipe policy
disables compiler-failure resampling, and the passive probe checks the loaded
six-hop kernel/compiler policy before recording its supplied sigma tensor.

The isolated GPU worker remains stopped with its frozen artifacts retained.
Production was not restarted; PTD and the updated recipe nodes are installed in
the persistent custom-node folder for activation at the next normal restart.

The accepted candidate paths cost 44.08 worker minutes for Compact (including its
failed first promotion), and 24.45 for SMD. Observed wall
time from first recorded preparation was 70m07s and 69m47s respectively, including
intervening development, other cases and waiting; initial downloads precede those
start times. Acceptance was recorded later, following a long human response gap;
that interval is retained separately and is not a controlled workflow-latency result.
Machine-readable timing and explicitly unrun cases are retained alongside the raw
records.

The implementation now includes the take-card/review action, editable promotion
draft, independent video-only media, preserved authored voice inputs, immutable
requests and execution receipts, Compact/SMD/FastH3/PTD preset selections, and
removed-preset migration. The three recipe runtime files are installed in the persistent
ComfyUI custom-node folder, for activation at the next normal restart. Production
was not restarted, and no further generations were run after the user's stop request.

## Code validation

The initial H3/reel/model-settings regression suite passed 1,151 tests, with three
opt-in live tests skipped. The integration follow-up passed 1,156 CPU regressions;
a later expanded run passed 1,157 and found one obsolete status-message assertion.
That assertion was updated to the requested factual UI behavior and verified in
the final focused run. The focused checks cover graph wiring, per-preset model
independence, source-independent recipes, adapter/refinement identities, settings
persistence, file/node contracts and documentation.

Both desktop/narrow motion-reference browser checks pass. Nine recorder/policy CPU
checks pass, together with a real-ComfyUI CPU integration check and the native PTD
node import/catalog check. No model weights were loaded and no sampler was called
in that PTD inspection. The exporter builds; NuGet audit metadata was unavailable.
No additional GPU generation was run for the implementation follow-up.
The working checkout also contains unrelated text-repair work: its two reel cases
in `TextRepairOfShotOrReelNeverAutoAppliesOnFinishOrRecovery` still fail with
"No complete failed response is available to repair." They were reproduced
separately and left outside this change; this is not a claim of a green full suite.
