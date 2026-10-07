# Take refinement: setup and technical evidence

**Hidden since 2026-10-06.** The companion nodes haven't been tested on a real server yet, so the app no longer shows take refinement: Video models has no setup for it, Shots offers no **Improve quality**, and new takes capture no refinement data. The code and its tests remain. `H3Settings.TakeRefinement` turns it back on; nothing in the app sets it, and the browser tests turn it on to keep covering it. The rest of this page describes the feature as it works when turned on.

The bundled companion was downloaded from **AI settings → Video models → Take refinement → Install refinement support**. Installation commands and the package protocol are in [the companion README](../../comfy_nodes/lumibelle_h3/README.md). The learned checkpoint is explicitly selected after catalog refresh; refresh and Save perform no inference.

Refinement support is optional. Normal H3 generation uses stock ComfyUI nodes and saves MP4 plus lossless WebP without the companion or learned upscaler. When capture is available at request start, the batch also retains refinement data automatically. This decision is immutable for remaining candidates and One more take. If a captured batch requires nodes that are later removed, it pauses for repair rather than silently dropping promised data. Installing nodes later enables capture for new batches; it cannot add refinement data to existing takes.

## Supported implementation

Reviewed 2026-09-07. Runtime graphs and serialization are application code. Community workflow JSON and these links are evidence, never runtime configuration or network dependencies.

| Source | Pinned revision | Used for |
| --- | --- | --- |
| [LBH learned 3D upscaler](https://github.com/LBH-123-AI/Comfyui_Minimax_h3_latent_Upscaler/blob/d7c01b9011f2e8439493f6c02c29995a27df276f/nodes/minimax_h3_latent_upscaler_3d.py) | `d7c01b9011f2e8439493f6c02c29995a27df276f` | Video-only latent input, learned spatial resize, nested target-dimension inputs, 32-pixel alignment, temporal chunking and model release. |
| [Tr1dae reference implementation](https://github.com/Tr1dae/ComfyUI-MiniMaxH3_LatentUpscaler/tree/895e3c471164423f0ea0e8eaf45eb701efe641ae) | `895e3c471164423f0ea0e8eaf45eb701efe641ae` | Joint audio/video preparation, reference-conditioning resize, and why audio needs both zero noise and a zero denoise mask. Not installed or imported by this implementation. |
| [ComfyUI H3 nodes](https://github.com/Comfy-Org/ComfyUI/blob/ea33b15489b1cc7f13fc74f26e96af995479548f/comfy_extras/nodes_minimax_h3.py) and [custom sampler](https://github.com/Comfy-Org/ComfyUI/blob/ea33b15489b1cc7f13fc74f26e96af995479548f/comfy_extras/nodes_custom_sampler.py) | `ea33b15489b1cc7f13fc74f26e96af995479548f` | H3 latent layouts, conditioning keys, fully denoised sampler output, and audio/video decoding. |
| [Author-supplied Seed Hunter workflow](minimaxSEEDHUNTERWorkflow_v122.json) | SHA-256 `e8dde147214f21af27b2a9add40ab27f0d0ebc5d2f60b30d1b213ff8d50ce561` | Preview selection followed by learned latent upscale and a second H3 pass. Original preserved apart from removed local file paths. |

The adapter checks exact installed filenames and required node/input/output contracts before submission. The protocol identifies supported companion behavior. The upscaler's source revision is retained in refinement provenance; ComfyUI's catalog cannot independently prove which Git commit is installed, so use the pinned installation above.

## Captured data and graph

Normal takes captured with the companion, and every refined take, include `refinement.safetensors` beside their MP4 and segmented lossless WebP archive. Capture reads **SamplerCustomAdvanced's fully denoised output**, and both decoders consume the captured streams. The package contains:

- Video `[1, 24, T, height/16, width/16]`, where `T = ((frames - 5) / 17) * 5 + 2`.
- Audio `[1, 32, 2, round(frames / 24 * 40)]`.
- Ordered conditioning tensors and tagged JSON metadata, including `minimax_refs` image/voice blocks.
- The exact original `VideoSnapshot` plus separate refinement settings, a package UUID, dimensions, frame count and 24 fps.

The JSON tree distinguishes dictionary values from tensor references. Local validation checks SafeTensors lengths/offsets, dtype/shape limits, full tensor membership, frame grid and captured context; files receive a SHA-256 identity. Python validation additionally checks finite tensor values. No object deserialization is used.

Refinement loads joint latents and sends only the video tensor through `MinimaxH3LatentUpscaler3D`. The adapter uses its `target dimensions` mode, alignment 32, temporal chunking, model release, CUDA and FP16. Temporal dimensions remain unchanged. Image reference latents resize spatially with their own aspect, updating RoPE layout dimensions; text and voice conditioning stay unchanged. A new BasicGuider uses this conditioning.

The versioned application profile `h3-refinement-experimental-v1` uses the source Ref2VA base model, Standard 20 steps, `res_multistep`, simple scheduling, shifts 12/3, and denoise 0.35/0.65. These are experimental application choices, not official quality guarantees. Refine zeroes audio noise and its denoise mask, archives the source audio latent, and remuxes the original encoded audio into the output MP4 with stream copying. Rework samples and decodes both streams. Neither pass carries the source Turbo LoRA or calls an LLM/text encoder again.

Size presets preserve the source aspect with grid rounding: Same size, native-area (`1344×768` pixels), 1080p-class area and 1440p-class area. Both dimensions must stay within 1–4 times the source, with no downscaling. Unsupported options show the reason. Larger-than-native sizes and both modes remain experimental pending visual evaluation.

## Jobs, recovery and independent versions

Before enqueueing, the project store verifies and copies the active source package and MP4 into the new run's inputs under the project lock. Current prompt edits, asset defaults, model selections and parent deletion cannot change those captured inputs. The current upscaler choice is captured separately. Each request has its own take-target lock, so a saved candidate can be refined while the original batch finishes; both still share the provider's concurrency limit.

Refinement uses the existing video handler and durable queue. Each candidate retains its exact submitted graph and remote prompt identity. Submission uncertainty requires recovery, never implicit duplication. Transfer failures preserve already validated MP4/WebP files and can retry the package without another generation. Publication waits for the complete validated bundle. Another version extends the captured batch with a fresh seed and idempotent append command.

Each child gets a complete new package. Parent links organize review, but are not required to load, refine, restore or purge a child. Take deletion and 30-day Trash treat the MP4, WebP archive and package as one bundle; queued copies are independent. Production selection stays explicit. The old generation snapshot remains separate from refinement settings, so higher resolution alone does not mark a take outdated.

## Reproducible checks

From the repository root:

```powershell
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj
node node_modules/@playwright/test/cli.js test
uv venv artifacts/refinement-python --python 3.12
uv pip install --python artifacts/refinement-python/Scripts/python.exe -r tests/companion/requirements.txt
artifacts/refinement-python/Scripts/python.exe -m pytest tests/companion -q --basetemp=artifacts/refinement-test-tmp
```

Browser tests use isolated projects and mocked providers; CPU tensor tests exercise the companion's package and preparation logic plus node wiring with host doubles. FFmpeg tests compare hashes of copied encoded streams. Neither establishes GPU compatibility, visual preservation or lip sync.
