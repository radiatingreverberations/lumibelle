# Development

The user manual lives in [docs/manual](manual/README.md) and is published at [lumibelle.ai](https://lumibelle.ai/manual/). Keep it plain Markdown: the website derives each page title from its first `# Heading` and supplies all styling and navigation. Links between manual pages should be relative (`script.md#assistant`); links from the manual to other files in this repository do not resolve on the website. The website's sidebar order is set in the `lumibelle-web` repository, so a new manual page needs an entry there.

For architecture, launch/publish commands and platform status, see [shared hosts and distribution](shared-hosts.md). For UI work, use the [UX guidelines](ux-guidelines.md). They describe the intended design.

## Test

```powershell
dotnet build lumibelle.slnx
dotnet test lumibelle.slnx
dotnet test lumibelle.slnx --list-tests
```

`Lumibelle.Tests` uses xUnit v3 and bUnit. The `xunit.v3.mtp-off` package selects the VSTest-compatible variant, with `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk` enabling `dotnet test` and Visual Studio Test Explorer. Open the solution and build to discover the tests.

Storage tests create isolated temporary folders and clean them up. Component tests use a fake `IProjectStore` and exercise application behavior; they need neither user projects nor ComfyUI. Test files and local project data are excluded from the web application's compilation and publishing.

### Frontend and browser checks

The Tiptap editor is bundled into `src/Lumibelle.UI/wwwroot/script-editor.js` and runs locally. Normal .NET runs use the checked-in bundle. After editing `src/Lumibelle.UI/Client/script-editor.js`, install Node.js and rebuild:

```powershell
npm ci
npm run build
npm run test:unit
npm test
```

Browser tests use Playwright with installed Microsoft Edge and a separate `Lumibelle.BrowserHost` process for each test, listening on a dynamically assigned loopback port. The fixture builds the host once, isolates queue and settings state between tests, and removes its temporary data after each test. The host uses the real UI and file stores with a temporary library and entirely mocked text/image providers; it never reads your AI credentials or project data. On a machine without Edge, install it with `npx playwright install msedge`. Tests cover the writing-to-assets flow, formatting, selections, undo, paste, conflicts, reconnects, keyboard controls, and mobile layouts. Browser screenshots and reports are ignored.

## Architecture and verification

Components use `IScriptStore`, `IAssistantHistoryStore`, `IAssetStore`, `IAiSettingsStore`, `IScriptAssistant`, `IAssetExtractor`, `IReferenceImageGenerator`, and `IReferenceImageEditor`. `IAiProviderRegistry` supplies Microsoft.Extensions.AI `IChatClient` instances for OpenRouter and ComfyUI text. Asset extraction reuses that boundary; the ComfyUI image adapters own workflow submission, in-memory source preparation, scoped cancellation, history polling, and download. Prompts, validation, HTTP, and filesystem operations remain outside Razor markup.

The xUnit/bUnit suite covers project, script, assistant, asset and image persistence; conflicts and recovery; context construction; backend contracts and cancellation; sanitized Markdown; settings; autosave; proposal application; reference approval; and retained edits after failures. HTTP tests use fakes; no automated test requires a GPU, ComfyUI, real user projects, or an API key. Browser checks additionally exercise actual keyboard and responsive behavior.

## Implementation notes

### Storage

Project creation writes into a `.creating-<id>` staging directory, then renames the directory within the same library after the manifest is complete. Interrupted staging writes are ignored.

Script files are published through same-directory atomic replacement; unpublished temporary files are ignored. Historical approved snapshots remain readable through the source compatibility loader. Script proposals are versioned JSON operations that replace ranges, insert, delete, or move captured blocks while preserving block IDs. Storage and editor acknowledgement complete before the next request can capture the applied document. Transport retries reuse their captured request.

Trash metadata lives alongside active images in each project's `assets.json`; files stay in their original locations until permanent deletion. Membership changes publish atomically under the project lock, and ordinary metadata saves cannot bypass Trash. A hosted cleanup service checks at startup and hourly while Lumibelle runs. Purges publish durable deletion intent before removing files, so interrupted or failed cleanup resumes safely after restart.

Settings API keys are protected with ASP.NET Core Data Protection using application name `Lumibelle` and the hosting account's default persistent key ring.

AI activity: a new publication advances its observation version; reading an older result cannot consume a later notification. Provider capacity checks are bounded and shared between drawers. OpenRouter charges come from `usage.cost`, including final usage-only stream events. ComfyUI memory reporting follows [model_management.py](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy/model_management.py); OpenRouter allowance comes from `/api/v1/key`.

### Prompt enhancement

Four versioned profiles are embedded in the application; the supplied guides and integration notes are in [prompt-enhancement documentation](prompt-enhancement/README.md). Requests capture their context once. Previews and Undo are visit-local; generated images retain the final submitted prompt through existing metadata.

### ComfyUI text

The native text-only workflow uses `CLIPLoader` with type `stable_diffusion`, `TextGenerate` with thinking disabled and a fresh seed, and `PreviewAny` to return text. See the [official Gemma 4 workflow](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/llm_gemma4_text_gen.json). Before either model test, Lumibelle verifies that ComfyUI has no running or pending work, then asks it to unload models and free cached memory. Successful checks are stored in ignored `App_Data/ai-settings.json` and match the normalized ComfyUI URL, reported ComfyUI version, and exact filename. Recorded VRAM values are baseline and peak device VRAM plus active PyTorch allocation; they are approximate runtime measurements rather than model file sizes, and other GPU activity can affect them.

The standard benchmark prefixes the fixed instruction with a deterministic screenplay of about 2k tokens (`ComfyTextBenchmark`; 64 scenes are 8,283 Qwen and 7,653 Llama 3 tokens) and uses the model's own reply-token setting as `max_length`. `TextGenerate` preallocates its KV cache for prompt length plus `max_length`, and reading the prompt allocates working memory for all prompt tokens at once; for Qwen3.5-style models the linear-attention prefill (`torch_chunk_gated_delta_rule`) dominates. The model still stops at its natural end, so the reply limit costs little time. Benchmarks record `ContextTokens`; earlier results and results measured with a different reply limit are flagged for retesting.

`ComfyTextCapacity.MeasureAsync` then runs two capacity measurements with a 32-token limit while sampling memory: the same prompt, giving `BytesPerReplyToken` from the difference to the benchmark's peak PyTorch allocation, and an ~8k-token prompt (~4k after an out-of-memory failure, recorded as `OutOfMemoryContextTokens`), giving `BytesPerPromptToken`. Dynamic VRAM loading keeps model weights outside PyTorch's allocator, so these isolate KV cache and prefill memory. `PromptCapacity` adds the device memory left at the large run's peak, less a 768 MiB margin and the extra reply reservation, divided by the prompt cost. Text jobs estimate their prompt (four characters per token; Qwen images at one token per 32×32 pixels of their canvas, Gemma 4 at most 280), carry a progress notice when it exceeds the capacity, and fail with `ComfyChatClient.OutOfMemoryMessage` when ComfyUI reports `OutOfMemoryError`. Multi-image canvases use the model's `BatchImageSide` (1,024, 768 or 512 pixels). `ComfyGenerationWatch` compares a ComfyUI text job's token rate with the model's newest standard benchmark: under 35% for a 20-second window, or no token two minutes after `TextGenerate` starts, adds a progress notice suggesting that dynamic VRAM loading is streaming the weights. Capacity runs slower than that fraction count as not fitting (`OutOfMemoryContextTokens`). At capture, `ComfyTextCapacity.FitReplyLimit` raises the captured `MaxOutputTokens` to `ReplyCapacity` for the prompt of the step that uses it (rounded down to 256, at most 8,192), unless the model sets `FixedReplyLimit` or a version-3 profile carries its own limit; it never lowers the configured value. Prompt composition can omit the scene text and neighbouring shots (`PromptCompositionRequest.ReducedScriptContext`).

ComfyUI prompt compositions with reference images run in two steps within one job. `PromptComposer.BuildBriefMessages` attaches the images and RefMod previews with reference data only (no shot text or directions) and asks for a visual brief of at most 600 words per Picture and Video label; its operation (`brief`) uses a 1,024-token reply limit. `AiTextJobRequest.Messages` hold the text-only composition (`BuildMessages(visualBrief: true)`), which states a `VISUAL BRIEF` input mode superseding the guide's inspection wording, and `WithBrief` appends the brief and a final "return only the JSON" line. Without that line, Qwen3.8 27B planned and redrafted the sections in plain text until the reply limit; without the input-mode statement it asked for the missing images. Thinking mode spent over 4,000 tokens reasoning without answering. Briefs are cached in `cache/visual-briefs` by `VisualBriefCache.Key` (profile, server, model, canvas size and the exact brief messages including image bytes); a hit is captured as `VisualBrief` and skips the first step. Recovery never submits new work: a request interrupted before its composition step finishes observing the brief (so it is cached) and asks to generate again. `LiveTwoStepCompositionTests` replays a saved composition job against a real ComfyUI when `LUMIBELLE_LIVE_COMFY_JOB` is set.

After the benchmark, the model test probes capabilities that `TextGenerate` accepts for every model but some tokenizers silently drop. Each probe asks the model to repeat a random code it can only know through the channel under test, at temperature 0.01 with 48 output tokens:

- **System prompt**, when `TextGenerate` advertises the `system_prompt` input ([ComfyUI #16442](https://github.com/Comfy-Org/ComfyUI/pull/16442)): the code is placed only in `system_prompt`. Gemma 4, Qwen3.5 and Qwen3-VL 4B/8B tokenizers honor it; the Krea 2, MiniMax (Qwen3-VL 32B) and other image-encoder tokenizers ignore it.
- **Single image**, when `LoadImage` and the `image` input exist: a rendered dot-matrix number.
- **Multiple images**, after a passed single-image probe and when `ImageBatch` exists: two numbers that must be read back in order, so a tokenizer that consumes only the first batch image fails.

Image probes accept one misread digit per number, since they test whether the image reached the model rather than OCR accuracy (Qwen3.8 27B read a slashed dot-matrix zero as a nine). A blind guess still matches a four-digit number only 0.37% of the time.

A rejected, failed or stalled probe records the capability as unsupported without failing the test. Results are stored with the verification (`Capabilities`) and are therefore bound to the ComfyUI version. Recovered tests record none. A detected image mode replaces the former manual setting, which only applies to models not yet tested with detection.

With a confirmed system prompt, requests put leading system messages (and the image-inspection instructions) in `system_prompt` and send a single user message as the raw `prompt`; later history keeps `[role]` markers. Immediately before submission, `system_stats` must report a ComfyUI version on which the newest test of that model passed. Otherwise the request uses the historical role-marked transcript unchanged. `use_default_template` stays enabled because ComfyUI ignores `system_prompt` without it ([#16625](https://github.com/Comfy-Org/ComfyUI/issues/16625) may change that).

OpenRouter key checks call `GET /api/v1/key` and model discovery calls `GET /api/v1/models`. Requests use OpenRouter's [OpenAI-compatible Chat Completions API](https://openrouter.ai/docs/quickstart).

### ComfyUI images

Reference generation uses a local, flattened version of the [official Krea 2 Turbo workflow](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_krea2_turbo_t2i.json): Krea 2 and Qwen3-VL loaders, the exact entered prompt, zeroed negative conditioning, eight Euler/simple sampling steps, VAE decode, and a temporary preview. Prompt enhancement is disabled so saved metadata matches the actual generation input. Lumibelle copies the completed image into the project; it does not depend on the temporary ComfyUI filename afterward.

Krea editing checks for `ETN_LoadImageBase64`, the Krea 2 Edit nodes, and the current `target_latent` input. The source is decoded, oriented, cropped, reduced to at most 2 MP, converted to PNG in memory, and embedded directly in the ComfyUI workflow through `ETN_LoadImageBase64`; Lumibelle does not upload it to or leave it in ComfyUI's input directory. The default workflow uses the identity-edit LoRA at strength 1, fidelity 4, 768 px grounding, `fit` mode, and ten Euler/simple steps at CFG 1. For two-image edits, the second image is wired into both the pixel/latent and grounded text paths (`source_image_b`, `source_latent_b`, and both encoders' `image_b`); `ref_boost_a` controls the base. The run checks current node capabilities before submission.

The Klein adapter flattens the [official Klein 9B KV editing workflow](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/image_flux2_klein_9b_kv_image_edit.json): Qwen3 8B `flux2` encoding, zeroed negative conditioning, `FluxKVCache`, CFG 1, four Euler steps with `Flux2Scheduler`, and a FLUX.2 latent canvas. Each edit reference is scaled to 1 MP with Lanczos, VAE-encoded, and appended to both conditioning chains in order. Creation omits the reference chains. Lumibelle uses the selected output aspect ratio instead of deriving the canvas from image 1, embeds sanitized PNG inputs instead of server filenames, and downloads a temporary preview instead of using `SaveImage`. Catalog checks inspect the required input contracts and exact installed files.

The selected workflow, files, source order, crop, and prompt are captured for each request; saved takes retain their workflow and ordered source IDs even if a source is later deleted. Saved edit metadata contains ordered source identities, base crop, exact additional-reference crop identities, and the actual Krea fidelity values. Existing images need no migration.

Optional LoRAs use `LoraLoaderModelOnly`: before sampling for Krea creation, after the required identity LoRA and before the reference patch for Krea editing, and before `FluxKVCache` for Klein. Each batch captures the ordered selection once and checks exact files and loader capabilities before submission. Saved image details and Trash retain the applied filenames, names, and strengths. Older records load with no optional LoRAs.

Lumibelle opens a prompt-scoped WebSocket before submission for progress. Model-loading stages show elapsed time because ComfyUI does not publish the console's VRAM details or a loader percentage. Lumibelle continues polling only its submitted job's history as the authoritative completion path, so a missing or dropped WebSocket reduces progress detail without losing the result. Older servers without the targeted cancellation endpoint have the pending job removed when possible, while an active execution may continue.

### Shots

The experimental production reset starts empty setups and take history while retaining scripts, assets, settings, shot IDs and coverage. Previous media files remain on disk. Old `/production` links redirect to Shots. See [Unified Shots implementation and validation](production.md) and [reference reels](h3/reference-reels.md).
