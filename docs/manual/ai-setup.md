# AI setup

Lumibelle works without any AI provider. Connect one when you want writing assistance, reference images or video generation. Lumibelle never downloads models, installs nodes, or switches models or providers on your behalf.

## AI settings

Open **AI settings** from Script or Assets. **Connections**, **Text models**, **Image models** and **Video models** list providers, workflows or sections in a sidebar, each with its own settings. The model tabs end with a **Defaults** entry for settings that apply to all of them, such as the global text model, the default image workflow, request timeouts and local media tools. **Text models** contains a searchable catalog with provider and starred filters; **LoRAs** holds the LoRA library. Refresh a provider explicitly to discover models. Opening settings never generates anything.

Star models to build a shortlist shared by every project. ComfyUI models must pass a test against the current server before starring; OpenRouter requires a valid key and a successful catalog check. Stars, global-default changes and successful test results save immediately. Each setup form has its own **Save** and **Cancel**.

### Project text model

All text assistance uses one model default per project, set in **Project settings → Text assistance** or through **Set as project default** in any model menu. New projects start with **Use global default**. The model chip inside assistance opens configured choices and reasoning options. A temporary override applies to one successfully queued request, and retries keep their captured inputs. Missing or incompatible models never trigger substitution.

The model menu opens from saved choices without refreshing providers. Picking a model checks its availability when needed; other choices remain usable while that check runs. **Refresh availability** checks the selected provider, and only the selected connection for OpenAI-compatible models. Opening assistance, returning to the tab and submitting reuse the current availability result while still checking for saved configuration or default changes.

Text-assistance composers share a compact dialog that becomes a full-width sheet on phones. Closing keeps draft instructions and model overrides.

### Project visual style

Set **Project settings → Visual style** to keep new asset images and reference reels in a shared medium and visual treatment. Assets and individual runs can override it. Prompt enhancement and optional vision review use the same captured direction; queued requests retain it if you change the default. See [visual style](assets.md#visual-style) for inheritance and overrides.

## ComfyUI

ComfyUI provides local text generation, reference images, image editing and video takes. [Install ComfyUI](comfyui.md) shows how to run it with the Windows portable build or Docker, where to download the node packs and model files below, and how to set up [video models](comfyui.md#video-models).

1. Start an up-to-date ComfyUI with the built-in `CLIPLoader`, `TextGenerate` and `PreviewAny` nodes.
2. Install a text-generation encoder supported by those nodes. The initial model is `gemma4_e4b_it_fp8_scaled.safetensors` when installed. Lumibelle lists every exact filename that `CLIPLoader` advertises.
3. For reference images, install a Krea 2 Turbo diffusion model, a Qwen3-VL 4B text encoder and `qwen_image_vae.safetensors`. The defaults prefer `krea2_turbo_int8_convrot.safetensors`, `qwen3vl_4b_fp8_scaled.safetensors` and `qwen_image_vae.safetensors` when detected.
4. For image editing, install the current [comfyui-krea2edit](https://github.com/lbouaraba/comfyui-krea2edit) and [comfyui-tooling-nodes](https://github.com/Acly/comfyui-tooling-nodes) node packs, restart ComfyUI, and place `krea2_identity_edit_v1_2.safetensors` in the ComfyUI LoRA directory. Missing edit dependencies disable editing without disabling image creation or manual asset work.
5. Enter the server URL (default `http://127.0.0.1:8188`) in **Connections**, check it, and save. In **Text models**, choose **Refresh ComfyUI**, then expand a model and choose **Test model**. The test starts right away. It runs the normal workflow with a fixed prompt of about 2,000 tokens and the model's own **Maximum reply tokens**, then repeats the prompt at larger sizes to measure how much fits in GPU memory. **Advanced test** opens a separate dialog for your own message. A successful result saves immediately and enables the star. The test also detects whether the model follows a native system prompt and whether it reads one image or several; **Details** and the model's settings show the result. Test again after replacing a model file or updating ComfyUI. Refresh and save the image and Krea 2 Edit LoRA files separately in **Image models**.

The advanced test accepts a custom message and output limit so you can inspect model behavior; its prompt and response are never saved. Before a model test, Lumibelle checks that ComfyUI has no running or pending work and asks it to free memory; a busy queue blocks the test instead of disturbing another job. Use **Test again** after replacing a model file without changing its name.

Each successful test records an observed token rate and, when ComfyUI reports it, VRAM use. The latest five observations per model are kept to help compare models on the same machine.

The test also estimates how large a prompt fits in GPU memory on your machine. Reading the prompt needs working memory for every prompt token at once, so prompt length matters far more than the reply limit. The model's settings show the estimate at its current reply limit and image size. A larger prompt runs out of GPU memory, or, with ComfyUI's dynamic VRAM loading, streams the model from system RAM and becomes many times slower. Such requests show a warning in their progress and in AI activity, and a failure explains that ComfyUI ran out of GPU memory. With dynamic VRAM loading, a request that does not fit is not rejected but becomes very slow; when a request writes at under a third of the speed the model reached in its test, or is still reading its prompt after two minutes, its progress says so. Lumibelle never cancels it for you. The model test treats a larger test prompt that slows down the same way as one that runs out of memory. Test again after changing the reply limit, freeing GPU memory, or updating ComfyUI.

A larger reply limit costs no time, since the model stops when it is done, only GPU memory. After a capacity test, **Allow longer replies when memory permits** (on by default) lets a request with a short prompt, such as shot drafting, use a larger limit, up to 8,192 tokens. It never lowers **Maximum reply tokens**, and an LLM profile with its own limit is left unchanged. **Request details** shows the limit a request used.

Reference images count as prompt tokens: with several references, each costs up to about 1,000 tokens at 1,024 pixels. **Reference image size** is **Automatic** by default: after a model test has measured how much fits in GPU memory, each request uses the largest size (1,024, 768 or 512 pixels) at which its prompt fits, so more references cost some detail instead of running out of memory. Without that measurement it uses 1,024 pixels. Choose a fixed size to always use it. **Request details** shows the size a request used. In Shots, **Compose prompt** checks the composition's size before queueing it, and **Include scene text** and **Include the shots before and after** can each be turned off to shorten it. Prompt assistance shows roughly how many tokens each adds.

Reference generation follows the [official Krea 2 Turbo workflow](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_krea2_turbo_t2i.json) with the exact prompt you entered. Lumibelle copies each completed image into the project.

Under **Image models**, each local workflow has expandable **Download one of these** or **Download link** sections beneath its model, text encoder, VAE and, where required, edit-LoRA selectors. They show publisher links, approximate download sizes, precision formats and the destination ComfyUI folder. The current filename is marked **Selected**; this does not mean the file has been discovered on the server. Base, Turbo and Distilled model alternatives follow the chosen **Checkpoint variant**. Download a compatible file yourself, refresh installed models, and select the exact filename. The links also lead to the publisher's model card, license and setup instructions.

### Progress and cancellation

ComfyUI queues Lumibelle's work alongside other jobs. Lumibelle shows the current workflow stage, elapsed time, generated tokens or sampling steps, and an approximate time remaining when ComfyUI supplies measurable progress. The timeout includes time spent queued. **Cancel** targets only that job; Lumibelle never sends a global interrupt or clears the queue.

### FLUX.2 Klein 9B KV

**FLUX.2 Klein 9B KV** is also available for creation and multi-reference editing. In **AI settings → Image models**, select **FLUX.2 Klein 9B KV**, refresh the catalog, and save; choose it as the default workflow under **Defaults** if you want it for new requests. Each workflow keeps its own file selections, and Assets has an **Image workflow** selector for the current run.

Install these files in the corresponding ComfyUI model directories (subfolders are supported):

- Diffusion model: `flux-2-klein-9b-kv-fp8.safetensors` in `models/diffusion_models`, from [Black Forest Labs](https://huggingface.co/black-forest-labs/FLUX.2-klein-9b-kv-fp8).
- Text encoder: `qwen_3_8b_fp8mixed.safetensors` in `models/text_encoders`, from [Comfy-Org's Klein 9B files](https://huggingface.co/Comfy-Org/flux2-klein-9B/tree/main/split_files/text_encoders).
- VAE: `flux2-vae.safetensors` in `models/vae`, from [Comfy-Org's FLUX.2 files](https://huggingface.co/Comfy-Org/flux2-dev/tree/main/split_files/vae).

Use a current ComfyUI with `FluxKVCache`, `Flux2Scheduler`, `EmptyFlux2LatentImage`, `ReferenceLatent` and the custom sampler nodes. Editing also uses `ETN_LoadImageBase64` from the tooling node pack. A missing KV model does not fall back to standard Klein, a base checkpoint, 4B or Krea.

### MOSS-Image

In **Image models → MOSS-Image**, refresh the installed files and select a MOSS-Image **Base or SFT** diffusion model, its **Qwen3.5 9B** text encoder and the **FLUX.2 VAE**. Comfy-Org provides [compatible model files](https://huggingface.co/Comfy-Org/MOSS-Image); follow the [native ComfyUI setup](https://github.com/OpenMOSS/MOSS-Image/blob/main/comfyui/README.md). Lumibelle keeps these choices separate from other workflows and preserves a missing saved choice until you replace it explicitly. Catalog checks establish installed nodes and filenames; they do not execute the model or test its architectural compatibility.

Creation defaults to 50 Euler/simple steps, CFG 3, shift 5 and negative-branch layer-22 skipping. Plain prose, tags and authored layout JSON pass through as the prompt. Configure the sampling values and optional negative prompt in the MOSS settings. The normal Assets aspect/resolution controls, optional MOSS-assigned LoRAs, background queue, candidate review and cancellation apply.

For editing, select the exact installed **Edit LoRA** and its training format. The [chinoll adapter](https://huggingface.co/chinoll/Moss-Image-Edit) uses **Moss-Image-Edit**; AI Toolkit adapters use their corresponding **ai-toolkit** format, with or without its training KV cache. Use a ComfyUI build containing the native [`TextEncodeMOSSImageEdit` node](https://github.com/Comfy-Org/ComfyUI/pull/17296), plus the existing comfyui-tooling-nodes image loader. Editing defaults to 30 steps, CFG 4 and adapter strength 1. It accepts up to four ordered references. New edits resize each captured crop to the output's pixel budget while retaining its own proportions, rather than capping every reference at 512 pixels. Describe each reference's role in the instruction. Creation remains available when the edit adapter or edit node is missing.

MOSS image-edit execution profile `moss-image-edit-v2` records this sizing policy. Saved `moss-image-edit-v1` requests remain valid and retain their earlier 512-pixel cap when resumed or retried; start a new request to use the new sizing. This execution version is separate from the prompt-enhancement guide's version. SFT is the starting point for the chinoll adapter, which was trained on it; Base is not an identity-preservation upgrade. A portrait-to-full-body restage asks for more than a local edit, so state that the same person, face and hair should be retained and specify the intended outfit and framing.

New settings apply to future runs. Queued requests retain their captured model files, sampling values, negative prompt, edit adapter/format and ordered references. Generated image metadata records the actual settings and canvas. Lumibelle does not download model files or install/update ComfyUI for you.

### FLUX 3 Image Klein

Select **Image models → FLUX 3 Image Klein** for native creation and editing in ComfyUI. This is separate from FLUX.2 Klein KV. Install a checkpoint, its encoder and VAE from the [official ComfyUI model repository](https://huggingface.co/black-forest-labs/flux-3-image-klein-comfy), accepting its access conditions first:

- `flux3-image-klein-int8-convrot.safetensors` in `models/diffusion_models` for **Distilled**, or `flux3-image-klein-base-int8-convrot.safetensors` for **Base**. The BF16 and NVFP4 files are alternatives when supported by your hardware.
- `qwen3vl_4b_int8_convrot.safetensors` in `models/text_encoders`.
- `flux2-vae.safetensors` in `models/vae`.

Refresh and select your exact filenames, then choose their **Checkpoint variant**. Distilled defaults to 16 Euler/simple steps without CFG; Base defaults to 50 steps and CFG 3.5. Steps, Base guidance and reference megapixels are editable. Missing saved filenames remain selected until you replace them. Settings and optional FLUX 3-assigned LoRAs stay separate from other workflows.

Use a current ComfyUI with `CLIPLoader` type `flux3`, `EmptyFlux2LatentImage` and, for editing, `ReferenceMultiLatent`. Reference loading uses the existing comfyui-tooling-nodes package. Editing accepts ten ordered images without an edit LoRA. Each reference keeps its crop and aspect before native resizing; **Reference size** defaults to 1 megapixel per image. Larger references, more images and the optional **4K** output canvas require more memory. The output canvas is independent of reference sizes.

Write a full scene caption even for edits. In Assets, **Enhance** can expand your instruction into a complete caption through your chosen text model; review and apply it before generating. A short instruction such as “make it blue” can change the framing. Lumibelle sends and saves the exact submitted caption without an automatic hidden rewrite. Authored layout JSON and reference markers pass through unchanged; model boxes guide placement rather than enforcing hard pixel masks.

Catalog checks verify installed nodes and filenames without executing the model. New settings affect future requests; queued requests retain their captured variant, sampling values, ordered references and canvas. Model downloads and ComfyUI updates remain separate setup actions.

### Fibo 2.0

In **Image models → Fibo 2.0**, choose **Dev** for creation and editing or **Edit Turbo** for editing only. Both use the [native ComfyUI files](https://huggingface.co/Comfy-Org/Fibo-2.0): `fibo2_dev_int8_convrot.safetensors` or `fibo2_edit_turbo_int8_convrot.safetensors` under `models/diffusion_models`, `qwen3vl_4b_int8_convrot.safetensors` under `models/text_encoders`, and `flux2-vae.safetensors` under `models/vae`. Matching BF16 files are alternatives.

Select the exact filenames and matching variant. Dev defaults to 50 Euler/simple steps and CFG 5; Edit Turbo defaults to four steps and CFG 1. The model keeps its native shift of 1.15. Negative captions apply to Dev creation; edits use the trained empty negative caption. Editing supports up to four ordered references without an edit LoRA. A single reference is fitted and center-cropped to the output canvas; multiple references are conditioned at their own trained proportions. Use Lumibelle's input crops to control the submitted framing.

Update ComfyUI for `CLIPLoader` type `fibo2` and `TextEncodeFibo2`; reference loading uses comfyui-tooling-nodes. **Enhance** proposes a structured VGL caption for review, including `edit_instruction` for edits. Incomplete VGL suggestions cannot be applied. Plain authored captions or edit instructions can also be sent directly. This workflow does not call BRIA's hosted API or the Gemini-backed VGL package.

The published weights are licensed for non-commercial use; commercial use requires a separate BRIA agreement. See the linked model repository for the current license.

### Qwen Image 2.1 Turbo

In the existing **Qwen Image 2.1** settings, select **Turbo** and its matching diffusion file, such as `qwen_image_2.1_turbo_int8_convrot.safetensors`. Use the same Qwen3-VL 8B encoder and Qwen Image 2.1 VAE as Base. [Official workflows](https://docs.comfy.org/tutorials/image/qwen/qwen-image-2-1) describe both variants.

Turbo supports creation and the same ten-reference editing, crops and output options as Base. It uses the published fixed eight-step sigma schedule with Euler and CFG 1. The step control in Assets is fixed at eight for Turbo. Merely selecting eight steps on a Base checkpoint does not select Turbo. ComfyUI must provide `ManualSigmas`, `KSamplerSelect`, `RandomNoise`, `CFGGuider` and `SamplerCustomAdvanced` in addition to the existing Qwen nodes. Queued jobs and take metadata retain the captured variant and step count.

### Z-Image

Choose **Image models → Z-Image** and select **Base** or **Turbo** with the matching `z_image_bf16.safetensors` or `z_image_turbo_bf16.safetensors` diffusion file. Both use `qwen_3_4b.safetensors` and `ae.safetensors`. Follow the official [Base setup](https://docs.comfy.org/tutorials/image/z-image/z-image) or [Turbo setup](https://docs.comfy.org/tutorials/image/z-image/z-image-turbo).

Base defaults to 25 `res_multistep`/simple steps, CFG 4 and shift 3. Turbo defaults to eight steps, CFG 1 and shift 3, without a negative prompt. Steps and shift are editable; Base also exposes CFG and a negative caption. Use ComfyUI's `lumina2` text-encoder type, `EmptySD3LatentImage` and `ModelSamplingAuraFlow`. This integration supports creation with the normal aspect, resolution, LoRA, queue and review tools. Reference editing and ControlNet are not enabled here; choose another workflow for editing.

Each family saves its own files and sampling settings. Refresh never substitutes a missing file, availability checks do not execute models, and settings changes affect future requests only.

### LoRAs

In **AI settings → LoRAs**, refresh installed files and register an exact file with a friendly name, workflow assignment, default strength and optional trigger text. Registrations belong to their ComfyUI server; they organize installed files without downloading them or testing compatibility. Krea's required identity-edit LoRA stays separate.

Add comma-separated **Tags** to LoRA registrations, then search or filter the library by tag. In a project's **Settings** tab, **LoRA visibility** applies to every asset and both Create and Edit:

- Empty filters show all LoRAs.
- **Only show these tags** matches any listed tag and hides untagged LoRAs.
- **Hide these tags** always wins, even when an allowed tag also matches. For example, hide `nsfw` in an SFW project.

An already selected, enabled LoRA that the project now excludes blocks generation until it is disabled, removed or allowed again. Each batch checks the saved project rules when it starts; changes never rewrite earlier images or their recorded LoRAs. To use LoRAs on an asset, see [Assets studio](assets.md#loras).

## OpenAI-compatible servers

Connect servers such as vLLM and Strata on this computer or your network. Multiple connections can remain enabled at the same time:

1. Open **Connections → OpenAI-compatible**, choose **Add connection**, give the server a name, and enter its API base URL, such as `http://llm-server:8000/v1`. A bare server address automatically uses `/v1`; include the full API path when using a reverse proxy. Existing vLLM settings appear as a connection automatically.
2. Enter an API key only if the server requires one. Choose **Check connection**, then **Save connection**.
3. Open **Text models → OpenAI-compatible**, refresh, and star a served model. Filter by connection when needed. Select it in **Assist**, or set it as the global default under **Defaults & profiles**. Choices include the connection name, so the same model ID on two servers stays distinct.

Checks read the model catalog and optional compatibility metadata without generating text. The server must serve a chat-capable model with a chat template. Each connection has its own concurrent-request limit, initially one; work on one server does not block another. Disabling a connection prevents new dispatches while active requests finish. The OpenAI-compatible pause in AI activity pauses all these connections.

Expand a model under **Text models → OpenAI-compatible** to set its temperature and maximum reply tokens. Blank fields use the server defaults; **Use server defaults** clears both overrides. Settings belong to the exact model and server. LLM profiles override individual values, and queued requests keep the values captured when submitted. **Test model** runs a short benchmark with the saved temperature and reply limit (2,048 tokens when no limit is set). **Advanced test** accepts a custom message and reply limit. **Details** reopens the saved response and measurements without generating again. Tests retain their server and settings; output speed includes reasoning and time waiting for the response. GPU memory and prompt-capacity measurements are available for ComfyUI only.

Saved choices and queued requests retain their server address. Changing an address does not transfer its saved key to the new server; credentials are stored protected and never included in queued requests. Removing or disabling a server makes its saved choices unavailable instead of routing them elsewhere.

**Compatibility & capabilities** normally uses automatic detection. Lumibelle recognizes vLLM and Strata metadata; **Standard OpenAI-compatible** uses ordinary chat requests. Select the runtime explicitly if a proxy hides its metadata. vLLM sequence drafting uses constrained JSON generation. Strata streams normally using the JSON format instructions in the prompt; Lumibelle validates the completed brief, screenplay and review locally. Existing captured requests keep their original format, including server validation and buffering when a JSON schema was captured.

Context windows come from the served model's metadata. Context is a runtime setting: refresh the catalog after changing it. Image support uses saved model tests and server metadata; absent metadata means **Image input unverified**. **Test model** and advanced tests check single and multiple images after a successful text response, using the same random-number and color challenges as ComfyUI. Probe time is separate from text throughput. Results belong to the exact server and model, and interrupted tests are never automatically repeated. An inconclusive image check leaves the text benchmark available; only an explicit image rejection marks input unsupported. Refresh the model picker after testing.

Compatible-server image checks allow at least 2,048 output tokens so reasoning can finish before the answer. A higher test reply limit also applies to these checks, up to 8,192 tokens. If a check exhausts its budget without an answer, its result explains the limit rather than reporting an image-reading failure. Native vLLM checks disable thinking and keep their shorter probe budget.

JSON-schema output is discovered per server and model from advertised `structured_outputs` capabilities or a successful **Test model** check. A catalog that advertises only `response_format` may support JSON-object mode without schemas, so it is not treated as proof. Compatible-server tests send a small synthetic schema and verify its exact constrained answer. Unknown or explicitly unsupported models retain prompt-based JSON and local validation. New supported requests use standard `response_format: json_schema`; native vLLM retains its own structured-output API. Captured requests keep their original schema and protocol settings during recovery.

When metadata is missing or inaccurate, set an explicit capability override on the connection. Overrides take priority over model tests. A context override applies to all models on that connection; leave it empty to follow the server. Reference images require image support. Configure model-specific reasoning on the server.

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
