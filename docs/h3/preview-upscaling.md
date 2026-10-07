# Upscaled preview

Choose **Upscaled preview** in the Shots resolution picker to sample at preview
size and save native-size video in one job. It uses the chosen Standard or Turbo
sampling mode, followed by a learned 3D latent upscale and the normal decode.
Audio is retained from the same sampling pass. No extra sampling pass is added.

| Aspect | Sampling | Output |
| --- | --- | --- |
| Landscape | 832 × 480 | 1344 × 768 |
| Portrait | 480 × 832 | 768 × 1344 |
| Square | 640 × 640 | 992 × 992 |

Upscaling, decoding and saving larger frames add time. Fine detail can be softer
than native generation. This option is not a guarantee of equal preview speed or
lower peak VRAM, even on GPUs that can generate natively. Defaults remain unchanged.

## Setup

Install **one** supported implementation in ComfyUI:

- [LBH original](https://github.com/LBH-123-AI/Comfyui_Minimax_h3_latent_Upscaler/tree/d7c01b9011f2e8439493f6c02c29995a27df276f), tested at `d7c01b9011f2e8439493f6c02c29995a27df276f`.
- [Upscaler-Plus](https://github.com/xmarre/Comfyui_Minimax_h3_latent_Upscaler-Plus/tree/db76324d6bbf231bebcb9d794e133ef4d4d9ee87), tested at `db76324d6bbf231bebcb9d794e133ef4d4d9ee87`.

They register the same `MinimaxH3LatentUpscaler3D` ID with different inputs; do not
install both. Place a compatible 3D SafeTensors checkpoint under
`models/latent_upscale_models`; the FP16 checkpoint is recommended. Restart
ComfyUI, open **Video models**, expand **Upscaled preview** under **Optional add-ons**,
choose **Change** on the checkpoint row, select it, and save. Native `LTXVSeparateAVLatent` and `LTXVConcatAVLatent` nodes must exist.

Lumibelle detects the input/output contract, not an installed Git revision.
Inference uses CUDA, FP16, exact target dimensions and alignment 32. LBH enables
temporal chunking and unloading; Plus processes the full sequence, uses exact
dimensions and offloads afterward. Runtime GPU capacity remains server-dependent.

## Captured jobs and saved output

The implementation, checkpoint, output dimensions and execution settings are
captured when queued. Changing setup later cannot silently switch an existing
job to another upscaler. Restore its captured setup or create a new batch.

The MP4 and lossless frame archives remain available for editing and frame
selection. Upscaled-preview jobs do not capture saved-refinement packages and
require no Lumibelle companion nodes. Existing Preview/Native jobs and saved
refinement behavior are unchanged. Timings show observed upscaling elapsed time;
missing observations are reported as unavailable, not zero.
