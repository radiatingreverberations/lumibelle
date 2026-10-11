# Shots studio

In take review, **Continue** and **Lead into** reuse the saved take's captured references. **Manage references** in the extension panel opens an extension-local draft: Apply changes affects only the new footage, and Reset restores the original captured inputs. The selected pictures, reels and audio are used by both prompt composition and generation. A written prompt is preserved when references change; recompose it or check its reference labels before queueing. Newly created continuation shots inherit the extension's customized references.

The extension compose control shows request progress and changes to **Needs attention** when composition fails. Click it to inspect the saved response and error. **New request** returns to the panel for editing and retrying; failed responses leave the current prompt unchanged. Successful compositions can be inspected through **View response** on the same control.

Open **Shots** from a project after approving a script. **Draft shots** captures the selected approved scenes, directing instructions, maximum duration, and starred text model. Review the proposed shots before adding them. Existing shots are preserved. Each shot describes one continuous camera take; dialogue remains editable separately.

The default breakdown maximum is 15 requested seconds. H3 rounds upward to its 17k+5 frame grid at 24 fps, with a maximum of 362 frames (15.083 seconds). Manual edits can use any requested duration from 1 through 15 seconds, independent of an earlier breakdown limit. The compiler produces a deterministic six-section prompt; generating a take does not call a writing model.

## Setup

Under **AI settings → Video models → MiniMax H3 Ref2VA**, refresh the installed catalog and select exact H3 Ref2VA diffusion, text encoder, video VAE, and audio VAE files. Select separate files for **4-step Turbo LoRA** and **8-step Turbo LoRA**, then save. Each shot's Quality menu offers Standard, 4-step Turbo, and 8-step Turbo. Unavailable Turbo dependencies disable only that mode.

| Quality | LoRA | Sampler / schedule | Steps |
| --- | --- | --- | --- |
| Standard | None | `res_multistep` / `simple` | 20 |
| Turbo · 4 steps | Ref2V 4-step v0.1, strength 1 | `res_multistep` / `simple` | 4 |
| Turbo · 8 steps | `minimax_h3_ref2v_turbo_8step_v1.0_768p_comfyui_bf16.safetensors`, strength 1 | `euler` / `simple` | 8 |

The 8-step mode follows [LightX2V's release settings](https://huggingface.co/lightx2v/Minimax-h3-Turbo/discussions/51). It chains `MiniMaxH3SigmaShift` after the LoRA with video shift 12 and audio shift 3; both the guider and scheduler use that patched model. Preview and native 768p-area presets remain available. Existing Standard/4-step graphs keep the template's default shifts and sampling. New batches record a versioned sampling profile, exact LoRA, strength, sampler, schedule, and shifts. Later edits cannot change remaining candidates or **One more take**. Existing shots and saved takes keep their original quality and fingerprints.

The dropdowns suggest H3 files by filename, including community conditioning encoders and separate 4/8-step Ref2V LoRAs. **Show all installed files (advanced)** exposes each loader's complete catalog for renamed or unrecognized exports. Current selections stay visible when filtering is enabled again and after reopening. This switch changes browsing only; selections still require **Save video models**. Submission checks exact installed filenames and node contracts, not checkpoint contents or compatibility. Known FL2VA diffusion files, FL2V LoRAs, opposite-step Turbo files, and standalone generation tails are rejected. Arbitrary video LoRA stacks are not supported.

FFmpeg and ffprobe must be available on PATH, or configured with their executable paths in Video models. Voice recordings belong to character assets and preserve the uploaded original. Excerpts must be 1–15 seconds within readable WAV, MP3, FLAC, M4A, or Ogg audio, up to 50 MB. Prepared excerpts are metadata-free 32 kHz stereo PCM WAV. Image references use independent orientation/crop/preparation.

## Takes and continuity

Shots and reels share **Quick**, **Preview**, **Detail**, and **Native** generation resolutions, with exact dimensions shown for the selected aspect. Choose Quick or Preview and 2–4 takes to explore candidates, then use **Regenerate…** on a preferred take to generate a new version at Detail or Native. The separate Upscaled preview option remains available when its optional setup is ready.

Take cards and review display actual output dimensions, MP, duration, seed, generation preset and setup. **Regenerate…** initially selects Native and **Keep source seed**; **New random seed** explores another candidate, while **Custom seed** accepts an exact nonnegative 64-bit seed. Reusing a seed can retain motion, but changing resolution can also change motion and detail.

Regeneration reuses the saved prompt, duration, model, sampling, LoRAs, output policy and prepared reference bytes. **Save lossless frames** starts from the source take's choice and can be turned on or off for the new take; requests captured before output policies always keep them. It makes a new take without editing the current setup or replacing the original. Reference-library edits do not affect captured inputs. Failed output transfers and retries reuse the captured request rather than generating again. Takes without their saved request cannot regenerate this way; refined outputs point authors back to the original take. The optional companion-based **Improve quality** workflow remains separate.

A single application-owned video batch runs at a time and continues through navigation and browser disconnects. Review opens after the first safely stored take. **One more take** uses the original captured shot and references with a new seed. Completed candidates remain completed when discarded; empty result slots are not recreated.

Each take stores an MP4 with audio. When Save lossless frames is enabled, it also stores a silent, full-resolution lossless animated WebP archive branched from decoded H3 images before MP4 compression. ComfyUI's native `RebatchImages` groups up to 24 frames per file, and `SaveAnimatedWEBP` saves them with `lossless=true`, 24 fps, quality 80, and the default compression method. There is no PNG-sequence fallback. This preserves exported 8-bit RGB pixels, not floating-point tensors or latents.

Small archive segments bound decoding memory and transfer retries. Ancillary metadata is stripped without re-encoding the compressed frames. WebP's merged identical frames are mapped back to the original frame numbers; timestamps use the source's exact 24 fps, rather than the animation container's rounded millisecond delays. Fully static segments can be stored by the native encoder as one still WebP with repeated frame bindings.

In review, choose an exact archived frame by number or timestamp and **Save to Assets**. It appears in **Assets → Continuity frames**, where authors can preview it, edit its name and preservation notes, search the collection, or move it to Trash with Undo. The save confirmation links directly to its Assets preview. Only the chosen frame is exported to an independent, metadata-free PNG, so purging its source take does not remove it. Assign it explicitly from **Add reference → Continuity frames** on another shot; this is reference conditioning, not first-frame anchoring or automatic continuation. Assets and Shots use the same project frame records and media IDs; no copies or storage migration are involved. Editing frame notes never rewrites captured take prompts or provenance.

Submitted image and voice references remain inspectable from take details. Retained Trash sources can be restored in place. New submissions require active inputs.

## Storage and recovery

Composition validation names missing or repeated H3 sections instead of reporting only a generic six-section error. Assist is instructed to include both sound sections even when there is no dialogue or music. Duration checking accepts equivalent digits and simple English number words (for example, `8 seconds` and `eight-second`). An ordinary shot may state the authored Requested seconds or any value up to the generated frame-derived duration, because H3 takes its output length from the frame count and treats the prompt wording as pacing guidance; a value outside that range is still rejected, and a stated value other than the exact frame-derived figure is reported in the prompt review notes. Reel prompts must state the generated duration exactly, since their cut timestamps are derived from the same frame grid. Dialogue checking treats printer's punctuation as equivalent to its plain form, so a response that only renders the authored line with typographic apostrophes, quotes, dashes or ellipses stays applicable; the words, their order and the language remain strict, and neither the prompt nor the source dialogue is rewritten. Duration and continuous-take failures have separate messages.

If a completed AI response omitted only its final sound sections, **Recover response as draft** shows the captured shot's atmosphere and music directions before adding them. Recovery preserves the returned visual prompt and reference-use guidance, makes no new AI request, and leaves the result editable and unaccepted. Review it before generating; the normal page **Undo** restores the previous draft.

If an earlier response was rejected by a validation bug and now passes all checks, the same **Recover response as draft** action restores it unchanged. It does not regenerate or rewrite the returned prompt or reference-use guidance.

In **Review prompt changes**, an empty Current prompt is a valid starting point. **Apply changes** installs the suggestion and closes the review. A harmless extra save, setup rename, seed change, or take-count change does not invalidate explicit Apply when the captured prompt and composition inputs still match. Automatic application retains its stricter revision check. An actual prompt/input conflict preserves the draft and suggestion, with an error that stays visible through background refreshes.

Recovery is unavailable for interrupted responses, missing sound directions, or unresolved prompt validation failures. Saving also checks that the prompt, shot settings, and references still match the captured request; newer edits cannot be overwritten. The original AI response remains available in request history.

- `shots.json`: editable shots, selected takes, frame records, recovery versions, and production-media Trash.
- `shots/runs/{run-id}/run.json`: captured settings, prompt, reference bindings, candidate states, ComfyUI prompt IDs, and output receipts.
- `shots/runs/{run-id}/inputs`: prepared inputs shared by the captured batch.
- `shots/runs/{run-id}/candidate-N`: recoverable output transfer staging.
- `shots/takes/{take-id}`: published `video.mp4` and ordered `archive-0000.webp` onward, with frame-to-file/index mappings in the take manifest.
- `shots/frames`: independent continuity references.
- Existing asset manifests retain voice records and voice Trash; audio originals stay beside their character asset.

A take publishes only after its archive is complete. Transfer retry reuses recorded output receipts; it does not regenerate. Accepted jobs reconcile against ComfyUI queue/history after restart. An unknown submission outcome pauses for manual inspection instead of automatically submitting a duplicate. Check the ComfyUI queue before explicitly requesting another take in that case.

Takes, voices, and saved frames use global Trash with 30-day retention and startup/hourly cleanup while Lumibelle runs. A take and all its frames form one Trash item. A selected production take must be deselected before discard or shot deletion. Undo/recovery of shot text does not resurrect discarded media.

## Validation

Run `dotnet test tests/Lumibelle.Tests -c Release` and `npx playwright test`. BrowserHost uses an isolated temporary library and mocked text/video/image providers. Its video mock uses local FFmpeg to create playable test media. No automated test submits a live generation job.

Pinned upstream guidance and the author-supplied community workflows are in [h3](h3/README.md). Runtime does not read Downloads or fetch documentation.

Archive behavior follows ComfyUI's [native image saver](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_images.py) and [WebP save helper](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_api/latest/_ui.py), with metadata handling based on the [WebP container specification](https://developers.google.com/speed/webp/docs/riff_container). `comfy-lossless-0.webp` and `comfy-lossless-1.webp` are tiny fixtures produced by those native nodes from 12 red and 27 blue 32×32 synthetic frames; they exercise merged frames and an entirely static segment without model generation.
