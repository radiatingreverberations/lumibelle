# Motion-aware extension

**Extend…** on a take, or **Continue from this frame** in review, captures the
source through the chosen frame. Next action and dialogue start empty. The form
shows the route, retained prefix, context length and achievable added duration.
The AI composer receives captured references, ordered context stills and the
preceding action. Choose a vision-capable text model for that composition.

**Extend this take** saves an independent combined take. **Separate continuation
shot** saves only new footage, in a new shot after the source or an existing shot.
The source ending and new opening are available through **Preview join**. Saving
opens the result for review. Production selection, cuts and planning durations
remain explicit and unchanged.

## Routes and boundaries

* **Saved motion:** a compatible 39-frame window ending on the full generation's
  `17k+5` frame boundary. Lumibelle copies the video/audio window into new padded
  `.latent` tensors. The retained package remains unchanged. `LoadLatent`, separate
  `SetLatentNoiseMask` nodes, and `LTXVConcatAVLatent` carry the masked streams into
  Standard sampling. Video masks use an explicit temporal image batch, avoiding
  duplicate-frame optimizations in animation codecs. The audio mask is a PNG.
* **Motion from frames:** exact boundaries use 39, 22, or 5 visible decoded frames
  and matching audio through stock `MiniMaxH3AddGuide`. Shorter clips use one
  frame. Lossless pixels are preferred; context is labelled re-encoded.

Each generation includes its context and is rounded upward to `17k+5` frames,
with a maximum of 362 frames at 24 fps. Video has
`2 + 5 * ((frames - 5) / 17)` latent steps; audio boundaries are calculated from
absolute frame positions at 40 latent steps/second. The context prefix is removed
once from delivered video and audio. Assembly adds no fades or frame blending.
Character/style LoRAs and captured attention settings carry forward; acceleration
presets do not. Fresh output latents are always retained.

**Snap end for continuation** in Trim defaults on when compatible latents and
39 visible context frames are available. Its suggested endpoint uses the full
source coordinates, including repeated trims. Exact-frame mode and one-frame
trims remain available; the start handle is never snapped.

Preflight checks the server's reported version (0.35+) and stock input/output
contracts. A queued route never changes silently. No custom nodes are installed.
See the upstream [latent nodes](https://github.com/Comfy-Org/ComfyUI/blob/master/nodes.py),
[AV concatenation](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_lt.py),
and [H3 guide](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_minimax_h3.py).

## Owned sources, refinement and recovery

`ShotTake.Composition` stores flat, ordered segments. Each segment maps a visible
range into an independently retained full generation, with its own latents,
immutable snapshot and prepared inputs. Visible frame count is derived from these
ranges, independently of the final generation's frame count. Combined takes may
exceed 362 frames; individual generations may not. Parent IDs are provenance.

Lossless archives remain exact per segment. Mixed takes keep that distinction,
including when saving a frame to Assets. Removing archives leaves MP4s, latents,
captured inputs and join previews intact. Storage accounting, Trash and project
packages include the complete retained bundle.

**Refine extension** and **Rework extension** process the final segment's full
generation at the existing dimensions, retain its context, reapply its visible
range and rebuild the earlier prefix. Refine keeps source audio; Rework generates
new audio. Further refinement uses the fresh result's latents. **Another version**
replays the captured request with a new seed, including after trimming and import.
If a trim removes the newest extension entirely, replay uses the remaining final
segment's captured generation. Refined segments also own the original refinement
input package, so that replay survives removal of earlier takes and requests.
Regeneration and new dubbing masters continue to use full generation sources.

The full generation is published before local assembly. Stable output identities
and publication receipts allow **Retry saving extension** after failure,
cancellation or restart without rerunning generation. Staged media is verified
before publication. Retained input hashes are checked during capture and retry.

## Validation

.NET tests cover tensor-byte preservation and padding for F32/F16/BF16, scaling
markers, mask graph contracts, full-source coordinates, successive extensions,
both destinations, final-segment Refine/Rework, replay, parent purge, cancellation,
publication conflicts/retries, archive removal and project package round trips.
Real FFmpeg checks cover overlap endpoints, exact frame counts, audio/silence,
one-frame boundaries and combined output longer than 362 frames. Browser tests
exercise prompt capture, join review, explicit selection, keyboard trim, final
frames, narrow layouts, downloads and existing cuts. AI workflow tests use mocks.

The stock-node runtime CPU check is opt-in, using ComfyUI's Python environment:

```text
python tools/h3_benchmark/check_motion_contract.py --comfy-root /path/to/ComfyUI
```

It submits no jobs and loads no model weights. It checks actual stock mask
transport, mask expansion, context preservation and LoadLatent scaling.

The CPU check passed against installed ComfyUI 0.39.0. The
[October 9 live smoke comparison](motion-extension-smoke-2026-10-09.md) completed
eight real Standard generations, covering walking, camera tracking, speech, both
motion routes, three chained extensions, Refine and Rework. Saved context matched
within float32 rounding; retained source hashes and selected lossless pixels were
unchanged. This is a single prompt and seed at a small resolution, not a general
visual-quality result or a guarantee against drift. Speech intelligibility and
join quality still need human review of the saved clips.

For another manual run, set `LUMIBELLE_LIVE_MOTION_URL`,
`LUMIBELLE_LIVE_MOTION_SETTINGS` (an ai-settings.json file), and
`LUMIBELLE_LIVE_MOTION_OUT` (a fresh output directory), then run only
`LiveMotionExtensionComparesSingleFrameFramesAndSavedMotionThenChainsAndRefines`.
The test submits real GPU work; normal suites skip it. Verify its retained hashes
and latent context afterward with:

```text
python tools/h3_benchmark/verify_motion_outputs.py /path/to/smoke-archive
```

Bridging into fresh takes, longer individual generations, sequence resizing and
whole-sequence refinement remain subsequent work.
