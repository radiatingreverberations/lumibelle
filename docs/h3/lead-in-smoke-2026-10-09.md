# Lead-in generation: live smoke test, October 9

Four real ComfyUI jobs and their local publication completed successfully after
the service restart. These results are separate from mock workflow/browser tests
and the CPU stock-node contract check. Evidence and media are retained locally in
`artifacts/lead-in-20261009-resumed/` (ignored by Git).

## Setup

- ComfyUI 0.39.0, PyTorch 2.11.0+cu130; RTX 4000 Ada, 20 GiB.
- `minimax_h3_ref2va_pruned_w6a8.safetensors` and
  `qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors`.
- Int8 video VAE, fp32 audio VAE, captured ServerDefault attention.
- Standard sampling, 608 × 352, 24 fps, seed 20261009, no LoRAs or voice reference.
- Reused the independently saved 124-frame courtyard source from the
  [forward extension comparison](motion-extension-smoke-2026-10-09.md).
- Requested two additional seconds before the clip. The supported generation
  length is 90 frames, including 39 frames of ending context; the delivered
  prefix contains 51 new frames, or 2.125 seconds.
- The prompt requests a woman in a blue jacket approaching the courtyard while
  walking right, with a tracking camera, footsteps and “Here we go.” The source
  requests “Let's keep moving.” Speech intelligibility was not assessed.

## Completed runs

| Case | Visible frames | ComfyUI execution (s) | Execution and local save (s) |
| --- | ---: | ---: | ---: |
| Saved motion, before first visible frame | 175 | 231.36 | 268.65 |
| Re-encoded motion, exact source frame 1 | 174 | 112.75 | 151.90 |
| Refine saved-motion lead-in | 175 | 28.33 | 67.08 |
| Rework saved-motion lead-in | 175 | 45.40 | 84.67 |

The opt-in .NET test passed in 9 minutes 49 seconds. It used production context
capture, workflow building, download, media assembly and take publication in a
temporary project. The first job loaded models after the restart; timings include
different cache states and are not a controlled performance comparison.

## Verified

- Every delivered lossless pixel matches the intended ordered source: all 51
  generated prefix frames, followed by all 124 original frames (123 for the
  exact-frame case). This checks both sides of all archive boundaries, the join,
  endpoints and removal of the ending overlap exactly once.
- Each MP4 has the expected frame count, 608 × 352 dimensions and 24 fps. Video
  and audio timestamps start at zero. Video/audio durations differ by less than
  one millisecond: 7.291667/7.291 seconds for 175 frames and 7.25/7.25 seconds for
  174 frames.
- Captured source-file hashes match, and the original full latent package still
  matches its pre-existing SHA-256 descriptor.
- Saved-motion, Refine and Rework retain the ending 12 video latent steps and 65
  audio steps. The largest differences are 2.3841858e-7 for video and
  5.9604645e-8 for audio, consistent with float32 normalization rounding. Refine's
  held audio is identical.
- Refine preserves the full generated source audio exactly when decoded to PCM.
  Both refinement modes rebuild the combined take with the earlier generated
  segment in the correct position and the original footage unchanged.
- The restarted server also passed the CPU stock-node check for opening/ending
  masks, AV mask transport, LoadLatent scaling and an ending frame guide. That
  check loads no model weights and submits no GPU job.

`results.json`, per-case `request.json`, `workflow.json`, `history.json`,
`full-generation.json`, `take.json` and their owned media bundles retain the
evidence. `latent-verification.json` records source hashes and tensor comparisons;
`media-verification.json` records all-frame pixel and media timing checks.
`verify_lead_in_media.py` in the local archive reproduces those media checks using
Pillow and FFmpeg/ffprobe. The normal test run skips the opt-in live test.

The earlier interrupted attempt is preserved separately in
`artifacts/lead-in-20261009/`. It recorded a prompt ID before the service became
unavailable, but the restarted server had no history for that prompt. Only the
four completed jobs above support this report's success claims.

## Review limits

Sampled MP4 frames around all four joins preserve the subject, setting and
ordering. They do not establish smooth motion or speech intelligibility. The
saved MP4s remain available for human playback review. One prompt, one seed and
this resolution do not establish a general quality advantage or freedom from
drift.

Parent purge, export/import, cancellation, retry, replay, short contexts and mixed
before/after chains remain covered by automated tests using mock generation.
This live test exercised successful generation and publication without injecting
those failures. No application project, production selection or cut was changed.
