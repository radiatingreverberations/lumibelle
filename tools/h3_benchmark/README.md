# H3 workflow benchmark

**Manual tooling only.** These scripts are not part of `dotnet test`, browser
tests or CI. Normal application tests use mocked catalogs and generation. Run
GPU benchmarks explicitly when a compatible local ComfyUI setup is available.

## Historical promotion and candidate trial

See the [30 October findings](../../docs/h3/promotion-feasibility-2026-10-30.md)
and [current generation capture](../../docs/h3/generation-recipes.md). The dated
trial used a recorder that has since been retired; its existing artifacts retain
that evidence. Current application graphs use native and upstream nodes without
Lumibelle custom nodes. Scripts expecting recorder receipts are historical tools.
Use a separate
ComfyUI server and owned artifact directory; never restart production for a trial.
The prepared directory contains `h3-settings.json`, `inputs/face.png`,
`inputs/outfit.png`, `inputs/voice.wav`, `requests`, `results`, a budget ledger and
explicit review decisions. Models remain in the configured ordinary model folders.

The exporter builds application graphs and freezes requests. It refuses to
overwrite a case. The worker runs one case, watches production, checks the budget
and owned input hashes, records memory/events/actual sigmas, and never retries
automatically. Given a prepared directory and an isolated server on port 18188:

```powershell
dotnet run --project tools/h3_benchmark/exporter -- --motion-reference artifacts/YOUR-TRIAL dialogue-draft
```

Copy `motion_reference_live.py` into the trial's `/bench` mount, then run inside
the isolated container, whose own ComfyUI is on port 8188:

```text
python /bench/motion_reference_live.py /bench dialogue-draft --maximum-seconds 1200
```

Export `dialogue-final-1` after the draft completes; use `motion-*` for the
movement scene. Standard Quick controls are `*-control`, followed by `*-refine`.
The latter compose and inspect a real refinement package and freeze its split
latents. Validate each completed receipt with:

```powershell
dotnet run --project tools/h3_benchmark/exporter -- --motion-receipt-check artifacts/YOUR-TRIAL dialogue-final-1
```

`acceleration_export.py` prepares an explicitly experimental Compact or SMD graph
only after both initial promotion reviews are accepted. It pins the complete
checkpoint versus adapter role and verifies contents before capture. This is
isolated recipe screening, not a production job import path. Its conditional
FastH3 prototype additionally requires an accepted initial-candidate promotion;
preparing that path does not advertise a validated production integration.

An explicitly requested retry uses `retry_capture.py ROOT FAILED_CASE NEW_CASE
--reason TEXT`. It preserves request, seed, prompt graph and media, changes only
the transport identity, and retains the failed result. Choose its watchdog
deliberately and charge the failed attempt as well. Native promotion exceeded
20 minutes once in the October trial; a step count alone is not a latency bound.

## Historical September tooling

The scripts below document the pinned September experiment. Current application
code rejects removed Spectrum and LightX2V 8-step recipes. Reproducing those old
exports requires the corresponding historical source revision; their presence
does not make them supported current presets. Preserve the dated evidence.

## Manual acceleration comparison

The [September 11 findings](../../docs/h3/acceleration-benchmark-2026-09-11.md)
cover Standard, Turbo4/8, Sol, Spectrum, FirstBlockCache, PDD and Larry on two
neutral Ref2VA scenes. The first round produced twelve validated clips and five
failed attempts. The user ended testing after review; the exported second-seed,
Native and longer scenarios were **not executed**. Do not infer their readiness
from exported graphs or CPU validation.

Preparation copies explicitly supplied references, snapshots only H3 settings,
pins source archives, and checksum-verifies two additional weight downloads.
It creates a new directory and refuses to overwrite an experiment. Use PNG
face/outfit references and a voice recording with at least 4.248 seconds of audio.
The frozen prompts describe an adult woman with auburn hair/glasses and armor;
change those prompts deliberately for a new study rather than comparing different
conditioning as if it were the same experiment.

```powershell
python tools/h3_benchmark/prepare_acceleration.py artifacts/h3-acceleration-NEW --face "face.png" --outfit "outfit.png" --voice "voice.mp3"
python tools/h3_benchmark/prepare_acceleration.py artifacts/h3-acceleration-NEW --weights
dotnet run --project tools/h3_benchmark/exporter -- --acceleration artifacts/h3-acceleration-NEW
```

The preparer needs host FFmpeg. The exporter creates 48 application-built control
graphs without contacting ComfyUI. Create an isolated container using the frozen
image/GPU/read-only model mount shown below, substituting the new `/bench` source
directory and a distinct container name. Never attach production custom nodes.
Run these commands **inside that isolated container** (via `docker exec`):

```text
/opt/venv/bin/python /harness/prepare_acceleration.py /bench --hash-models
/opt/venv/bin/python /harness/acceleration_worker.py --name check-standard --configuration standard --validate-only
```

Repeat CPU validation for each selected configuration. Experimental packages load
only in the worker that needs them. Sol's static validation did not catch its
runtime input failure; CPU validation is not GPU verification. Do not run the GPU
worker directly: use the queue-aware controller from Windows:

```powershell
./tools/h3_benchmark/run-case.ps1 -Case acceleration -Name dialogue-standard-r1 -Container YOUR-BENCHMARK-CONTAINER -ExperimentDirectory C:/path/to/artifacts/h3-acceleration-NEW -Configuration standard -Scene dialogue -Frames 141 -Seed 20260911
```

Configuration keys are `standard`, `turbo4`, `turbo8`, `sol`, `spectrum`, `cache`,
`pdd`, and `larry`. `-Scene motion`, `-Seed 20260912`, `-Frames 243`, `-Native`,
and `-Warm` select prepared follow-ups. Native is prepared at 141 frames only.
`-Warm` executes a second pass in the same process with node-output caching
disabled; this code path is not live-validated by the September 11 results.

`run-acceleration-matrix.ps1` accepts an explicit JSON array of cases, each with
`name`, `configuration`, `scene`, `frames`, `seed`, and optional `native`/`warm`.
It calls the same controller sequentially, retains failed attempts, stops if
production work arrives, and never retries an existing attempt automatically.
Every attempt must have a unique name. The controller verifies that its artifact
directory matches the container's `/bench` mount before starting.

```powershell
python tools/h3_benchmark/acceleration_report.py artifacts/h3-acceleration-NEW
python -m http.server 8791 --bind 127.0.0.1 --directory artifacts/h3-acceleration-NEW
python -m unittest discover -s tools/h3_benchmark -p 'test_*.py'
```

The page supports paired playback, sortable measurements and per-clip diagnostic
details. An optional `review-notes.json` array of `{ "title": "...", "text": "..." }`
records attributed quality observations. Completed files from an interrupted
attempt are excluded from comparison, and unavailable memory remains unknown.
`archive_acceleration.py SOURCE NEW_DESTINATION` exports JSON evidence for Git
without copying clips, references, raw logs or weights. Full measurements and
checksums remain linked to the original local archive; it is never overwritten.

## Application preview-upscaling workflows

The optional exporter calls Lumibelle's actual workflow builder and writes API
graphs plus captured settings under the existing benchmark evidence directory:

```powershell
dotnet run --project tools/h3_benchmark/exporter --configuration Debug -- artifacts/h3-benchmark
```

It uses the frozen `h3-settings.json`, `reference.jpg` and Standard benchmark
manifest from the earlier study. It exports Standard/Turbo4/Turbo8 pairs, one
longer take, and portrait/square cases; it neither starts ComfyUI nor generates.
Existing exports are never overwritten. Review the exported requests before use.

With the isolated container configured below, explicitly run one exported case:

```powershell
./tools/h3_benchmark/run-case.ps1 -Case preview-app -Name app-20-upscaled
```

`app_preview_worker.py` validates and executes the unmodified API graph through
native ComfyUI with only the pinned Plus package. It records memory/stage
measurements, events, output dimensions, archive frames and decoded-audio hashes.
Compare matched preview/upscaled audio hashes and videos separately. This utility
does not establish a tested hardware matrix; application integration runs remain
opt-in and distinct from the earlier node-level study.

Validation status: the exporter has compiled and produced all nine graphs; the
executor has passed Python syntax checks but has not yet been run on the GPU.
The regular .NET and browser suites require no ComfyUI server or GPU.

Research tooling only. It calls native ComfyUI classes and the three pinned node
packages in a separate process. It does not use Lumibelle companion nodes, submit
production prompts, update settings, or save results into a project. Upscaling and
experimental second passes happen in the same execution; saved-take refinement
is outside this study.

## Frozen setup

| Package | Revision |
| --- | --- |
| [LBH original](https://github.com/LBH-123-AI/Comfyui_Minimax_h3_latent_Upscaler) | `d7c01b9011f2e8439493f6c02c29995a27df276f` |
| [Upscaler-Plus](https://github.com/xmarre/Comfyui_Minimax_h3_latent_Upscaler-Plus) | `db76324d6bbf231bebcb9d794e133ef4d4d9ee87` |
| [Flow-Aligned](https://github.com/xmarre/MiniMax-H3-Flow-Aligned-Regenerate) | `d876590674b27c4410ef415ff605bc7d316dcdd8` |

The original and Plus export overlapping node IDs with different inputs. The
runner imports them under separate module names and never combines their node
registries. It loads no production custom-node directory.

The September 10 setup uses the production image ID
`sha256:b3ae356d86c36d82e9900f30ec8891e28ebb1a9e27dbe2bd3f1debe78c33789f`:
ComfyUI 0.35.0, PyTorch 2.11.0+cu130, comfy-aimdo 0.5.3. The runner reproduces
`main.py`'s allocator and dynamic-VRAM initialization, uses SageAttention and
`--disable-smart-memory`, and pins CUDA to the **20 GB RTX 4000 Ada** by UUID.
The 3080 is not used. Check the recorded runtime before comparing results with
a different installation.

Artifacts live in ignored `artifacts/h3-benchmark/`:

- `vendor/{lbh,plus,flow}/<archive-root>/`: source archives extracted at the above
  revisions; keep each directory's `__init__.py` and relative package structure.
- `pins.json`: repository URLs and full revisions.
- `h3-settings.json`: the selected H3 model/encoder/video VAE/audio VAE/Turbo8 LoRA
  filenames and performance settings, with no credentials.
- `reference.jpg`: a copied existing QA reference, never modified in place.
- `preflight.json`: full SHA-256 hashes of the selected weights, FP16 upscaler,
  reference and relevant core source files.
- `production-system.json`: read-only `/system_stats` snapshot.
- `budget.json`: aggregate worker wall time, including failures and interrupted
  runs. Two hours is a planning target, not a hard limit (user correction on
  September 10). Queue waiting is excluded.
- `results/<run>/`: manifest, stage measurements, memory samples, detailed ComfyUI
  log, MP4s and lossless frame archives. Never overwrite an existing run.

The upscaler is `minimax_h3_latent_upscaler_3d_fp16.safetensors`, with offloading
enabled. Both model packages read the same checkpoint. Trajectories stay in RAM.
The harness never changes precision, resolution, attention or models on failure.
Native-node retries, such as a VAE tiled-decode fallback, must be checked in the
detailed log and reported separately if they occur.

## Isolated container

This example runs Docker through Debian WSL; bind paths must resolve inside WSL.
Replace the host paths and GPU UUID with your own.
Use the frozen image, not a moving image tag, and mount models **read-only**.

```powershell
wsl -d Debian -- docker run -d --name lumibelle-h3-benchmark-20260910 --network none `
  --gpus device=GPU-<uuid> -e H3_BENCHMARK_GPU_UUID=GPU-<uuid> `
  --mount type=bind,source=/path/to/comfyui/models,target=/comfyui/models,readonly `
  --mount type=bind,source=/path/to/lumibelle/artifacts/h3-benchmark,target=/bench `
  --mount type=bind,source=/path/to/lumibelle/tools/h3_benchmark,target=/harness,readonly `
  --entrypoint /opt/venv/bin/python `
  sha256:b3ae356d86c36d82e9900f30ec8891e28ebb1a9e27dbe2bd3f1debe78c33789f `
  -c 'import time; time.sleep(86400)'

docker exec lumibelle-h3-benchmark-20260910 /opt/venv/bin/python /harness/runner.py --case preflight
```

The container exposes no port and has no network access. Never mount the
production input, output, user or custom-node directories into it. Stop this
container when finished; leave production running.

## Execution

Use the PowerShell controller, not a bare worker invocation. It refuses to start
when ComfyUI or Lumibelle's durable queue has queued/running ComfyUI work, checks
observed GPU utilization/free memory, polls both queues every two seconds,
and terminates only the isolated worker if production work arrives, status
cannot be read, or the per-case watchdog expires (default two hours per worker,
adjustable with `-MaxRunSeconds`). Interrupted cases are not valid
performance comparisons. Resolve any recorded `running` case before another
launch. Do not reset the budget to conceal failed or interrupted attempts.

```powershell
./tools/h3_benchmark/run-case.ps1 -Case standard -Name standard-02
./tools/h3_benchmark/run-case.ps1 -Case turbo8 -Name turbo8-01
./tools/h3_benchmark/run-case.ps1 -Case comparison -Name comparison-01
./tools/h3_benchmark/run-case.ps1 -Case guided -Name guided-01
./tools/h3_benchmark/run-case.ps1 -Case progressive -Name progressive-01
./tools/h3_benchmark/run-case.ps1 -Case long -Name long-01
```

Run one command at a time and review each result before continuing. The table is
priority order; complete useful matched comparisons before extra parameter sweeps.

| Case | Configuration |
| --- | --- |
| `standard` | 73 frames, 1344×768, Standard20, res_multistep/simple |
| `turbo8` | Same target; native Turbo8 LoRA, Euler/simple, video/audio shift 12/3 |
| `comparison` | One Standard20 832×480 preview; LBH and Plus each upscale to 1344×768, followed by identical 8-step, denoise .35, locked-audio second passes |
| `guided` | One captured Standard20 preview; Plus second passes without/with direction-only guidance, identical refinement seed |
| `progressive` | Full Standard20 schedule at target size; learned handoff, source scale .80, coordinate .35, direction-only guidance |
| `long` | Matched LBH/Plus comparison at 141 frames; inspect actual chunk activation and boundaries |

All cases use 24 fps, seed 20260910, the same reference, prompt template and
encoding. Second-pass seed is 20260911. The standard prompt contains a subtle head
turn and no speech. It can test identity, motion and flicker, but **cannot establish
lip synchronization**. A harder motion/dialogue scene needs a separate matched
comparison if the budget permits. Scale .70 and additional seed sweeps come last.
For a deliberate matched repeat, pass `-Seed` to both cases. `-Frames 141` allows
a longer direct/progressive control; the `long` case always uses 141 frames.
The selected values are recorded in the result manifest.

Each cold-process run includes model loading. OS file caches may be warm.
The second comparison branch reuses the loaded process and preview, so compare
its upscaling/sampling stages separately; do not call its total a cold-start result.
Use stage records to distinguish setup, GPU sampling and diagnostic encodes.

All matched upscaling and sampling passes finish before decoding diagnostics,
so a preview decode cannot change model residency at the first handoff. Raw
upscaled clips are decoded **after** their corresponding refined clips. Their
`*-upscaled/` decode/encode/archive stages are additional diagnostics and must be
excluded from normal workflow elapsed time. Both branches share the exact preview
latent. Audio latent hashes must match exactly with locking enabled.

## Measurements and validation

- NVML samples whole-device used/free memory about every 100 ms, with stage
  endpoints sampled too. The JSONL trace is written about once per second; stage
  extrema retain the faster samples. Brief peaks may be missed, so these are lower
  bounds. They include non-PyTorch allocations and any baseline device usage.
- PyTorch peak allocated/reserved counters are reset at each stage. They describe
  the worker's allocator, not total device memory. Zero/unsupported counters under
  `cudaMallocAsync` are marked unavailable, not interpreted as zero VRAM. Dynamic
  offloading can allocate outside these counters; preserve that distinction.
- Peak process RSS, minimum available host RAM and peak host swap use are sampled.
  ComfyUI detail logs retain model load/offload diagnostics. No PCIe transfer-byte
  estimate is inferred from these logs.
- At least 2 GiB observed free VRAM is provisionally `comfortable`; less is `tight`;
  OOM is `failed`. An aborted or incomplete run is not a validated configuration.
- Decoded shape, finite pixels, encoded frame count, frame rate, audio presence,
  output hashes and exact locked-audio latent equality are checked. Manifests record
  native class calls, parameters and runtime/source fingerprints. They are execution
  receipts for this Python harness, **not** ComfyUI API prompt graphs.
- Guided runs must report a nonzero direction correction. Progressive runs must
  also report both resolutions and a learned transfer without a target fallback.
  Flow's internal wall-time events separate learned-upscaler time from sampling;
  their individual memory peaks are not measured separately from the enclosing
  sampler stage.
- Review complete MP4s for likeness, framing, motion, flicker, detail and audio.
  For LBH's 32-latent-frame chunks, inspect the reported temporal length and the
  corresponding decoded boundary neighborhood. A short, unchunked clip cannot
  establish long-clip equivalence with Plus's full-sequence processing.

CPU checks:

```powershell
python -m unittest discover -s tools/h3_benchmark -v
python -m py_compile tools/h3_benchmark/runner.py tools/h3_benchmark/metrics.py
docker exec lumibelle-h3-benchmark-20260910 /opt/venv/bin/python /harness/runner.py --case contracts --name contracts-01
python tools/h3_benchmark/report.py
python tools/h3_benchmark/compare_pixels.py artifacts/h3-benchmark/results/comparison-02 lbh plus
```

These verify tooling, not image quality or GPU compatibility. Untested durations,
resolutions, warm repetitions and absent measurements stay explicitly unverified.
`review_frames.py` creates a contact sheet covering every frame and full-resolution
boundary stills (PyAV, Pillow and NumPy; available in the benchmark image).
`compare_pixels.py` compares lossless RGB archives with Pillow, preserving a
per-frame difference record. Differences locate material for visual inspection;
they are not quality or flicker scores. MP4 byte hashes alone cannot establish
whether the generated pixels differ.

The generated `clips.html` displays pipeline time, validated resolution, duration,
frame rate, seed and sampled peak VRAM beside every output. Expand a card for
stage timing, whole-worker time and memory headroom. Paired clips include their
shared first pass plus only their own branch; raw upscales exclude refinement.
Incomplete workers retain a visible status without finalized performance claims.
Regenerate the report after a worker finishes, then refresh the local clip page.
