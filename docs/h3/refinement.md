# Take refinement

Refining a take makes a larger version of it that keeps its motion, composition
and audio. The intended workflow is to generate several cheap takes, for example
at Quick size (0.2 MP) with Standard's 20 steps, pick the best, and refine only
that one to a larger size.

A take's seed doesn't carry over to another size or preset: the starting noise
depends on the clip's dimensions, and presets sample differently. Generating the
same seed at a larger size gives an unrelated clip. Refinement instead starts from
the take itself.

It uses stock ComfyUI nodes plus the learned 3D latent upscaler pack that
Upscaled preview also uses. No Lumibelle companion nodes are involved; the earlier
companion-based version was removed on 2026-10-07.

## How it works

**Keeping the take.** Saving latents is a per-shot option, **Save latents**, off by
default like lossless frames; the Regenerate dialog has the same option. With it on, a
take's graph splits the sampler's audio/video
latent, the same output the take decodes, with `LTXVSeparateAVLatent` (node 20) and saves both parts with
two stock `SaveLatent` nodes (21 video, 22 audio). Lumibelle downloads the two
`.latent` files, checks their shapes and combines them into the take's
`refinement.safetensors` package: tensors `video` `[1, 24, T, height/16, width/16]`
and `audio` `[1, 32, 2, round(frames/24*40)]`, with `T = (frames - 5) / 17 * 5 + 2`.
The package's metadata holds only its id, size, frame count and fps, so it carries
no prompt or settings. ComfyUI's own `.latent` metadata, which includes the
submitted prompt, is not kept. At 0.2 MP and 141 frames the package is about
3.5 MB; the size grows with the pixel count and duration, and the shot options show an
estimate. ComfyUI also keeps its own copies of the two `.latent` files in
`output/lumibelle/`, since stock `SaveLatent` always writes a new numbered file and
ComfyUI has no API to delete outputs. Takes made with Upscaled preview keep no
package. If ComfyUI lacks these nodes, a shot with **Save latents** on is refused
before queueing.

A take generated without latents can be regenerated with its seed, its resolution
and **Save latents** on, then refined. That relies on the same seed, size and
inputs producing the same take on that ComfyUI setup.

**The refinement pass.** It is a normal H3 generation with three changes, built by
the same graph builder as any take (`ComfyH3Video.BuildWorkflow` with a
`RefineSource`):

1. The package is split back into two `.latent` files, uploaded to the top level
   of ComfyUI's input folder (where `LoadLatent` lists files), and loaded with two
   stock `LoadLatent` nodes (30, 31).
2. The video latent is enlarged to the target size by `MinimaxH3LatentUpscaler3D`
   (32), using the installed implementation (LBH or Plus, captured with the
   request), then joined with the audio latent by `LTXVConcatAVLatent` (33). That
   joined latent replaces the empty latent as the sampler's input.
3. The prompt and references are encoded again at the target size by the normal
   `MiniMaxH3ReferenceToVideo` node, from the source take's saved prompt and
   prepared reference files, which are copied into the refinement request the way
   regeneration copies them. A start-frame anchor is skipped, since the take
   already starts on that frame.

Sampling is always Standard (`res_multistep`, `simple`), whatever preset made the
take; character and style LoRAs and the attention setting carry over, the Turbo,
Larry, PDD, Spectrum and HyperFlow patches don't. ComfyUI's `BasicScheduler`
keeps the step count when denoise is lowered, so the pass asks for the steps a
20-step schedule would run over that part:

| Mode | Denoise | Steps | Audio |
| --- | ---: | ---: | --- |
| Refine | 0.35 | 7 | The take's own audio latent is decoded beside the new video, so the audio is unchanged. |
| Rework | 0.65 | 13 | Newly sampled audio. |

The refined take saves its own latents again (Refine saves the source audio
latent), so it can be refined further. Lossless frames are always kept for
refinements.

**Sizes.** The source must use the 32-pixel grid. The choices are Same size and
presets near 1344 × 768, 1080p and 1440p in the source's aspect, rounded to 32;
a choice that would downscale or enlarge more than 4× per dimension is
unavailable.

## Spike evidence

A spike on 2026-10-07 on a 20 GB RTX 4000 Ada with the official W6A8 model,
a text-only prompt and seed 20260911 generated a 608 × 352, 141-frame take with
20 Standard steps, kept its latents with stock nodes, and refined it to
1120 × 640 at 0.5 denoise with the Upscaler-Plus pack. The refined clip kept
the take's room, path, outfit and timing with more detail; a fresh 1120 × 640
generation with the same seed was an unrelated clip. The refined clip's decoded
audio had the same SHA-256 as the source take's. The spike ran 20 steps for the
refinement (about 6.6 minutes of sampling at that size), which is why the pass
now sets its step count from the denoise strength. This was one scene and one
seed, without reference pictures.

## Jobs, recovery and versions

Before enqueueing, the source take's package is re-checked and copied into the
new run's inputs, and its prepared reference files are copied after it, under the
project lock. Later prompt edits, asset changes or deleting the source take can't
change those inputs. The request also captures the chosen upscaler checkpoint and
implementation; if the installed implementation changes before submission, the
request asks to be queued again.

Refinements use the normal video job queue. Each one locks its source take, so the
same take can't be refined twice at once, while the source's own batch can keep
running. **Another version** extends the refinement's batch with a fresh seed.
Each version gets its own complete package; parent links organize review but
aren't needed to load, refine, restore or purge a version. Take deletion and Trash
treat the MP4, lossless archive and package as one bundle.

## Checks

```powershell
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj --filter "FullyQualifiedName~Refinement"
node node_modules/@playwright/test/cli.js test tests/browser/refinement.spec.js
```

Unit tests cover the graph (stock nodes only, partial pass, audio handling), the
package's combine/split round trip through ComfyUI's `.latent` format, transfer
retries, readiness, locking and recovery. Browser tests use a mock generator.
Neither establishes GPU behaviour; the spike above is the only real run so far.
