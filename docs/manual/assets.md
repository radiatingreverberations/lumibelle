# Assets studio

Assets are the characters, environments and props of your film. Each one keeps visual notes, evidence from the script, suggested reference tags and its own image library.

## Create assets

[Creating a prop and generating its first reference images](https://lumibelle.ai/media/manual/assets-create-prop.mp4)

Create Characters, Environments and Props manually, or choose **Extract from script** to inspect all or selected scenes of the latest saved screenplay with the configured text model. Without saved scenes, extraction links back to the [Script studio](script.md). Extraction shows the approximate context size and returns editable proposals. Each proposal must be explicitly created, merged into an existing asset or skipped; extraction never edits accepted assets automatically.

Evidence is tied to the captured script snapshot and scene IDs.

Scene selection, extraction progress and review stay in one dialog, with **Choose scenes → Find assets → Review & add** showing the current step. Choose the text model in the footer before starting. After submission, the selected scenes and captured request remain available under **Request details**. Closing the window keeps extraction running; reopen it from the library button or AI activity.

As extraction streams, readable asset cards show the suggestions arriving. Each card fills the available width, with descriptions and tags alongside looks, consistency notes and script evidence on wider screens. Narrow screens stack those details. The raw response remains available separately. These are previews; validation and your review still precede library changes.

If an extraction needs correction, the review lists the affected fields and offers **Selected source scenes** with the captured scene IDs. **Recheck saved response** runs local validation again without another model call. **Edit JSON** uses the same response editor as Script; **Review corrected JSON** validates and saves a separate correction, preserving the original response and request. It then opens the usual add/update/skip review. Unknown scene references and invalid asset or look matches must be corrected before applying. **New extraction** starts a new model request.

## Images

Import PNG, JPEG or WebP files up to 25 MB, or generate one to four local image candidates with the selected workflow, including MOSS-Image Base/SFT. Image runs are sequential and keep candidates completed before a later failure or cancellation. Prompts, model filenames, aspect ratios, seeds, dimensions and generation timestamps remain with each generated take. MOSS creation and LoRA-based editing use their separate sampling defaults in AI settings; its edit adapter supports up to four ordered references. See [MOSS setup](ai-setup.md#moss-image).

Imported and generated images begin as unapproved takes. Select **Use as reference** to make an image available to shot composition. Several images can be approved for one asset, and one can be the cover. Tags such as `face`, `full body`, `wide view` or `outfit: red coat` describe what each reference provides.

### Visual style

Choose a visual style when creating a project, or set a shared treatment near the top of **Project settings → Visual style**. Choose **Photographic**, **Anime**, **Illustration**, **3D** or **Custom**, then add direction for the palette, lighting, textures or rendering. **Not specified** leaves prompts unchanged.

New asset images, image edits, reference reels and prompt enhancement inherit this direction. Set an **Asset visual style** in asset details for exceptions, or use **Run visual style** for one composer and **Batch visual style** for a bulk plan. Run or batch overrides take priority over the asset override, then the project default. Choose **Inherit** to follow the parent setting; choose **Not specified** to turn style direction off for that override.

Queued work keeps its captured style. Changing the project default affects new requests; saved images, existing reels, original author prompts and identity notes stay intact. Image details and batch results show the captured style. AI evaluation checks the captured direction under prompt adherence and can flag mismatches, but approval remains yours. Reviewing an existing reel uses that reel's recorded style rather than the current project default.

### Bulk assets

Choose **Bulk assets** in the asset library, then select **Images**, **Character reels**, or **Environment reels**. Image batches prepare references for several assets and character looks. **Select missing references** selects items without an approved image in that exact look. Character looks need an approved identity image first. Archived looks are excluded.

The button uses the same AI action style as extraction. It shows progress while a batch runs, **Paused** while it is held, and highlights **Review bulk assets · N** when saved items still need your decision. The count is asset/look groups, not individual candidates. Decide what to do with each image candidate, or save reel keyframes, to clear the reminder. Problems show an attention notice; reopening the button prioritizes active work and batches awaiting review.

The same dialog keeps **Choose assets → Review plan → Generate & review** together. Choose an image workflow, output size and one to four candidates per item, then review and edit each prompt before starting. The plan shows the approved input references and the asset's enabled LoRAs. Items with references use editing; other items create images from their direction. The batch captures these choices, so later settings changes do not alter its requests.

Image plans include presentation defaults by asset type. **Characters** start with one clearly visible character and look, a neutral studio-style backdrop, soft even lighting and an unobstructed face and clothing. **Props** start with the complete object in frame, a simple neutral background and clear materials and silhouette. **Environments** start with an establishing view that makes the setting's layout and fixed features readable. These defaults keep the project/asset's rendering style: an illustrated project stays illustrated. Explicit asset/look direction and batch instructions take priority, including requested poses, backgrounds, lighting and framing. The defaults are part of each editable generation prompt, so AI enhancement can adapt them to the chosen model and they also work when enhancement is off. Other reference assets retain their authored purpose and framing.

Use the asset-type chips to show **Characters**, **Environments**, **Props**, or **References**, then choose **Select all ready items** or **Select missing references**. Only selected items in the shown type enter the batch; selection shortcuts and **Clear** apply to that type. **All** includes all types. Each batch has one aspect ratio, so you can generate characters in **9:16** and run a separate environment batch in **16:9**.

Expand **Additional instructions (optional)** for direction shared by this image batch, such as “full-body characters, entire figure visible.” The instructions supplement the individual asset prompts and visual style. **Review plan** shows the aspect and instructions before starting. Generation, retries and optional AI review retain the same captured direction; asset definitions and prompts in other batches are unchanged.

Enable **Enhance prompts with AI** to rewrite each selected item's prompt automatically before generating its candidates. Choose a text model; it uses the same image-model-specific create or edit guide as single-image **Enhance**, together with the captured asset/look, reference notes, visual style, aspect ratio, LoRAs and batch instructions. This stage uses text context and does not require a vision model. Start the batch once; prompt enhancement and image generation then proceed item by item in the background. **Captured direction** shows the prompt used for generation, **Original prompt & instructions** preserves your input, and **Saved prompt response** opens the original AI response. Asset definitions and the single-image composer are unchanged.

Optional **Evaluate candidates with a vision model** sends the captured direction and a contact sheet to a model with verified image input. It ranks candidates and reports prompt adherence, reference consistency, framing and visible defects as **Pass**, **Concerns** or **Cannot judge**. Enhancement and evaluation are independent options. These are recommendations. Missing references and details that cannot be seen remain uncertain; a model recommendation never approves an image.

Candidates save immediately in **Pending review**, separate from the normal asset gallery, and appear as they arrive. **Use as reference** keeps and approves a candidate; **Keep as take** keeps an alternative without approving it. Each candidate needs its own decision, so keeping one does not silently keep its alternatives. Open **Compare & details** to inspect candidates, or choose **Pending review** in the asset gallery's **Show** filter. Existing reference selections and covers are preserved. The captured prompt and original AI review response remain available; an AI recommendation does not accept an image.

Choose **Discard** to move an undecided candidate to recoverable Trash. Once the batch finishes, **Discard all remaining candidates (N)** moves exactly those undecided images to Trash in one action, across all asset/look groups. It preserves kept takes, approved references and the asset definitions. **Undo discard** restores the discarded candidates to Pending review; the same images remain recoverable through Trash for 30 days. **All candidates** shows previous keep/discard decisions alongside the AI findings. Review reminders clear when no active candidates await a decision.

Closing the dialog or leaving Assets keeps the batch running. Reopen **Bulk assets** to follow progress, or choose an earlier run under **Saved batches**. **Pause after current request** lets the active request finish and holds later work; **Resume batch** continues from its saved checkpoint. **Stop batch** cancels the active request and leaves completed candidates available. Restarting Lumibelle retains the batch checkpoint and uses the ordinary AI queue recovery rules.

A failed item or evaluation keeps its saved images and response available and lets later items continue. Failed prompt enhancement marks that item **Needs attention** without generating from an unenhanced fallback. **Retry prompt enhancement** allows one additional attempt; a successfully saved enhancement is reused for image retries and after restart. Retry a recoverable image request to retrieve its outputs; otherwise a retry may perform generation or evaluation again. Each item allows one additional generation attempt and one additional evaluation attempt. Changed author notes, missing inputs or archived targets need a refreshed plan rather than silent replacement of the captured direction.

#### Bulk reference reels

For **Character reels**, choose the character and looks, a visual coverage purpose, and an optional saved generation preset. For **Environment reels**, choose the locations and **Full set**, **Feature study**, **Walkthrough**, or **Custom**. In **Review plan**, edit each duration, aspect, coverage/route instructions and generation prompt. Use **Update prompt from recipe** after changing the recipe. Feature studies, walkthroughs and custom routes require instructions. Both types generate silent visual reels from approved images, with one candidate per new recipe.

**Select missing coverage** matches the exact asset/look and coverage purpose. A matching reel with saved keyframes counts as covered. When matching footage already exists without saved keyframes, the plan offers to review that reel without generating again. Uncheck **Review the saved coverage reel instead of generating again** to create new footage. Existing recipes and earlier reels remain available.

Optional vision evaluation uses a contact sheet of sampled frames and original reference pictures. It checks visible appearance, coverage, consistency and clarity, reports uncertainty, and suggests named keyframes. It cannot establish what happens between the sampled frames. Play the video to assess motion and transitions. Choose the suggestions you want and click **Save selected keyframes**, or open **Review video & keyframes** to use the full timeline editor. Keyframes save only through your explicit action; these batches do not assign references to shots.

Reel batches use the same saved checkpoints, pause/resume, stop and bounded retry controls as image batches. A failed evaluation leaves the saved video available for manual keyframe review and lets later items continue. The captured direction and raw review response remain available.

### Create images for a character look

With **Group by look** enabled, each character look has one section containing its description, consistency notes, actions and references. **General** references come first, and active looks with no images remain visible. Expand **Consistency & script evidence** for the saved guidance and source excerpts. **Manage look** opens the existing look controls in asset details. In the flat-gallery view, the look overview keeps those notes and actions accessible above the images. Search and media filters narrow the gallery; empty looks appear in unsearched All or Images views.

Choose **Create images for this look** to prepare a draft. Lumibelle includes an approved identity reference when one is available, preferring your preferred identity references, then general references, then other active approved images. Archived looks, pending candidates and continuity frames are excluded. Without an approved reference, the draft starts from character and look notes.

The selected look's description stays beside the prompt, and generated images are assigned to that look. You can edit the prompt and inputs before choosing **Generate images**; opening a look draft does not start generation. Text drafts are remembered separately for each look during this visit.

### Image prompt enhancement

Write an image prompt or edit instruction, choose an **Enhancement model**, then click **Enhance**. The remembered choice is shared across Create/Edit and Krea/Klein within the project. A review dialog shows the original and an editable suggestion; **Apply** changes only the prompt, and **Undo enhancement** remains available until you type or change its context. Image generation is a separate action.

**Direction for AI** is optional guidance for this request only, such as what to improve or keep; the review shows the direction it was given. Each row shows roughly how many tokens it adds: the prompt with the workflow's guide and notes, and, in Edit mode, the reference images when they are inspected.

Edit mode also offers **Inspect reference images**, off by default. With a supported vision model selected, this sends the actual ordered images with their current crops to that provider. OpenAI-compatible connections can verify image support through **Test model** in AI settings. Missing models, unsupported image input and missing or trashed references produce actionable errors instead of silently substituting inputs. A suggestion cannot overwrite a prompt, workflow, asset or set of references that changed after the request.

## Edit images

[Editing an image: remove the chair, then compare the result with the source](https://lumibelle.ai/media/manual/assets-edit-image.mp4)

Choose **Edit image** on any take to use it as the source for a Krea 2 edit. **Crop source** lets you drag the crop to reposition it and pull its edges to zoom; expandable precise controls provide the same adjustments with sliders. **Crop shape** keeps a fixed shape (the image's own, or 1:1, 4:3, 3:4, 16:9 or 9:16), or choose **Free** to drag each edge and corner on its own and type the crop in pixels. Changing shape keeps the crop where it was, and a crop reopens in the shape it was made with. Cropping never changes the stored original. Edit instructions can target a different output aspect ratio. Results are stored beside the source as unapproved takes, and their details retain the sources, crop and edit settings even if a source is later deleted.

### Two-image Krea edits

Choose **Edit image** for **Image 1 · Base**, then add one image from any asset in the same project as **Image 2 · Reference**. Adding the second image enables two-image mode; removing it restores single-image editing.

For a person-into-scene edit, use the scene as the base and the person as the second reference, with an instruction such as “Place this person at the cafe table, holding a coffee.” For clothing transfer, try “Dress the person in the base image in the outfit from the second reference. Preserve their identity and fit the clothing naturally.”

Krea's **Advanced** controls expose **Base fidelity** (default 1) and **Second-reference fidelity** (default 4) for two-image edits. Single-image editing keeps **Reference fidelity** (default 4). All use a 0–10 range and share the grounding resolution. Older ComfyUI installations can keep single-image editing while showing an update instruction for two-image support.

### Multi-reference Klein edits

With **FLUX.2 Klein 9B KV**, choose **Edit image** to make that take **image 1**, then add references from any asset in the same project, up to eight images in total. Refer to them by number in the instruction, such as “Keep the person from image 1, wearing the outfit in image 2.” Klein does not use Krea's LoRA, fidelity or grounding controls. Switching to Krea keeps images and crops; more than two inputs blocks the run until the extra references are removed.

### FLUX 3 edits

**FLUX 3 Image Klein** supports up to ten ordered references without an edit LoRA. Use **Image 1** as the base and add the other references in the Inputs tab. For a focused edit, describe the change and what to preserve; for a new composition, describe the intended result and framing. You can also use **Enhance**, review its suggestion and apply it. Image 1 corresponds to the model's `<ref_image_0>` marker. Crops, review, recovery and optional workflow-assigned LoRAs work through the existing image tools.

The default Distilled checkpoint uses 16 steps; Base uses 50 steps and CFG 3.5. Configure the matching files and variant in [AI setup](ai-setup.md#flux-3-image-klein). FLUX 3 also offers **4K** output as a roughly 16-megapixel canvas with the selected aspect ratio. This increases memory use; reference size is configured separately and does not set the output dimensions.

### Fibo, Qwen Turbo and Z-Image

**Fibo 2.0 Dev** creates and edits images. **Edit Turbo** requires at least one reference and defaults to four steps. Add up to four ordered references through Inputs. **Enhance** proposes an editable VGL JSON caption; review it before applying. For edits it includes the requested change as `edit_instruction`. Input crops, regional protection, candidate review and output recovery use the existing tools.

**Qwen Image 2.1 Turbo** is selected in Qwen's [AI settings](ai-setup.md#qwen-image-21-turbo). It retains Qwen's output sizing, transparency and ten-reference editing, using its fixed eight-step schedule. Base retains adjustable steps.

**Z-Image Base and Turbo** support creation here, with separate sampling and negative-caption controls in [AI settings](ai-setup.md#z-image). Reference editing and ControlNet are not enabled in this workflow. Switch to an editing workflow to use existing pictures as references.

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

Expand **LoRAs** under a reel's references to choose H3 LoRAs for that reel alone. They apply on top of the LoRAs in the reel's preset; for a LoRA in both, the reel's strength is used.

## Environment reference reels

An **Environment** asset is a library of possible backgrounds: stills and saved keyframes across all its reels. Prepare a reusable inspection set in **Scenes → Choose environment views**, manually or with AI, and save authored staging constraints with **Save scene setup**. Scene drafting uses these pictures to understand the set; they are not attached to generated shots automatically.

In **Shots**, use **Edit references** to add individual environment pictures, or **Copy…** to reuse another shot's references. The editor shows scene inspection views first and offers the rest of the environment library. Compose uses the attached pictures and applicable reviewed direction; there is no separate per-shot environment AI selection step. Apply references before composing, and finish character, prop and continuity references in the same editor.

Selected reel pictures consume one picture slot per keyframe and no reel slots. Their source reels, timestamps, names, notes and crops stay attached as provenance, so they can be reviewed or removed using **Edit references**. Removing a source there removes its attached pictures from this setup, not from Assets. The limits remain nine pictures and three full-video/RefMod inputs. **RefMod** is an explicit experimental alternative requiring at least two selected views from each reel. Reels without saved keyframes are marked **Needs keyframes**, with a link to their keyframe manager. Compose does not watch entire videos or extract missing angles. The text-only asset picker remains a separate tool.

Use **Copy** in the shot's **References** action row to reuse another shot's references and environment library. The dialog defaults to the previous shot and previews the replacement before saving. Review or change **Environment library** here; **None selected** means the copied pictures remain usable, but no library is linked for choosing other views. Copy reuses the current views: afterwards, use **Edit references** to choose viewpoints for the new shot. The library follows the shot's references across generation presets. When a selected production take is available, optionally include its last frame as a continuity picture; for a later source shot the default is its first frame. You can change first/last or omit the frame. Copying replaces this shot's references, preserves its action, dialogue and prompt text, and marks the prompt for review. Cancel leaves everything unchanged; Undo restores the previous references.

Start with a location image and generate its missing reference angles in one coherent reel. In image review, choose **Create reel from this image** to open a fresh recipe with that image selected; existing recipes are retained. Choose **Full set**, **Feature study**, **Walkthrough**, or **Custom** under **Coverage and route** in the reel’s Prompt dialog. Full set is the default: a wide perimeter circuit looking inward across the acting area, with held shooting positions on all four sides.

See the [illustrated environment reel guide](environment-reels.md) for camera diagrams, reference setup, and selecting useful keyframes. The same guide is available from **Environment reel guide** in the editor.

## Trash

Deleting images from the library moves them to **Trash**, with snackbar Undo and no confirmation. Trash covers all projects, supports project filtering and pagination, and keeps images for **30 days**. Choose a project, then **Select all in this project** to select its discarded media across every page and **Restore selected** to recover them in one action. With **All projects** selected, **Select all projects** includes every project. **Clear selection** or changing the project filter clears the selection; newly discarded media is not automatically selected on refresh. **Delete permanently** and **Empty Trash** require confirmation. Empty Trash applies to the current project filter and removes exactly the media shown in its confirmation, not later discards.

Sources and references still in Trash remain viewable and comparable in image review, including submitted crops. Their rows show **In Trash** and **Restore source/reference**. Viewing does not extend retention; images must be restored before they can be used for new generation. Missing files and permanent deletions show unavailable states.

Removed reference reels also go to Trash for 30 days, where they can be previewed, restored or deleted permanently. Deleting a reel permanently removes its video only when no shot, take, other reel or saved image still uses it; those keep working. Reels removed before reels had a Trash period stay until you delete them.

Deleting an entire asset asks for confirmation and sends its images to Trash. Restoring an image recreates the original asset if necessary, with its name, notes, category and evidence. Restores preserve the recorded asset and image order, including when you restore in separate selections. Later moves of surviving assets are retained. Existing covers take precedence over restored covers.

Expired images are purged at startup and hourly while Lumibelle runs; interrupted cleanup resumes safely after restart.
