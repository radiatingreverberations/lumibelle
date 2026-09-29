# Assets studio

Assets are the characters, environments and props of your film. Each one keeps visual notes, evidence from the script, suggested reference tags and its own image library.

## Create assets

Create Characters, Environments and Props manually, or choose **Extract from script** to inspect all or selected scenes of the latest saved screenplay with the configured text model. Without saved scenes, extraction links back to the [Script studio](script.md). Extraction shows the approximate context size and returns editable proposals. Each proposal must be explicitly created, merged into an existing asset or skipped; extraction never edits accepted assets automatically.

Evidence is tied to the captured script snapshot and scene IDs.

## Images

Import PNG, JPEG or WebP files up to 25 MB, or generate one to four local Krea 2 or Klein KV candidates. Image runs are sequential and keep candidates completed before a later failure or cancellation. Prompts, model filenames, aspect ratios, seeds, dimensions and generation timestamps remain with each generated take.

Imported and generated images begin as unapproved takes. Select **Use as reference** to make an image available to shot composition. Several images can be approved for one asset, and one can be the cover. Tags such as `face`, `full body`, `wide view` or `outfit: red coat` describe what each reference provides.

### Image prompt enhancement

Write an image prompt or edit instruction, choose an **Enhancement model**, then click **Enhance**. The remembered choice is shared across Create/Edit and Krea/Klein within the project. A review dialog shows the original and an editable suggestion; **Apply** changes only the prompt, and **Undo enhancement** remains available until you type or change its context. Image generation is a separate action.

Edit mode also offers **Inspect reference images**, off by default. With an OpenRouter vision model selected, this sends the actual ordered images with their current crops to that provider. ComfyUI enhancement uses text context only. Missing models, unsupported image input and missing or trashed references produce actionable errors instead of silently substituting inputs. A suggestion cannot overwrite a prompt, workflow, asset or set of references that changed after the request.

## Edit images

Choose **Edit image** on any take to use it as the source for a Krea 2 edit. **Crop source** lets you drag the crop to reposition it and pull its edges to zoom; expandable precise controls provide the same adjustments with sliders. Cropping never changes the stored original. Edit instructions can target a different output aspect ratio. Results are stored beside the source as unapproved takes, and their details retain the sources, crop and edit settings even if a source is later deleted.

### Two-image Krea edits

Choose **Edit image** for **Image 1 · Base**, then add one image from any asset in the same project as **Image 2 · Reference**. Adding the second image enables two-image mode; removing it restores single-image editing.

For a person-into-scene edit, use the scene as the base and the person as the second reference, with an instruction such as “Place this person at the cafe table, holding a coffee.” For clothing transfer, try “Dress the person in the base image in the outfit from the second reference. Preserve their identity and fit the clothing naturally.”

Krea's **Advanced** controls expose **Base fidelity** (default 1) and **Second-reference fidelity** (default 4) for two-image edits. Single-image editing keeps **Reference fidelity** (default 4). All use a 0–10 range and share the grounding resolution. Older ComfyUI installations can keep single-image editing while showing an update instruction for two-image support.

### Multi-reference Klein edits

With **FLUX.2 Klein 9B KV**, choose **Edit image** to make that take **image 1**, then add references from any asset in the same project, up to eight images in total. Refer to them by number in the instruction, such as “Keep the person from image 1, wearing the outfit in image 2.” Klein does not use Krea's LoRA, fidelity or grounding controls. Switching to Krea keeps images and crops; more than two inputs blocks the run until the extra references are removed.

### Crops

Both Krea and Klein support independent, reversible input crops. **View**, **Crop** and **Remove** are available for each additional reference; additional references start with their full frame. Cancelling or clearing a crop affects only that input, never its original file or the output aspect. Switching workflows preserves crops. Workflow switching and reference changes are disabled during generation.

## Review takes

As soon as the first edited take is saved, **Review edited takes** opens with its source, additional references and placeholders for remaining candidates. Takes appear as they are saved without changing your current image or comparison. Live progress and **Cancel remaining** are available inside the review. Closing keeps generation running.

Select a thumbnail row to view one image, or toggle **Compare with** on another row to compare any pair using a slider or side-by-side view. Cropped inputs offer **Full images / Submitted crops**. Details retain the prompt, seed, workflow and provenance. **Review latest edit** reopens the latest batch during the current page visit; after reloading, library previews show each saved image with its recorded inputs.

**One more take** adds a candidate to the latest edit batch, during generation or after it finishes. Extra takes use the captured prompt, workflow, references, crops, LoRAs and ComfyUI settings; later sidebar changes do not affect them. Fixed seeds advance to the next take; blank seeds stay random. Missing or trashed inputs must be restored before adding another take.

Takes save automatically. In batch review, **Discard** moves a saved take to Trash immediately, even while later candidates generate, and **Undo** restores its original place in the batch.

## LoRAs

Expand **LoRAs** to add, enable, reorder or remove registered LoRAs and adjust their individual strengths. Selections autosave separately for each asset and workflow and are shared between Create and Edit. **Insert trigger into prompt** appends the saved trigger text only when clicked. Changing a library default does not change existing asset strengths. Disabled LoRAs and strength zero do not affect generation; enabled missing files block it until refreshed, repaired, disabled or removed.

Register LoRAs and set per-project tag filters as described in [AI setup](ai-setup.md#loras).

## Character reference reels

Character reference reels live in Assets, optionally under a look. Use the right-hand tools column to fill paired generation/use prompts from three presets, start with Custom and Assist, or import a clip. Reuse the saved clip in [Shots](shots.md) with its own guidance and optional soundtrack.

## Trash

Deleting images from the library moves them to **Trash**, with snackbar Undo and no confirmation. Trash covers all projects, supports project filtering and pagination, and keeps images for **30 days**. Restore selected images without confirmation; **Delete permanently** and **Empty Trash** require confirmation. Empty Trash applies across all projects and removes exactly the images shown in its confirmation, not later discards.

Sources and references still in Trash remain viewable and comparable in image review, including submitted crops. Their rows show **In Trash** and **Restore source/reference**. Viewing does not extend retention; images must be restored before they can be used for new generation. Missing files and permanent deletions show unavailable states.

Removed reference reels also go to Trash for 30 days, where they can be previewed, restored or deleted permanently. Deleting a reel permanently removes its video only when no shot, take, other reel or saved image still uses it; those keep working. Reels removed before reels had a Trash period stay until you delete them.

Deleting an entire asset asks for confirmation and sends its images to Trash. Restoring an image recreates the original asset if necessary, with its name, notes, category and evidence. Existing covers take precedence over restored covers.

Expired images are purged at startup and hourly while Lumibelle runs; interrupted cleanup resumes safely after restart.
