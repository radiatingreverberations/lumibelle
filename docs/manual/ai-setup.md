# AI setup

Lumibelle works without any AI provider. Connect one when you want writing assistance, reference images or video generation. Lumibelle never downloads models, installs nodes, or switches models or providers on your behalf.

## AI settings

Open **AI settings** from Script or Assets. **Connections**, **Text models**, **Image models** and **Video models** list providers, workflows or sections in a sidebar, each with its own settings. The model tabs end with a **Defaults** entry for settings that apply to all of them, such as the global text model, the default image workflow, request timeouts and local media tools. **Text models** contains a searchable catalog with provider and starred filters; **LoRAs** holds the LoRA library. Refresh a provider explicitly to discover models. Opening settings never generates anything.

Star models to build a shortlist shared by every project. ComfyUI models must pass a test against the current server before starring; OpenRouter requires a valid key and a successful catalog check. Stars, global-default changes and successful test results save immediately. Each setup form has its own **Save** and **Cancel**.

### Project text model

All text assistance uses one model default per project, set in **Project settings → Text assistance** or through **Set as project default** in any model menu. New projects start with **Use global default**. The model chip inside assistance opens configured choices and reasoning options. A temporary override applies to one successfully queued request, and retries keep their captured inputs. Missing or incompatible models never trigger substitution.

Text-assistance composers share a compact dialog that becomes a full-width sheet on phones. Closing keeps draft instructions and model overrides.

## ComfyUI

ComfyUI provides local text generation, reference images and image editing.

1. Start an up-to-date ComfyUI with the built-in `CLIPLoader`, `TextGenerate` and `PreviewAny` nodes.
2. Install a text-generation encoder supported by those nodes. The initial model is `gemma4_e4b_it_fp8_scaled.safetensors` when installed. Lumibelle lists every exact filename that `CLIPLoader` advertises.
3. For reference images, install a Krea 2 Turbo diffusion model, a Qwen3-VL 4B text encoder and `qwen_image_vae.safetensors`. The defaults prefer `krea2_turbo_int8_convrot.safetensors`, `qwen3vl_4b_fp8_scaled.safetensors` and `qwen_image_vae.safetensors` when detected.
4. For image editing, install the current [comfyui-krea2edit](https://github.com/lbouaraba/comfyui-krea2edit) and [comfyui-tooling-nodes](https://github.com/Acly/comfyui-tooling-nodes) node packs, restart ComfyUI, and place `krea2_identity_edit_v1_2.safetensors` in the ComfyUI LoRA directory. Missing edit dependencies disable editing without disabling image creation or manual asset work.
5. Enter the server URL (default `http://127.0.0.1:8188`) in **Connections**, check it, and save. In **Text models**, choose **Refresh ComfyUI**, then expand a model and choose **Test model**. The test starts right away and runs the normal workflow with a fixed script-sized prompt of about 8,000 tokens and the model's own **Maximum reply tokens**, so the measured VRAM reflects real requests. A successful result saves immediately and enables the star. The test also detects whether the model follows a native system prompt and whether it reads one image or several; **Details** and the model's settings show the result. Test again after replacing a model file or updating ComfyUI. Refresh and save the image and Krea 2 Edit LoRA files separately in **Image models**.

**Advanced test** opens a separate dialog that accepts a custom message and output limit so you can inspect model behavior; its prompt and response are never saved. Before a model test, Lumibelle checks that ComfyUI has no running or pending work and asks it to free memory; a busy queue blocks the test instead of disturbing another job. Use **Test again** after replacing a model file without changing its name.

Each successful test records an observed token rate and, when ComfyUI reports it, VRAM use. ComfyUI reserves memory for the prompt plus the reply limit before generating, so raising **Maximum reply tokens** raises VRAM use; the model's details ask you to test again when the setting no longer matches the measurement. The latest five observations per model are kept to help compare models on the same machine.

Reference generation follows the [official Krea 2 Turbo workflow](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_krea2_turbo_t2i.json) with the exact prompt you entered. Lumibelle copies each completed image into the project.

### Progress and cancellation

ComfyUI queues Lumibelle's work alongside other jobs. Lumibelle shows the current workflow stage, elapsed time, generated tokens or sampling steps, and an approximate time remaining when ComfyUI supplies measurable progress. The timeout includes time spent queued. **Cancel** targets only that job; Lumibelle never sends a global interrupt or clears the queue.

### FLUX.2 Klein 9B KV

**FLUX.2 Klein 9B KV** is also available for creation and multi-reference editing. In **AI settings → Image models**, select **FLUX.2 Klein 9B KV**, refresh the catalog, and save; choose it as the default workflow under **Defaults** if you want it for new requests. Each workflow keeps its own file selections, and Assets has an **Image workflow** selector for the current run.

Install these files in the corresponding ComfyUI model directories (subfolders are supported):

- Diffusion model: `flux-2-klein-9b-kv-fp8.safetensors` in `models/diffusion_models`, from [Black Forest Labs](https://huggingface.co/black-forest-labs/FLUX.2-klein-9b-kv-fp8).
- Text encoder: `qwen_3_8b_fp8mixed.safetensors` in `models/text_encoders`, from [Comfy-Org's Klein 9B files](https://huggingface.co/Comfy-Org/flux2-klein-9B/tree/main/split_files/text_encoders).
- VAE: `flux2-vae.safetensors` in `models/vae`, from [Comfy-Org's FLUX.2 files](https://huggingface.co/Comfy-Org/flux2-dev/tree/main/split_files/vae).

Use a current ComfyUI with `FluxKVCache`, `Flux2Scheduler`, `EmptyFlux2LatentImage`, `ReferenceLatent` and the custom sampler nodes. Editing also uses `ETN_LoadImageBase64` from the tooling node pack. A missing KV model does not fall back to standard Klein, a base checkpoint, 4B or Krea.

### LoRAs

In **AI settings → LoRAs**, refresh installed files and register an exact file with a friendly name, workflow assignment, default strength and optional trigger text. Registrations belong to their ComfyUI server; they organize installed files without downloading them or testing compatibility. Krea's required identity-edit LoRA stays separate.

Add comma-separated **Tags** to LoRA registrations, then search or filter the library by tag. In a project's **Settings** tab, **LoRA visibility** applies to every asset and both Create and Edit:

- Empty filters show all LoRAs.
- **Only show these tags** matches any listed tag and hides untagged LoRAs.
- **Hide these tags** always wins, even when an allowed tag also matches. For example, hide `nsfw` in an SFW project.

An already selected, enabled LoRA that the project now excludes blocks generation until it is disabled, removed or allowed again. Each batch checks the saved project rules when it starts; changes never rewrite earlier images or their recorded LoRAs. To use LoRAs on an asset, see [Assets studio](assets.md#loras).

## OpenRouter

1. Enter your own OpenRouter API key in **Connections**, check it, and choose **Save key**.
2. In **Text models**, choose **Refresh OpenRouter**, search for a model, and star it. Optionally choose **Set default**.
3. Select the starred model through the model chip inside **Assist**.

Checking the key and refreshing models generate no text. Requests stream text into the assistant; a model must be explicitly selected, and Lumibelle does not retry paid generation requests automatically.

Default advanced settings are temperature **0.7**, maximum output **2,048 tokens** and a **300-second** timeout. Authentication, credit, rate-limit, context, model, connection and execution errors are shown. Partial text and instructions remain available after a failure or cancellation.

## Claude Code

Lumibelle can use an installed Claude Code CLI for text assistance, including optional image inspection. It runs with the account you set the CLI up with, and never signs in, reads credentials or modifies the CLI. Claude Code cannot generate images.

1. Install Claude Code 2.1.259 or later (see [Claude Code setup](https://code.claude.com/docs/en/setup)), open a new terminal and run `claude` once to sign in, as the same operating-system user that runs Lumibelle. **Connections → Claude Code → Install the Claude Code CLI** shows the command for your system.
2. Open **AI settings → Connections → Claude Code**, enable the provider, and **Check connection**. Leave the executable path empty for detection, or select the installed executable.
3. Save the connection. The check shows the provider, account and sign-in method that requests will use; it generates no content.
4. In **Text models**, star the models you want and optionally set **Default Claude Code reasoning effort**.

Claude Code does not report remaining usage, so Lumibelle shows no allowance meter. When a request ends because of a usage or rate limit, it needs attention and the Claude Code queue pauses until you choose **Resume**.

## Codex

Lumibelle can use an installed Codex CLI for text assistance and image Create/Edit, with the account the CLI is set up with.

1. Install Codex CLI 0.153.4 or later (see [Codex CLI](https://learn.chatgpt.com/codex/cli)), then run `codex login` as the same operating-system user that runs Lumibelle. **Connections → Codex → Install the Codex CLI** shows the command for your system.
2. Open **AI settings → Connections → Codex**, enable the provider, and **Check connection**.
3. Save the connection. The check reports the CLI version, account and, for ChatGPT sign-ins, the plan allowance; it generates no content.
4. In **Text models**, refresh Codex, star the models you want and optionally set **Default Codex reasoning effort**.
5. For images, select **Codex Images** in **Image models**, refresh, choose the **Codex agent model** and **Agent reasoning effort**, and save. Choose it under **Image models → Defaults** to use it for new image requests.

Codex image edits support one base plus up to seven additional references, each with its own crop. For ChatGPT sign-ins, the plan allowance is shown for information only. When Codex reports an exhausted limit, that request needs attention and the Codex queue pauses until you choose **Resume**.
