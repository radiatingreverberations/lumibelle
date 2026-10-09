# Motion extension: live smoke comparison, October 9

Eight real ComfyUI runs completed successfully. These are separate from the mock
workflow tests and the CPU stock-node contract check. Local evidence and media are
in `artifacts/motion-extension-20261009/` (ignored by Git).

## Setup

- ComfyUI 0.39.0, PyTorch 2.11.0+cu130; RTX 4000 Ada, 20 GiB.
- `minimax_h3_ref2va_pruned_w6a8.safetensors`.
- `qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors`.
- Int8 video VAE and fp32 audio VAE, captured ServerDefault attention.
- Standard sampling, 608 × 352, 24 fps, seed 20261009, no LoRAs or voice reference.
- A woman walks through a courtyard while the camera tracks beside her. The
  source requests “Let's keep moving.” Extensions request “The door is just ahead.”
- Two added seconds requested for each extension. Supported lengths deliver
  55 new frames for single-frame context and 51 for 39-frame motion context.
- Refine/Rework use the final generation at its original dimensions. The installed
  Plus upscaler returns the original latent at unchanged dimensions.

## Completed runs

| Case | Visible frames | ComfyUI execution (s) | Execution and local save (s) |
| --- | ---: | ---: | ---: |
| Source | 124 | 249.73 | 252.95 |
| Single-frame baseline | 179 | 99.85 | 140.16 |
| Exact boundary, re-encoded 39-frame context | 174 | 156.80 | 206.17 |
| Saved motion, first extension | 175 | 73.75 | 118.34 |
| Saved motion, second extension | 226 | 67.04 | 115.04 |
| Saved motion, third extension | 277 | 68.71 | 136.05 |
| Refine first extension | 175 | 28.77 | 68.28 |
| Rework first extension | 175 | 50.59 | 93.00 |

These timings include different context encoding and cache states. They are not
a controlled performance benchmark. The full test took 19 minutes 13 seconds;
context capture and file copies also take time outside the per-run measurements.

## Verified

The production workflow builder, downloader, context capture, media assembly and
take publication produced the saved results. Every MP4 has the expected dimensions,
frame count, 24 fps and audio. Each extension retains source pixels exactly at
frames on both sides of a lossless archive boundary and at its retained endpoint.
Each combined take owns flat generation segments and fresh output latents.

The stock CPU check verified mask transport through `SetLatentNoiseMask` and
`LTXVConcatAVLatent`, temporal mask expansion and `LoadLatent` scaling. Checking
the real saved outputs confirmed that held video/audio latent context differed
by at most float32 rounding (less than 1e-6). All captured source-file hashes
matched. Refine reproduced the full source audio exactly when decoded to PCM;
Rework produced a fresh result while holding its context. Overlap was removed
once when assembling delivered footage.

`results.json`, per-case `request.json`, `workflow.json`, `history.json`, `take.json`
and owned `take/` bundles retain the evidence. `latent-verification.json` records
per-stream comparisons and retained-file checks. No application project, cut or
production selection was changed by this temporary-project smoke test.

## Review limits

Sampled frames around the joins preserve the subject and courtyard setting.
The clips include walking, camera motion and requested speech, but intelligibility,
motion at the join and visual preference need human playback review. One prompt,
one seed and a small output resolution do not establish a broad quality advantage
over single-frame continuation. Repeated extensions are still capable of drift.

Parent purge, package round trips, arbitrary trims, replay after removing the
newest segment, cancellation and failed-assembly recovery are covered by automated
workflow/storage tests; those checks use mock generation. The live test exercised
generation and local publication without injecting those failures.
