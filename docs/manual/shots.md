# Shots

**Shots** combines coverage, references, prompt writing and takes in one workspace. **Shot / Takes** switch the center view. Manual editing works without AI or video models.

Each shot keeps the script lines it was made from. If those lines change in a later saved script, the shot says so above its preview: **Compare script lines** shows them then and now, and **Mark checked** takes the current lines once the action and dialogue fit. Saving changes elsewhere in the script does not affect the shot.

## Shot view

Edit the title in the heading, with scene, duration, action/camera, dialogue and an inline take preview in **Shot**. Cast and other supporting details expand below. Browsing the preview does not change the selected production take.

The shot list groups shots by script scene. Each thumbnail shows its selected take (or latest) with its size, such as **0.7 MP**; Quick and Preview sizes are marked in amber, so shots still waiting for a Detail or Native regenerate stand out. A shot whose takes are being generated shows **Generating** (or **Queued**) beside its take count. Click a scene heading to collapse it; collapsed scenes are remembered per project. Drag a shot by its ⠿ handle to reorder it within its scene, or click the handle for **Move to…**. Each shot's **⋯** menu moves it up or down, opens **Move to…**, duplicates it, opens its **Language versions** or deletes it. Shots stay within their scene, and **Undo** reverses a move. Bulk operations are in **Bulk operations**. Duplicating a shot copies coverage only.

**Draft shot**, at the right of the shot heading, drafts the selected shot with AI from its scene in the saved script, for example after **+ Shot** when the breakdown missed a moment. The scene's other shots are sent as context, so the draft covers something they don't unless your directions say otherwise; a shot that already has action or dialogue is sent too, for the directions to revise. Review the proposed title, duration, action, dialogue, cast and sound, then **Apply to this shot** or **Dismiss** it. Applying keeps the shot's references, takes and existing cast, and **Undo** restores the previous version.

The right panel lists the references first. Its footer then has the output settings, **Prompt** with the prompt's status (highlighted while a missing or unreviewable prompt is what keeps the shot from generating), and **Generate takes**; the footer also contains the preset dropdown, and the pencil beside it opens **Generation settings** to edit the one in use. A shot that [continues from a frame](#continue-from-a-frame) shows **Starts from** with that frame above its references. Named generation presets are shared across shots and projects; the prompt and references belong to the shot.

**Draft shots** and **Draft shot** use a constrained structured response on native vLLM and compatible connections whose selected model advertises or passes a test for JSON-schema output. Required shot and direction fields, duration bounds, saved scene/source identifiers and inspected-view citation choices are captured with the request. The instructions focus on directing choices and continuity. Coverage, staging and dramatic quality still need review; older captured requests and connections without confirmed schema support retain their original response format.

The drafter budgets real screen time in **Timing** for action, speech and reactions. A source paragraph can span several clips: longer processes are divided into playable intervals while preserving authored waits and the physical state between shots. The maximum duration is a ceiling. A single-shot request surfaces unresolved timing conflicts for review. These instructions also apply to Codex and Claude Code's plain-JSON responses; a schema constrains structure, not physical plausibility.

## Reviewable direction

### Inspect an environment before drafting

Open **Scenes** in the project navigation to prepare a script scene, including scenes without shots. Scenes follow approved-script order and remember your selection. Each page shows the script excerpt, environment setup, accepted direction history and its shots. **Edit in Script** changes the screenplay; shot links open production in **Shots**.

Use **Choose environment views** to select an Environment asset and inspect its stills and saved keyframes across all reels. Add **Staging constraints** for authored facts the empty set cannot establish. AI can suggest complementary inspection views, or you can choose them manually. **Save scene setup** saves this reusable selection; **Back** cancels changes. Unkeyframed reels link to Assets. Full videos are not inspected and no new frames are extracted.

**Draft shots** uses the selected scene's saved setup without repeating view selection. Review its strategy, assessment and shot proposals before adding them. These inspection views explain the set; they are not a storyboard or generation references. New shots inherit the environment association with their generation references empty. In **Shots**, use **Copy…** or **Edit references** to attach only the pictures needed for that shot. In the reference editor, **Environment** shows scene inspection views first and can browse the environment's other stills and saved reel views. Compact cards keep view notes under **View notes**. Each selected frame uses one picture slot and no reel slot. Nothing is attached automatically.

Image capacity is separate from the nine-picture generation budget. The complete drafting request must fit the selected scene, its inspection pictures, screenplay, constraints and the structured reply. If it does not fit, reduce inspection views or choose another model. Drafting remains one request; pictures are never silently dropped. Large picker libraries use bounded inspection and comparison rounds, recording inspected, unavailable and uninspected sources. A stopped scan remains incomplete; manual selection is still available.

**Draft shot** resolves that shot setup's environment override before gathering pictures, guidance and constraints. An override does not borrow another set's views. Use **Scene setup** to revisit inspection views and constraints. An override without its own inspection selection continues explicitly without pictures; it never borrows another environment's views. Existing generation references remain untouched by a redraft.

Review the **Environment assessment** once beside the scene strategy. It separates authored constraints, reference-supported claims, assumptions, conflicts and **Additional environment view needed**. Expand **Evidence and assumptions**, then a claim, to correct its text, downgrade its support, choose different inspected evidence or remove it. Claims can cite only pictures actually attached to that request; Supported requires at least one. Pending edits survive closing and reopening review; **Response details** keeps the original model output separately. An unidentified or obscured feature is not evidence of its absence. An unseen angle may be proposed, but is not observed geometry. Important reactions and exact story events must survive inconvenient set scale or obstructions; the drafter should not invent a platform to fix its preferred framing.

Applying stores the accepted assessment and exact evidence snapshot separately from the editable setup used by future requests. Later setup changes flag dependent staging, continuity and reference support while preserving intent and the earlier text. Before Compose, inspected sources and environment guidance are checked against Assets. Changed or unavailable evidence excludes the earlier staging and assessment from current context with a review limitation; adding unrelated views does not invalidate it. Changing review messages or applying a selection does not invalidate it. Undo, recovery and copying preserve these associations; project copies remap live resources and retain original historical evidence identities with an explicit resource mapping. Request inspection retains the actual cropped picture bytes.

**Draft shots** writes a concise scene strategy alongside its coverage in the same request. Adding reviewed shots inserts each scene's coverage in saved script order, so redrafting an earlier scene places it before later scenes already in the list. Additional coverage for an existing scene follows its current shots; existing shots keep their relative order. **Direction style** defaults to **Auto — adapt to the scene**; the last submitted style is remembered per project. Auto can blend approaches across dramatic beats, rather than classify a genre. Its chosen approach and short explanation appear in **Scene strategy**. Your instructions override every style preference. Optional television examples illustrate an approach, not a template to imitate.

| Approach | Preference |
| --- | --- |
| Auto | Adapt framing, staging and cuts to each beat and explain the choice. Recommended for most scenes. |
| Ensemble / Classical | Keep relationships visible in shared compositions with clear geography and restrained movement. |
| Performance Comedy | Protect setups, reactions, physical jokes and awkward pauses. |
| Prestige Drama | Develop emotion, power relationships and reveals through changes in proximity and attention. |
| Kinetic Action | Build urgency while preserving objectives, cause and effect, and readable geography. |

Review and edit each **Scene strategy** once above its proposed shots: dramatic progression, visual progression, audience information and spatial anchors. Applying stores a distinct strategy linked to those shots; another breakdown creates a new strategy without rewriting existing shots. **Draft shot** consumes its existing strategy or proposes one for that shot alone. Closing a review preserves pending edits; it does not apply them. Request inspection and retry use the existing drafting controls.

Each shot shows its purpose and short reason. Expand **Direction** to edit intent, essential blocking, facing, eyelines, camera behavior and intended cuts. **Advanced** holds continuity, assumptions and optional long-take ideas. The same editor appears in breakdown review, single-shot review and the shot itself. Accepted scene strategy is expandable context in the editor. Intent describes what the audience should understand; staging describes one possible arrangement, not established architecture. All ordinary clips remain within the requested maximum of 15 seconds. Long-take ideas suggest later manual extension and never replace complete ordinary coverage.

Direction retains its text when inputs change and labels the affected groups **Needs review**. Story/action or strategy changes affect all groups; cast affects staging and continuity; dialogue affects intent and timing/cuts, while opening staging remains available; duration affects timing; neighbouring shots affect editorial transitions and continuity. References, crops and environment changes invalidate reference support while preserving dramatic intent. Review the flagged text and choose **Mark direction reviewed** when it fits. Downstream requests omit stale prescriptions and describe their limitations. Prompt freshness also follows direction changes.

Scene drafting uses the saved inspection set, staging constraints and applicable direction. Accepted assessments distinguish supported relationships from inferred, missing or conflicting ones. Compose uses applicable accepted assessments and the shot's attached references; it does not choose additional environment pictures. Historical **reference-supported staging** and shot-level selection requests remain readable, with their captured evidence. Changing scene setup or references can flag staging for review while preserving dramatic intent.

Applying a redraft changes existing shots only after review. Undo, recovery, shot duplication and project copying preserve direction and linked strategies. Copying references alone does not transfer direction. A single-shot draft captured before later author edits cannot overwrite the changed target. Historical drafts without supplemental metadata remain readable with review notes.

## References

**Edit references** keeps three sources together: **Assets**, **Environment** and **Shot frames**. The selected pictures, reels and voices stay alongside the browser (under **Selected references** on narrow screens). Add only the sources this shot needs, adjust crops and guidance, then choose **Apply changes**. Cancel leaves the saved references unchanged.

To reuse a picture from another shot, choose **Shot frames**, select the shot and take, and preview **Last frame**, **First frame** or **Choose frame**. Shots in the same scene are grouped first; the source's selected take is the default. **Add picture** attaches the exact frame as one numbered picture without saving an asset or replacing the other references. It does not set a generation start frame. Add as many shot frames as the shot needs to stay consistent, for example the previous shot's last frame and an earlier shot of the same room, within the nine-picture limit. They are numbered after the library images in the order listed; reorder them with the arrows. **Change** on an attached frame reopens the picker to replace just that frame with **Replace shot frame**; a frame that is already attached cannot be added twice. Its label identifies the source shot, take and frame. Choosing another take on the source shot never updates the attached picture automatically.

**Copy…** opens a source-shot preview. **Copy and edit references** opens **Manage references** with the copied selection as a draft, so you can remove pictures or shorten voice excerpts to fit the limits. Nothing changes until **Apply changes**; Cancel discards the copy. Copying can include the first or last frame of the source shot's selected take as the same pinned shot-frame picture. Voice recordings keep excerpt controls and speaker mappings. Character reference reels saved in [Assets](assets.md#character-reference-reels) can be reused here with their own guidance and optional soundtrack.

### LoRAs

Expand **LoRAs** under References to choose H3 LoRAs for this shot. They are saved with the shot, like its references, and apply in every setup on top of the LoRAs in the setup's preset (the pencil beside the preset's name), which are shared by every shot using that preset. For a LoRA in both, the shot's strength is used; the section lists the preset's other LoRAs so you can see everything a take will apply. Changing a shot's LoRAs shows **Check prompt**, and takes already generated keep the LoRAs they were made with. Register LoRAs in [AI setup](ai-setup.md#loras).

## Prompt

**Compose prompt** examines the actual cropped images and current coverage. A valid initial composition applies automatically if the empty target is unchanged. **Revise with AI** proposes changes for **Apply changes / Discard**.

With a ComfyUI model, composing is one request when the reference images, the long composition guide and the shot fit in GPU memory together, by the capacity the model test measured. The model then composes while seeing the images. Otherwise, and for a model whose capacity hasn't been measured, composing takes two steps. The first step inspects the reference images and writes a short visual brief per Picture and Video; the second composes the prompt from the brief without images. The brief depends only on the references, their crops and guidance, the model and the image size, so revisions and other shots that use the same references reuse it and skip the first step. **Request details** shows the brief the prompt was written from. **Compose prompt** sizes the request against the model's measured capacity before queueing it. A request that is too large is not queued: Prompt assistance shows the size of each step, so you can shorten it and compose again, or choose **Compose anyway**. See [AI setup](ai-setup.md).

The styled prompt editor and **Exact text** view share the same literal text; direct edits autosave. **Accept prompt** records a manual review without rewriting the text. Prompt-template deviations are advisory: **Generate takes** saves and uses the displayed text without requiring a separate acceptance step, while still validating the actual media and generation settings.

Reference or shot changes preserve your prompt and show **Check prompt**. **Clear prompt** keeps references, settings and Direction for AI; Undo restores the draft.

## Takes

[Reviewing generated takes](https://lumibelle.ai/media/manual/shots-take-review.mp4)

The dedicated **Takes** view shows large previews, newest first, with a selected badge and an optional setup filter. Its tab shows how many takes are new, such as **1 new**, until you open it or review their batch. Retries and **One more take** reuse their batch's captured inputs.

Click a take to review it. The player steps through single frames with **‹ ›** or the `[` and `]` keys. Below it, **Save frame to Assets** saves the paused frame as an image, and **Continue from this frame** makes a shot start from it. Under the player are the take's settings and any changes since it was made, and its details: the file and **Download MP4**, the generation details, timings, LoRAs and the references it was submitted with. The takes of the same batch are listed beside the player, each with **Use this take** and **Discard**.

### Refine from video

Choose **Refine from video…** on a take card or in review. For several takes, use
**Select multiple**, tick them and choose **Refine selected…**. To refine each
shot's selected take, choose **Bulk operations** › **Refine selected takes…**
above the shot list, then untick the shots to leave out. All three open the same
dialog.

Each clip becomes a separate Standard generation from its take's captured
prompt, references, dialogue, aspect and visible duration. The copied clip
guides blocking, action order, timing and camera movement. Its soundtrack is
excluded; the authored dialogue and other enabled voice references remain
active, so the result has fresh audio and can reinterpret the action or lip
movements. It does not rescale saved latents: **Upscale latents…** remains the separate,
optional saved-latent path.

The dialog lists each clip with its duration, source size and the effective
output size. **Shared settings** set the output resolution, motion instructions
and storage options for every clip. The default is **Native**, or the first
larger size for a bigger source. Choose **Edit** on a row to override them, or
to change its base prompt and references and inspect **Submitted prompt
preview**. A row is flagged when a setting cannot apply to it, for example a
size smaller than its source. Fix it or untick the row. Takes shorter than two
seconds or longer than 15 seconds are flagged; trim them first.

When one selected take uses a shot frame or start frame from another selected take, for example a scene's takes refined together, **Use refined frames from earlier clips** is offered and on by default. That clip waits for the earlier clip's refined take, then uses the same moment from it instead of the draft frame. If the earlier clip fails or is cancelled, the draft frame is kept and the batch continues. **Refinement batches** lists each frame that was swapped or kept. When the earlier clip saves no full lossless archive, its render still keeps the frames later clips use as lossless PNGs, so those clips get exact frames rather than MP4 stills. This lets you queue a scene of curated Quick drafts overnight and get consistent finals.

Choose **Start now** or **Hold until started**, then **Queue refinements**.
Lumibelle freezes the reviewed settings, prompts and seeds and prepares media in
the background, so you can close the dialog or leave Shots. Results appear as
new takes in their shots; the originals stay selected. Review them under
[Refinement batches](#refinement-batches).

### Refinement batches

**Refinement batches**, opened from the Takes toolbar or **View batch** in the
notice after queueing, lists each batch with completed, failed, waiting and reviewed counts.
Clips move through **Preparing media**, **Ready**, **Waiting**, **Generating** and
**Completed**. Media preparation runs on the CPU beside the GPU queue, and
survives restarts. A batch submits one render at a time to the ComfyUI queue,
where the usual priority and ordering controls apply.

- **Start batch** or **Resume batch** submits prepared clips.
- **Pause after current clip** lets the running render finish.
- **Cancel waiting clips** stops clips that have not been submitted.
- **Cancel current render** stops the clip that is rendering now.

For each result, **Compare** plays the final beside its source: play or seek
the final and the source follows. **Use this take** selects the result in its
shot. **Mark reviewed** records that you have looked at it. **Retry** reuses the
clip's saved request and seed, even after its source take was deleted. A failed
clip does not hold up the others. **New request…** reopens the clip's settings
to change its resolution, prompt or instructions, and queues a new request with
a new seed.

10Eros Compact, SMD Ref2VA, FastH3 Omni and PTD + RefAsymOPD are available in
[Video models](comfyui.md#presets). See the [Refine from video guide](../h3/motion-reference-promotion.md)
for captured inputs, retries and native ComfyUI execution.

### Upscale latents (alternative)

**Refine from video** is the recommended way to make a final from a draft. **Upscale latents…** is an alternative that enlarges a take from its saved latents; it needs the learned 3D latent upscaler node pack and its model. Turn on **Save latents** in the shot's generation options before generating; it is off by default, adds a few MB per take at Quick size, and ComfyUI also keeps a copy in its output folder. Then choose **Upscale latents…** on the take, in the Takes view or at the bottom of the review. Pick an output size and a mode:

- **Refine** enlarges the take and re-noises it lightly, so its motion and composition carry over. Its audio stays exactly as it was.
- **Rework** re-noises more of the take, so more can change, and generates new audio.

The refined version appears beside the original in the same review, and **Another version** makes more with new seeds. Refining needs the learned latent upscaler described in [Video models](comfyui.md#optional-add-ons). Takes generated without latents, including Upscaled previews, can't be refined directly: choose **Regenerate…** on the take, keep its seed, pick its resolution and turn on **Save latents**, then refine the new take.

### Continue from a frame

**Continue from this frame** asks which shot should open on the paused frame. **A new shot after** the take's shot is added in the same scene, with the same cast and sound, called "… (cont.)". **An existing shot** starts there instead, for example when a cutaway sits between the two; it replaces that shot's starting frame. Takes of that shot open exactly on the frame, so they play on from the take. Pause on the last frame to continue where the take ends; when you continue from an earlier frame, trim the first clip in [Cut](cut.md) to end there. The new shot opens with its action and dialogue empty: write them, or use **Draft shot**, which sees the shot before it.

**Starts from**, at the top of the shot's references, shows the frame and its take; **Remove** makes the shot cut in again. **Compose prompt** sees the frame after the references and writes the shot to begin in it and carry on from there, rather than opening differently from the previous shot. The frame needs the same aspect ratio as the shot, and its take can't be discarded while a shot starts from it. A shot that starts from a frame can't also have a first-frame reference. Changing or removing the frame marks the prompt for review and the existing takes as changed.

To assemble takes into a sequence, open the [Cut studio](cut.md).
