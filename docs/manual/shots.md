# Shots

**Shots** combines coverage, references, prompt writing and takes in one workspace. **Shot / Takes** switch the center view. Manual editing works without AI or video models.

## Shot view

Edit the title in the heading, with scene, duration, action/camera, dialogue and an inline take preview in **Shot**. Cast and other supporting details expand below. Browsing the preview does not change the selected production take.

The shot list groups shots by script scene. Click a scene heading to collapse it; collapsed scenes are remembered per project. Drag a shot by its ⠿ handle to reorder it within its scene, or click the handle for **Move to…**. Each shot's **⋯** menu moves it up or down, opens **Move to…**, duplicates it, opens its **Language versions** or deletes it. Shots stay within their scene, and **Undo** reverses a move. Bulk operations are in **Bulk operations**. Duplicating a shot copies coverage only.

**Draft shot**, at the right of the shot heading, drafts the selected shot with AI from its scene in the saved script, for example after **+ Shot** when the breakdown missed a moment. The scene's other shots are sent as context, so the draft covers something they don't unless your directions say otherwise; a shot that already has action or dialogue is sent too, for the directions to revise. Review the proposed title, duration, action, dialogue, cast and sound, then **Apply to this shot** or **Dismiss** it. Applying keeps the shot's references, takes and existing cast, and **Undo** restores the previous version.

The right panel summarizes references and provides **Prompt** and **Generation settings** dialogs. A shot that [continues from a frame](#continue-from-a-frame) shows **Starts from** with that frame above its references. Named generation presets are shared across shots and projects; the prompt and references belong to the shot.

## References

Choose ordered images and crops in **References**, with optional advisory **AI use hints**. **Copy references from** replaces the draft's references with another shot's. Copying from the previous shot also adds the last frame of its production take as a continuity picture, numbered after the images, so wardrobe, props and the set carry across the cut. It is read from the take rather than saved to Assets; **Remove** drops it before you apply. Voice recordings keep excerpt controls and speaker mappings. Character reference reels saved in [Assets](assets.md#character-reference-reels) can be reused here with their own guidance and optional soundtrack.

### LoRAs

Expand **LoRAs** under References to choose H3 LoRAs for this shot. They are saved with the shot, like its references, and apply in every setup on top of the LoRAs in the setup's preset (**Prompt** → **Preset**), which are shared by every shot using that preset. For a LoRA in both, the shot's strength is used; the section lists the preset's other LoRAs so you can see everything a take will apply. Changing a shot's LoRAs shows **Check prompt**, and takes already generated keep the LoRAs they were made with. Register LoRAs in [AI setup](ai-setup.md#loras).

## Prompt

**Compose prompt** examines the actual cropped images and current coverage. A valid initial composition applies automatically if the empty target is unchanged. **Revise with AI** proposes changes for **Apply changes / Discard**.

With a ComfyUI model, composing is one request when the reference images, the long composition guide and the shot fit in GPU memory together, by the capacity the model test measured. The model then composes while seeing the images. Otherwise, and for a model whose capacity hasn't been measured, composing takes two steps. The first step inspects the reference images and writes a short visual brief per Picture and Video; the second composes the prompt from the brief without images. The brief depends only on the references, their crops and guidance, the model and the image size, so revisions and other shots that use the same references reuse it and skip the first step. **Request details** shows the brief the prompt was written from. **Compose prompt** sizes the request against the model's measured capacity before queueing it. A request that is too large is not queued: Prompt assistance shows the size of each step, so you can shorten it and compose again, or choose **Compose anyway**. See [AI setup](ai-setup.md).

The styled prompt editor and **Exact text** view share the same literal text; direct edits autosave. **Accept prompt** records a manual review without rewriting the text. Prompt-template deviations are advisory: **Generate takes** saves and uses the displayed text without requiring a separate acceptance step, while still validating the actual media and generation settings.

Reference or shot changes preserve your prompt and show **Check prompt**. **Clear prompt** keeps references, settings and Direction for AI; Undo restores the draft.

## Takes

[Reviewing generated takes](https://lumibelle.ai/media/manual/shots-take-review.mp4)

The dedicated **Takes** view shows large previews, newest first, with a selected badge and an optional setup filter. Retries and **One more take** reuse their batch's captured inputs.

Click a take to review it. The player steps through single frames with **‹ ›** or the `[` and `]` keys. Below it, **Save frame to Assets** saves the paused frame as an image, and **Continue from this frame** makes a shot start from it. Under the player are the take's settings and any changes since it was made, **Improve quality**, and its details: the file and **Download MP4**, the generation details, timings, LoRAs and the references it was submitted with. The takes of the same batch are listed beside the player, each with **Use this take** and **Discard**.

### Continue from a frame

**Continue from this frame** asks which shot should open on the paused frame. **A new shot after** the take's shot is added in the same scene, with the same cast and sound, called "… (cont.)". **An existing shot** starts there instead, for example when a cutaway sits between the two; it replaces that shot's starting frame. Takes of that shot open exactly on the frame, so they play on from the take. Pause on the last frame to continue where the take ends; when you continue from an earlier frame, trim the first clip in [Cut](cut.md) to end there. The new shot opens with its action and dialogue empty: write them, or use **Draft shot**, which sees the shot before it.

**Starts from**, at the top of the shot's references, shows the frame and its take; **Remove** makes the shot cut in again. **Compose prompt** sees the frame after the references and writes the shot to begin in it and carry on from there, rather than opening differently from the previous shot. The frame needs the same aspect ratio as the shot, and its take can't be discarded while a shot starts from it. A shot that starts from a frame can't also have a first-frame reference. Changing or removing the frame marks the prompt for review and the existing takes as changed.

To assemble takes into a sequence, open the [Cut studio](cut.md).
