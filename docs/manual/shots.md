# Shots

**Shots** combines coverage, references, prompt writing and takes in one workspace. **Shot / Takes** switch the center view. Manual editing works without AI or video models.

## Shot view

Edit the title in the heading, with scene, duration, action/camera, dialogue and an inline take preview in **Shot**. Cast and other supporting details expand below. Browsing the preview does not change the selected production take.

The shot list groups shots by script scene. Click a scene heading to collapse it; collapsed scenes are remembered per project. Drag a shot by its ⠿ handle to reorder it within its scene, or click the handle for **Move to…**. Each shot's **⋯** menu moves it up or down, opens **Move to…**, duplicates it, opens its **Language versions** or deletes it. Shots stay within their scene, and **Undo** reverses a move. Bulk operations are in **Bulk operations**. Duplicating a shot copies coverage only.

The right panel summarizes references and provides **Prompt** and **Generation settings** dialogs. Named generation presets are shared across shots and projects; the prompt and references belong to the shot.

## References

Choose ordered images and crops in **References**, with optional advisory **AI use hints**. Voice recordings keep excerpt controls and speaker mappings. Character reference reels saved in [Assets](assets.md#character-reference-reels) can be reused here with their own guidance and optional soundtrack.

### LoRAs

Expand **LoRAs** under References to choose H3 LoRAs for this shot. They are saved with the shot, like its references, and apply in every setup on top of the LoRAs in the setup's preset (**Prompt** → **Preset**), which are shared by every shot using that preset. For a LoRA in both, the shot's strength is used; the section lists the preset's other LoRAs so you can see everything a take will apply. Changing a shot's LoRAs shows **Check prompt**, and takes already generated keep the LoRAs they were made with. Register LoRAs in [AI setup](ai-setup.md#loras).

## Prompt

**Compose prompt** examines the actual cropped images and current coverage. A valid initial composition applies automatically if the empty target is unchanged. **Revise with AI** proposes changes for **Apply changes / Discard**.

With a ComfyUI model, composing takes two steps so that the images and the long composition guide never have to fit in GPU memory together. The first step inspects the reference images and writes a short visual brief per Picture and Video; the second composes the prompt from the brief without images. The brief depends only on the references, their crops and guidance, the model and the image size, so revisions and other shots that use the same references reuse it and skip the first step. **Request details** shows the brief the prompt was written from. **Estimate prompt size** in Prompt assistance reports both steps against the model's measured capacity; see [AI setup](ai-setup.md).

The styled prompt editor and **Exact text** view share the same literal text; direct edits autosave. **Accept prompt** records a manual review without rewriting the text. Prompt-template deviations are advisory: **Generate takes** saves and uses the displayed text without requiring a separate acceptance step, while still validating the actual media and generation settings.

Reference or shot changes preserve your prompt and show **Check prompt**. **Clear prompt** keeps references, settings and Direction for AI; Undo restores the draft.

## Takes

[Reviewing generated takes](https://lumibelle.ai/media/manual/shots-take-review.mp4)

The dedicated **Takes** view shows large previews, newest first, with a selected badge and an optional setup filter. Retries and **One more take** reuse their batch's captured inputs.

To assemble takes into a sequence, open the [Cut studio](cut.md).
