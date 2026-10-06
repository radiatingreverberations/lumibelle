# HyperFlow 8-step ComfyUI conversion (experimental)

This preset integrates the **drbaph ComfyUI conversions** into Lumibelle's existing
MiniMax H3 Ref2VA workflow. It is not a port of Video Rebirth's upstream two-time
HyperFlow loader, and has not been benchmarked or generation-tested in Lumibelle.
No speed, memory, voice-similarity or visual-quality improvement is asserted.

## Set up and use

1. Download the conversion matching your base model from
   [drbaph/MiniMax-H3-Turbo-Lora-ComfyUI](https://huggingface.co/drbaph/MiniMax-H3-Turbo-Lora-ComfyUI)
   and put it in the running ComfyUI server's `models/loras` directory. Subfolders
   are supported. Keep the published basename.
2. In Lumibelle's **Video models**, choose **Check again**, expand
   **HyperFlow · 8 steps**, choose **Change** on its file row, select the exact
   installed checkpoint and save.
3. Choose **HyperFlow · 8 steps** in the shot's generation settings or the reference
   reel's generation settings. This is separate from the existing Turbo 8-step
   preset. Do not also add HyperFlow to the optional character/style LoRA list.

The default is
`minimax_h3_hyperflow_8step_v1.0_comfyui_pruned_bf16.safetensors`, intended by the
conversion publisher for a compatible pruned/curve-form base. The preset also
recognizes these published alternatives:

- `minimax_h3_hyperflow_8step_v1.0_comfyui_pruned_bf16_resized_avg_rank_20_bf16.safetensors`
- `minimax_h3_hyperflow_8step_v1.0_comfyui_bf16.safetensors`
- `minimax_h3_hyperflow_8step_v1.0_comfyui_bf16_resized_avg_rank_20_bf16.safetensors`

Use full conversions with the corresponding full base and pruned conversions
with a compatible pruned base. Discovery verifies exact catalog filenames and
node interfaces, **not the tensor contents, base-model compatibility or available
GPU memory**. Renaming unrelated weights to a supported filename does not make
them compatible. Arbitrarily renamed conversions are not accepted in this initial
preset; restore the published basename instead.

The raw upstream `minimax_h3_hyperflow_8step_v1.0.safetensors` is deliberately not
accepted. Its upstream loader installs additional endpoint-time conditioning as
well as the schedule. Loading that raw file as an ordinary model-only LoRA is not
the functionality implemented here.

## Frozen recipe

| Parameter | Value |
| --- | --- |
| Evaluations | 8 Euler intervals |
| LoRA loader | `LoraLoaderModelOnly`, strength 1.0 |
| Model shift | `MiniMaxH3SigmaShift`: video 12, audio 3 |
| Guider | `BasicGuider`, no negative/extra CFG pass |
| Sampler | `KSamplerSelect`: `euler` |
| Sigma input | `ManualSigmas`, fixed **video-shifted** grid below |
| Denoise | Complete 1-to-0 trajectory; no partial-denoise truncation |

```text
1.0, 0.9939097854, 0.9842874953, 0.9660637197, 0.9230769231,
0.8349423898, 0.6968520491, 0.4687533149, 0.0
```

Nine points define eight intervals. `ManualSigmas` goes directly into
`SamplerCustomAdvanced`; there is no `BasicScheduler` on this preset's path and no
automatic fallback to Normal, Simple or Beta when the native node is missing.
Update ComfyUI and refresh discovery instead. The preset needs no new Lumibelle
companion, custom scheduler package or runtime dependency.

### Why these are not the raw values in the original suggestion

Video Rebirth's `schedule.py` calls
`1, .931506, .839236, .703462, .5, .296538, .160764, .068494, 0` the **raw,
unshifted** grid. It applies this transform separately for each modality:

```text
shifted_sigma = shift * raw_sigma / (1 + (shift - 1) * raw_sigma)
```

ComfyUI's native `ManualSigmas` only parses the numbers; it does not apply that
transform. Its H3 model shift node tells the DiT how to invert the video schedule
and derive the audio schedule. Therefore this graph supplies the video-shifted
numbers above and still configures the model with shifts 12/3. It neither feeds
raw sigmas to the sampler nor shifts the sampler numbers twice. Decimal notation
also avoids the native ManualSigmas parser's lack of exponent parsing.

The conversion model card inspected on 2026-09-19 is internally inconsistent: its
leading **Update — HyperFlow 8-Step LoRAs** section lists the shifted sequence,
while several later repeated sections still list the raw sequence. This preset
uses the leading recommendation, cross-checked against the upstream transform and
the native ComfyUI interfaces. That checks the schedule representation, not
behavioral equivalence to the upstream endpoint-conditioned implementation.

## Capture, reuse and other features

The checkpoint, sampler, raw grid, exact sampler-domain grid, shifts and strengths
are retained in the captured preset. `h3-hyperflow-comfy-v1` is a frozen recipe:
changing current global defaults does not change queued requests, saved take
regeneration or appended takes. A changed/tampered captured recipe is rejected,
not silently normalized to a different schedule. Old valid presets and their
sampling profiles are unchanged; the optional settings field is omitted when
unset to preserve older JSON representations.

The existing native references and the experimental Fantastic RefMod path keep
their respective latent output ports (1 and 2). Voice remains an independent
reference. Shared multi-take workflows share conditioning, model preparation and
the fixed sigma node while keeping sampler/noise/output branches separate.
Ordinary optional LoRAs retain their order; acceleration files cannot be added a
second time as character/style LoRAs. Existing attention selection, resolution,
preview upscaling and lossless-output choices remain in place.

Refine/Rework remain their existing **Standard** sampling workflows; this patch
does not add HyperFlow partial-denoise refinement. It also does not change which
reference types the refinement preparation supports.

## Validation and smoke test

```powershell
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj -c Release --filter "FullyQualifiedName~HyperFlow"
```

The added tests cover schedule arithmetic, conversion selection, captured recipe
integrity, legacy preservation, discovery contracts, output options, reference
and audio wiring, shared sampling branches, settings UI, and the existing queued
output-recovery/one-more path. HTTP/GPU interactions in those tests are fixtures,
not real generation. Run them and the full Release build before deployment.

For an actual server smoke test, use one short shot and one speaking character
reel at Preview, initially without extra optional LoRAs. Inspect the outgoing
graph for exactly one `ManualSigmas`, the fixed grid, `euler`, `BasicGuider`, one
HyperFlow loader and shifts 12/3. Generate once, then try two takes and a separate
voice reference. Test a RefMod only after a native-picture baseline works. Compare
likeness, motion and audio against Standard using the same source inputs and
seed set. Keep the one-time preparation and sampling timings separate.

## Primary implementation references

- [HyperFlow schedule, source revision 1dd2f342](https://github.com/Video-Rebirth/hyperflow/blob/1dd2f342aba5ab51da02b62885939655e8e268da/src/hyperflow_h3/schedule.py).
- [HyperFlow upstream model/loader](https://huggingface.co/videorebirth/hyperflow).
- [ComfyUI conversion card and files](https://huggingface.co/drbaph/MiniMax-H3-Turbo-Lora-ComfyUI), inspected 2026-09-19; conflicting schedule sections noted above.
- [ComfyUI H3 sigma-shift node, source revision 3c80da7](https://github.com/Comfy-Org/ComfyUI/blob/3c80da7f87ee359b2d06f107cb3c0797079dfbbb/comfy_extras/nodes_minimax_h3.py).
- [ComfyUI native ManualSigmas and custom sampling nodes](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_custom_sampler.py), inspected 2026-09-19.
