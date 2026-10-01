# Install ComfyUI

[ComfyUI](https://github.com/Comfy-Org/ComfyUI) runs Lumibelle's local text generation, reference images, image editing and video takes on your own graphics card. Lumibelle connects to it over HTTP, so it can run on the same computer or another machine you control. You only need it for local generation: writing, asset editing and cutting work without it, and hosted text models are covered in [AI setup](ai-setup.md).

Choose one way to run it:

- [Windows portable](#windows-portable): the official standalone build. Extract and run, no installation.
- [Docker](#docker): a prebuilt image with ComfyUI and its dependencies. Updating means pulling a new image, so an update cannot break an existing installation. Suits Linux, Windows with Docker Desktop, and cloud GPU providers.

Both need a current NVIDIA driver. Either way, you then [add two node packs](#node-packs), download the [text and image models](#text-and-image-models) and [video models](#video-models), and [connect Lumibelle](#connect-lumibelle).

## Windows portable

1. Download [`ComfyUI_windows_portable_nvidia.7z`](https://github.com/Comfy-Org/ComfyUI/releases/latest/download/ComfyUI_windows_portable_nvidia.7z) from the [latest ComfyUI release](https://github.com/Comfy-Org/ComfyUI/releases/latest). It supports NVIDIA 20 series and later; for older cards use `ComfyUI_windows_portable_nvidia_cu126.7z` from the same release.
2. Extract it with [7-Zip](https://7-zip.org), or with File Explorer on a recent Windows version, to a drive with plenty of free space.
3. Run `run_nvidia_gpu.bat` in the extracted `ComfyUI_windows_portable` folder. ComfyUI opens at [http://127.0.0.1:8188](http://127.0.0.1:8188). Keep the console window open while you use Lumibelle.

If ComfyUI does not start, update your NVIDIA driver. A `c10.dll` error means the [Visual C++ Redistributable](https://aka.ms/vc14/vc_redist.x64.exe) is missing.

In this build, models go in `ComfyUI_windows_portable\ComfyUI\models` and node packs in `ComfyUI_windows_portable\ComfyUI\custom_nodes`. To update ComfyUI later, close it and run `update\update_comfyui.bat`.

## Docker

The [comfyui-docker](https://github.com/radiatingreverberations/comfyui-docker) images package ComfyUI with its Python dependencies and are rebuilt when ComfyUI releases. Use `comfyui-extensions`, which adds [ComfyUI-Manager](https://github.com/Comfy-Org/ComfyUI-Manager) and installs the requirements of your node packs each time the container starts.

You need Docker with GPU support: [Docker Desktop](https://docs.docker.com/desktop/) on Windows, or Docker Engine with the [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html) on Linux.

Create a folder for ComfyUI, save this as `compose.yaml` in it, and run `docker compose up -d` there:

```yaml
services:
  comfyui:
    image: ghcr.io/radiatingreverberations/comfyui-extensions:latest
    container_name: comfyui
    ports:
      - "127.0.0.1:8188:8188"
    volumes:
      - ./models:/comfyui/models
      - ./custom_nodes:/comfyui/custom_nodes
      - ./user:/comfyui/user
      - ./input:/comfyui/input
      - ./output:/comfyui/output
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]
    restart: unless-stopped
```

ComfyUI opens at [http://127.0.0.1:8188](http://127.0.0.1:8188). The mounted folders keep your models, node packs and settings when the container is replaced; without them, everything is lost on update. The `127.0.0.1` port binding keeps ComfyUI reachable only from this computer. To run it on another machine, see the image's notes on [SSH tunnels](https://github.com/radiatingreverberations/comfyui-docker/blob/main/SSH.md) rather than exposing the port.

Models go in `models` and node packs in `custom_nodes` beside `compose.yaml`. To update ComfyUI, run `docker compose pull`, then `docker compose up -d`. Update the image rather than updating ComfyUI from inside it.

On Windows, Docker runs in a WSL2 virtual machine, and models load slowly from Windows drives. Keep the ComfyUI folder on the Linux file system, for example under `\\wsl$\Ubuntu\home\<you>`, if loading is slow.

## Node packs

Image editing needs two node packs. Reference images and text generation work without them.

- [comfyui-krea2edit](https://github.com/lbouaraba/comfyui-krea2edit) (**Krea 2 Identity Edit**)
- [comfyui-tooling-nodes](https://github.com/Acly/comfyui-tooling-nodes)

With [Git](https://git-scm.com), run these in the `custom_nodes` folder:

```powershell
git clone https://github.com/lbouaraba/comfyui-krea2edit
git clone https://github.com/Acly/comfyui-tooling-nodes
```

Without Git, choose **Code → Download ZIP** on each GitHub page and extract it so that its folder sits directly inside `custom_nodes`. With the Docker image you can instead search for both in ComfyUI-Manager and install them there.

Neither pack needs extra Python packages. Restart ComfyUI afterwards: close and rerun `run_nvidia_gpu.bat`, or run `docker compose restart`. Update a pack with `git pull` in its folder, or by downloading it again.

## Text and image models

Download these files into the listed folders under `models`. Together they take about 30 GB. Subfolders are allowed. These are the files Lumibelle selects by default; other variants, such as `krea2_turbo_fp8_scaled.safetensors`, can be chosen in **AI settings**.

| File | Folder | Used for |
| --- | --- | --- |
| [`gemma4_e4b_it_fp8_scaled.safetensors`](https://huggingface.co/Comfy-Org/gemma-4/blob/main/text_encoders/gemma4_e4b_it_fp8_scaled.safetensors) | `text_encoders` | Text generation |
| [`krea2_turbo_int8_convrot.safetensors`](https://huggingface.co/Comfy-Org/Krea-2/blob/main/diffusion_models/krea2_turbo_int8_convrot.safetensors) | `diffusion_models` | Reference images and editing |
| [`qwen3vl_4b_fp8_scaled.safetensors`](https://huggingface.co/Comfy-Org/Krea-2/blob/main/text_encoders/qwen3vl_4b_fp8_scaled.safetensors) | `text_encoders` | Reference images and editing |
| [`qwen_image_vae.safetensors`](https://huggingface.co/Comfy-Org/Krea-2/blob/main/vae/qwen_image_vae.safetensors) | `vae` | Reference images and editing |
| [`krea2_identity_edit_v1_2.safetensors`](https://huggingface.co/conradlocke/krea2-identity-edit/blob/main/krea2_identity_edit_v1_2.safetensors) | `loras` | Editing |

Choose the download button on each Hugging Face page. You can start with only the files for the features you want; Lumibelle disables what is missing and leaves the rest working. [FLUX.2 Klein 9B KV](ai-setup.md#flux2-klein-9b-kv) is an alternative image workflow with its own files.

## Video models

Shots generates takes with [MiniMax H3](https://huggingface.co/MiniMaxAI/MiniMax-H3) Ref2VA, which runs on nodes built into current ComfyUI, so no node pack is needed. It needs four files, about 43 GB in total:

| File | Folder | Source |
| --- | --- | --- |
| [`Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8.safetensors`](https://huggingface.co/WarmBloodAban/Minimax-h3_Singularity/blob/main/Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8.safetensors) | `diffusion_models` | [Singularity](https://huggingface.co/WarmBloodAban/Minimax-h3_Singularity) |
| [`qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors`](https://huggingface.co/koongrizzly/MiniMax_H3_int4_W4A8_ConvRot_Pruned/blob/main/text_encoders/qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors) | `text_encoders` | [koongrizzly](https://huggingface.co/koongrizzly/MiniMax_H3_int4_W4A8_ConvRot_Pruned) |
| [`minimax_h3_video_vae_fp16.safetensors`](https://huggingface.co/Comfy-Org/MiniMax-H3/blob/main/vae/minimax_h3_video_vae_fp16.safetensors) | `vae` | [Comfy-Org](https://huggingface.co/Comfy-Org/MiniMax-H3) |
| [`minimax_h3_audio_vae_fp32.safetensors`](https://huggingface.co/Comfy-Org/MiniMax-H3/blob/main/vae/minimax_h3_audio_vae_fp32.safetensors) | `vae` | [Comfy-Org](https://huggingface.co/Comfy-Org/MiniMax-H3) |

Use Singularity rather than the official Ref2VA model. MiniMax released two H3 checkpoints: FL2VA, which starts from a first or last frame, and Ref2VA, the only one that takes reference images. Ref2VA has a known training-quality problem that makes its output noticeably worse than FL2VA's, even without references. Community models work around it by [merging the two](https://huggingface.co/smhfacct/Minimax-H3-fl2va-ref2va-hybrid-models): FL2VA's weights for picture and sound quality, Ref2VA's for following references. Singularity is built on such a merge and finetuned further. The official [`minimax_h3_ref2va_pruned_int8_convrot.safetensors`](https://huggingface.co/Comfy-Org/MiniMax-H3/blob/main/diffusion_models/minimax_h3_ref2va_pruned_int8_convrot.safetensors) still works, at that lower quality. Comfy-Org also publishes the [`qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors`](https://huggingface.co/Comfy-Org/MiniMax-H3/blob/main/text_encoders/qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors) encoder, which is meant for NVIDIA RTX 50 series cards; the W4A8 encoder above is Lumibelle's default.

Lumibelle also suggests these alternatives:

- [`Minimax-h3_Singularity_ref2va_v1.3_Pruned_w4a8.safetensors`](https://huggingface.co/WarmBloodAban/Minimax-h3_Singularity/blob/main/Minimax-h3_Singularity_ref2va_v1.3_Pruned_w4a8.safetensors) (about 12 GB), from the same repository: Singularity at about half the size, for cards with less memory.
- [`minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors`](https://huggingface.co/smhfacct/Minimax-H3-fl2va-ref2va-hybrid-models/blob/main/minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors) (21 GB): a plain merge of the official weights, without further training. The same repository has `b20-49` and `b15-49`, which follow references more closely at some cost in quality, and `b30-49`, which leans the other way.

To select the files, open **AI settings → Video models → MiniMax H3 Ref2VA** and choose **Refresh video models**. Singularity and the W4A8 encoder are the defaults; the **Model** list also offers other Ref2VA files, including FL2VA/Ref2VA merges. Choose the encoder and both VAEs, then **Save MiniMax H3**. Lumibelle recognizes these by name, so if a renamed file is missing from the list, turn on **Show all installed files (advanced)**. **Generation presets** below the files shows which presets are ready to use.

![Video models with Singularity, the W4A8 encoder and both VAEs selected](https://lumibelle.ai/media/manual/ai-settings-video-models.png)

### Faster takes

The Turbo presets need a Ref2V Turbo LoRA in `models/loras`. Choose the matching file under each preset in **Generation presets**.

- **Turbo · 4 steps**: [`minimax_h3_ref2v_turbo_4step_v0.1_comfyui_bf16.safetensors`](https://huggingface.co/Comfy-Org/MiniMax-H3/blob/main/loras/minimax_h3_ref2v_turbo_4step_v0.1_comfyui_bf16.safetensors). The Singularity author suggests it at strength 0.75 to 1.0, so **Turbo · 4 steps · 0.75** is a good start with Singularity.
- **Turbo · 8 steps**: [`minimax_h3_ref2v_turbo_8step_v1.0_768p_comfyui_bf16.safetensors`](https://huggingface.co/lightx2v/Minimax-h3-Turbo/blob/main/minimax_h3_ref2v_turbo_8step_v1.0_768p_comfyui_bf16.safetensors).

Use the `ref2v` files. The `fl2v` LoRAs beside them belong to a different workflow.

### Upscaled preview

**Upscaled preview** in the Shots resolution picker samples at a smaller size and upscales the result to full size in the same job. It needs the [Minimax H3 latent upscaler](https://github.com/LBH-123-AI/Comfyui_Minimax_h3_latent_Upscaler) node pack, installed like the [node packs](#node-packs) above, and [`minimax_h3_latent_upscaler_3d_conv_v1_fp16.safetensors`](https://huggingface.co/LBH-123-AI/Minimax_h3_latent_Upscaler/blob/main/minimax_h3_latent_upscaler_3d_conv_v1/minimax_h3_latent_upscaler_3d_conv_v1_fp16.safetensors) in `models/latent_upscale_models`. Restart ComfyUI, then refresh, select the checkpoint and save in **Video models → Preview upscaling**.

## Connect Lumibelle

With ComfyUI running, open **AI settings → Connections** in Lumibelle, enter `http://127.0.0.1:8188`, check the connection and save.

![The ComfyUI connection after a successful check](https://lumibelle.ai/media/manual/ai-settings-comfyui-connection.png)

Then test a text model and select the image files as described in [AI setup](ai-setup.md#comfyui), and the video files as described [above](#video-models).

Lumibelle never installs nodes or downloads models for you. When a feature reports a missing node or file, add it here and refresh in **AI settings**.
