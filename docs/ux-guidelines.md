# Lumibelle UX guidelines

Version 3 · 2026-11-02 · Shared behavior baseline; visual foundations and the shared components are specified in [Design system](design-system.md).

These guidelines describe the intended experience, not a claim that the current UI already meets it. Apply them incrementally; preserve the existing production and recovery behavior.

## 1. Make the work the most prominent thing

The screenplay, asset images, shot description, or assembled cut is the main surface. Navigation tells the author where they are; tools help them perform the current task.

- Keep the warm neutral palette and plum accent in both light and dark appearances. Use the semantic tokens and locally bundled Geist typography defined in the design system; preserve screenplay monospace and original media colors.
- Use the compact app/project bars for global context, project navigation, save state, and pane controls. Keep studio headings accessible, independent scrolling intact, and one horizontally scrollable project-tab row on narrow screens.
- Use the center for the work, the left pane for finding/selecting it, and the right pane for the current assistance or generation task. Shots uses persistent Shot / Takes tabs in the center pane header, References in the right pane, and Prompt / Generation settings as two tabs of one dialog opened from the right pane's footer. Coverage belongs only to Shot. Language versions uses the same three panes: master takes on the left, the dialogue editor in the center and translation assistance on the right. Keep shot identity and setup selection compact above the tabs, with administrative actions in Shot options; each tab body scrolls independently. Cut keeps its viewer-and-timeline layout.
- Keep independent pane scrolling and reachable action footers. Avoid scroll areas nested inside other scrolling panes, apart from editors and dedicated media controls.
- Pin only what is needed while scrolling: identity, view navigation, and the active task's action. Long descriptions, help, administrative actions, and technical details belong in the body or a focused inspector.
- Selected items, approved/production items, and items currently playing are different states. Give them different labels and indicators.

## 2. Give each kind of control a recognizable appearance

| Meaning | Representation | Lumibelle examples |
| --- | --- | --- |
| Navigate to another page | Text link; active destination has an underline | Script, Assets, Shots, Cut |
| Switch the contents of this pane | Tab row attached to the pane, with a shared baseline and a strong underline on the selected tab | Shot / Takes; Source / Outline; AI settings sections |
| Choose a mutually exclusive operating mode | Joined segmented selector inside a single subtle track, with a visible selected state | Create / Edit |
| Perform an action | Button labelled with a verb and object where needed; ends with … when it opens a dialog or further choice | Generate takes, Edit references…, Apply changes |
| Filter a collection | Labelled select, search, or filter chips; show the current filter/count | Look, Characters, Starred |
| Reveal optional detail | Text disclosure with a chevron and expanded state | Advanced, Generation details |
| Show status | Non-interactive text or badge with a meaningful label | Saved, Approved, Queued, In Trash |
| Show help | Small inline help trigger | What is a target look? |

### Tabs

- Use one shared tab treatment across studio tool panes. Reuse the clear active underline already used by project navigation; keep actual route navigation as links.
- A tab labels a destination or topic. Prefer **Generation** to **Generate** for a pane, while the submission button remains **Generate takes**.
- The selected tab uses stronger text plus a 2–3px indicator. Do not rely only on a faint fill or color difference. Tabs do not look like separate outlined action buttons.
- Place tabs immediately against the content they switch, on one row. Assets uses Create/Edit as its only tool tab row; avoid nested tabs there.
- Use stable tab IDs and a group ID independent of display labels. Two tab groups in one workspace must preserve their own selection, focus, scroll, and ARIA panel relationships.
- Counts and issue markers may appear in tab labels, such as **Inputs · 2** or **Inputs · Needs attention**. Explain the actual issue beside the action and affected field.
- Tab changes retain drafts, crops, selection, and request state. They never submit work.
- Preserve `tablist`, `tab`, `tabpanel`, selected state, associated IDs, and roving keyboard focus. Arrow keys navigate the tabs; Tab leaves the tab row. Automatic activation is appropriate when the local panel appears immediately. Follow the [WAI tabs pattern](https://www.w3.org/WAI/ARIA/apg/patterns/tabs/).

### Actions and modes

- Use filled plum for the principal action in a task area. Other actions are neutral or text buttons. Multiple independent areas can have their own primary action; do not make every button equally prominent.
- Mode selectors choose a state. Use proper radio-group or pressed-state semantics appropriate to their implementation, rather than giving them tab semantics solely to obtain tab styling.
- Name pane visibility controls after the pane they open, such as **Show Library / Hide Library** or **Show Asset tools**, with a recognizable panel icon. Bare **Assistant** must not ambiguously mean both a pane toggle and a tab.
- Put **Reset layout** and infrequent layout controls in a labelled **Layout** menu. Keep opening a hidden pane easy on desktop and mobile.
- Retain explicit, visible entry points for frequent tasks: Extract from script, Add images, Add voice, Add references, and Review results. Move administrative operations such as Duplicate, Archive, and Delete into object-specific menus where they are not the current task.
- A disabled primary action needs a nearby reason and an actionable repair path. A tooltip on a disabled button is insufficient.
- Every studio's project-bar actions follow one order: save status, **Save**, **Undo** and **Redo** where the studio has them, studio actions such as **Export MP4…**, then an options menu for infrequent commands such as **Recovery…** and **Export Markdown**.
- Save status uses one vocabulary: **Saved**, **Unsaved**, **Saving…**, **Save failed · draft retained** and **Conflict · draft retained**. A failed save shows its repairs in one notice: **Retry save**, **Download unsaved copy** and **Reload saved version…** as the studio supports them.
- A menu item or button that opens a dialog or a further choice ends with an ellipsis (**Delete…**, **Edit details…**, **Move to…**). Items that act immediately do not.
- Recoverable media uses **Move to Trash**. Documents and objects use **Delete…**, which asks first. **Delete permanently** and **Remove permanently** appear only in Trash and storage management.
- Multi-selection modes toggle between **Select multiple** and **Done selecting**.

## 3. Reduce repeated controls, not readability

Use a small spacing scale: 4px for tightly related items, 8px within a control group, 12–16px between fields, and 20–24px between task sections. Treat these as starting values, not reasons to distort an existing editor.

- Prefer 13–14px control text and 12px supporting text in dense panes. Avoid turning every action into tiny low-contrast text to fit more controls.
- Prefer separators and spacing to a succession of nested bordered cards. Reserve cards for selectable objects, such as images, takes, or library results.
- Use a thumbnail, a useful name, one or two state labels, and a concise action area for repeated rows. Do not repeat full edit forms for every object in a collection.
- Keep essential filenames, long names, and provenance readable in details. Use meaningful display labels and thumbnails in pickers; a GUID filename is a fallback identity, not the normal way to find an image.
- Plan for generous hit areas: approximately 32–36px high on desktop and 44px for frequent touch controls. A visually small icon can sit within a larger hit area. These are product targets; WCAG's minimum criterion is 24×24 CSS pixels subject to its exceptions, not a requirement that every icon glyph be 24px. See [Target Size (Minimum)](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html).

## 4. Put details in the appropriate place

| Surface | Use it for | Behavior |
| --- | --- | --- |
| Main editor | Frequent creative changes | Remains directly editable with existing autosave and undo |
| Docked tools | Prompt/instructions, inputs, generation options, current request | Keep context and primary action reachable |
| Disclosure | Occasional details within the same task | Opens in place without navigation or resetting state |
| Help popup | Short explanations and examples | Opens on hover, keyboard focus, or explicit click/tap; dismissible with Escape |
| Object inspector/dialog | Image metadata, one reference binding/crop, voice excerpt, look definition | Explicit local draft and Apply/Cancel when changes are staged |
| Review dialog | Large image/video/proposal comparison and accepting a result | Media/content first, actions accessible, details collapsible |
| AI activity / Requests | Current jobs and past results | Compact summaries lead to review; response history does not lengthen the composer |
| Global/project settings | Installed models, defaults, project policy | Separate from per-request creative decisions |

- Keep prompt composition docked. More modals are useful for bounded tasks, not as a replacement for all studio controls.
- Extend the existing media review surface with selected-image details where practical. Avoid introducing a second competing preview for the same image.
- Preserve selection, scroll, compare pair, crop, and playback position when editing details. Image review opens editable details by default beside the viewer and keeps them open after Apply or Cancel. Place the source and take choices in a horizontally scrollable strip below the viewer, with comparison and crop controls in the dialog header. Detail changes and AI prompt application remain explicit; guard image switches and dismissal when details are unsaved.
- Reel details keeps name, look and reusable directions visible beside the player. Put infrequently read use guidance, applied LoRAs and the captured recipe in collapsed sections below the player; expanded content scrolls with the dialog. Keep keyframe editing and applicable generation actions in the persistent footer with Close and Save details. Image review shows its toolbar only for comparison or crop controls; omit generic instructions to select an already visible image.
- Keep one active modal surface. A subtask can replace its content and return, or suspend a tools drawer, rather than stack multiple competing dialogs.
- Modal focus stays inside; provide an accessible title, visible close/cancel control, Escape behavior, and focus restoration to the origin or a sensible replacement. Follow the [WAI modal-dialog pattern](https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/).
- Help must also work with keyboard and touch. Do not hide an input limit, data-sharing choice, missing source, conflict, or other consequential information in help.

## 5. Make generation state concise and trustworthy

- Keep one task footer containing captured/effective context, progress or the next action, and review/cancel as applicable.
- Show the current request's state once in its pane. If the pane is hidden, expose a compact summary outside it. Avoid duplicate cancelled/error paragraphs in the page toolbar and the open pane.
- Use distinct states: **Ready**, **Queued**, **Running**, **Saving results**, **Completed**, **Cancelled**, **Failed**, **Needs attention**. Cancellation with saved takes should say how many remain available and provide Review.
- A queued job shows its queue position or waiting reason. A running job shows available phase/progress and a ticking elapsed time; do not imply a percentage that the provider does not report.
- Normal readiness is one short summary. Show setup advice for a selected unavailable feature beside that feature; keep optional unselected capability details in diagnostics.
- Technical sampling settings, encoders, archive details, and observed timings belong in **Generation details**. Keep choices that materially affect output, such as workflow, quality, aspect, duration, reference inputs, and take count, understandable before submission.
- Distinguish active work from unread results in global AI activity. A bare aggregate number must not suggest dozens of running requests. Keep counts and job history intact when changing their presentation.
- Preserve current background-job behavior: one logical submission per action, immutable batch inputs, explicit retries, no surprise modal stealing focus after navigation/dismissal, and One more take extending the captured batch. Closing review does not cancel generation.

## 6. Use words that distinguish production concepts

| Concept | Preferred label / explanation |
| --- | --- |
| Stored media on an asset | Image; the collection is Images or Image library |
| Image chosen for a generation request | Reference image, numbered in submitted order |
| Base image being edited | Source image / Image 1 · Base |
| Named character appearance | Look; gallery filters do not change creation destinations |
| Image accepted as a usable reference | Approved reference; keep approval distinct from cover and defaults |
| Image anchoring a shot endpoint | First frame / Last frame; exclusive whole-frame reference purpose |
| H3 consistency instructions | Keep consistent in shots; show whether inherited or customized |
| Character assignment on a shot | Character / Represents, bound to exact character identity |
| Generated alternative | Take; use Takes consistently instead of alternating with Candidates |
| Choosing a shot's production version | Use this take; does not replace an explicitly chosen Cut clip |
| New request with captured batch settings | One more take |
| Information about a failed request | Response details / Generation details, with explicit Retry |

Naming changes must not change underlying approval, reference, generation, or ownership behavior. Reference numbering in prompts and saved provenance remains exact.

Missing relationships need a direct path forward. For example, a character image with **No character link** should explain whether the character is absent from the shot and offer an explicit **Add character to shot** or **Link character** flow. Do not infer cast or looks silently from filenames, capitalization, or image contents.

## 7. Apply the language to each studio

### Script

Retain the continuous screenplay and collapsible outline. Keep the dedicated right-side **Assistant** panel and its narrow-screen drawer. Put compact model options inside the panel and **Requests** beside its heading. Put the current action, target, and instructions together. Keep **Revise** primary for an existing script and **Draft script** for an empty one; keep replacement/start-over operations secondary. Show the selected scope once clearly, with a compact reminder in the footer if needed. Use help for token/context explanations. Retain diff review, Apply changes, and Undo.

### Assets

Keep the three-column workspace. The left library follows its saved manual order. Each row has a drag handle and an actions menu for Rename, Move to…, and Delete. Show insertion markers while dragging and keep Move to… usable with touch and the keyboard. A filtered move positions the asset in the full library; Undo reverses one move without removing newly published media.

Pin the editable asset name and a short visual-notes preview above the center gallery. Put **Edit details** at the right of the name; it opens a modal containing the asset name, notes, preservation guidance and Assist, suggested references, script evidence, and character looks. Use a scrolling modal body and a persistent save-status/Close footer. Keep the existing autosave and flush before closing; failed saves keep the modal and local edits open. Closing returns focus to Edit details. Asset and look guidance links open this modal before their existing review. Keep asset actions in the left row menu.

Use one mixed reference gallery with **All / Images / Reels / Voices**, search, and viewing-look filters. Voices remain available across looks. Order newest first with stable media-type/ID tie-breakers. Keep approval badges and approval/cover actions; omit the gallery COVER label. Use cached reel thumbnails and matching 4:3 voice previews. Fit full images and reel frames inside the thumbnail, preserving portrait framing rather than cropping to fill. Overlay a media-type icon and clip/excerpt duration on the thumbnail; keep the body to a single-line, ellipsized title and menu. Expose the full title on hover and in the selection button's accessible name. Use the same borderless menu button for images, reels, and voices. Show a translucent look-name badge at the bottom left of character image and reel previews, using General when unassigned. Truncate long look names with a full-name hover hint, and keep approval badges separate at the bottom right. Voices and non-character media have no look badge. Load playback only through Preview or an active editor. Keep reel request history in global AI activity, with links to captured request and response details; do not append a request list below the gallery. Keep deleted-reel recovery in Trash → Reference reels, without a recovery section below the Assets gallery. **Import…** contains only the selected asset's supported media types.

Clicking the card body selects it and opens **Edit**. Clicking it again, **Clear selection**, or **Create** deselects it. The thumbnail opens Preview; keep the action menu beside the title, without a separate Preview row. Preview and menu actions preserve the edit target unless moving the reference. Metadata dialogs preserve the selected follow-up target. Character images and reels offer **Change look…** beside **Move to asset…**, opening a small Apply/Cancel picker. Keep look assignment out of the card body. Changing a look organizes the saved reference without changing generation destinations or captured recipes; failed saves retain the picker for retry. Keep selection borders even around the entire card. Keep bulk image operations in explicit **Select multiple** mode; bulk checks do not change the tools. A hidden selected item remains named in the tools with **Reveal in gallery**. Filters never change draft prompts, references, or destinations.

Image, reel, and voice menus offer **Rename**, focusing and selecting the current name in the existing editor. All three open their preview/details modal. Keep the name directly accessible, with metadata save/cancel controls in the persistent modal footer. Image details come before the batch list while editing. Repeated Rename preserves an already open draft, and existing save/cancel behavior remains in effect. The left asset list uses its existing Rename shortcut to the pinned name field.

Reel and voice menus also offer **Edit details** and **Move to asset…**. Edit details opens the requested record and preserves an already open draft. Moves flush the current editor, ask for another asset of the same type, then follow the reference to its destination editor. Character reels become unassigned to a look. Media identity, attached references, and captured recipes stay intact; old voice bindings still resolve after a move. **One more** keeps its captured destination, while **Generate a variation** creates an editable recipe for the current owner. Keep crop, cover, and approval actions specific to images.

**Create / Edit** are the only tool tabs. Create has an **Image / Reference reel** selector, with reels available for characters, environments and props. Create image shows workflow, prompt/Assist and collapsed LoRAs/settings, with output controls and Generate in the persistent footer. Edit image shows the source and compact input rows; **Manage references** handles crops, source replacement and additional images with one Apply/Cancel transaction. Choosing Edit without a selection asks for a gallery card.

Create reel combines picture references, applicable voice fields, generation prompt/Assist, use guidance and collapsed settings in one scrollable form. Duration, aspect and Generate reel stay in the footer. Edit reel keeps playback and follow-up actions; its name, use guidance, look and captured recipe belong in the preview/details modal. Variation opens an editable unassigned recipe copy; One more reuses its captured destination and settings. Voice selection shows playback and a details entry; the modal owns name, excerpt controls and explicit Save. Closing unsaved reel/voice details offers Save, Discard changes or Keep editing; failed saves retain the modal draft. Label image preservation notes Use guidance (optional), explain their role in composing appearance references and retain identity-only behavior.

Fresh image creation, image edits and reel drafts use an unassigned destination. Organize saved results afterwards. Existing associated drafts show their destination with an explicit **Use unassigned** action; clearing a reel destination preserves its previous saved draft. Selection and mode changes flush edited records before leaving. Failed saves keep the editor and its draft available. Hidden editors keep job observation, request recovery and drafts alive. Preserve specialized review, automatic review opening, initial prompt application, and historical request links.

After a Create image request successfully saves every candidate, clear its used prompt, tags, fixed seed and destination, keeping the selected workflow and aspect. Image-edit drafts stay intact. After a reel request successfully saves every clip, open a fresh default recipe with empty pictures, directions and prompt pair; retain the original recipe for review and variations. Only clear an unchanged submitted draft, including when completing in the background or reopening Assets. Preserve newer edits, failed/partial/cancelled requests, and captured One more inputs. Disable Generate reel while this asset has a queued, running or unconfirmed reel request; label the button Queued, Generating or Cancellation requested and keep View request available.

### Shots

Keep **Shot / Takes** visible in the center header. Description, dialogue, and cast names belong in Shot; the take gallery has its own view. References stay in the right pane with numbered image crops and advisory AI use hints; the prompt editor and generation settings open as the **Prompt** and **Generation settings** tabs of one dialog from the pane's footer.

Shot breakdown review is an editorial decision. Changed or omitted dialogue appears only in collapsed **Script comparison notes** and never disables **Add reviewed shots**. Preserve the proposed wording exactly; the author can keep it or edit it later. Previously blocked dialogue-only responses reopen from their saved output without another provider call.

Possible cuts, transitions, extra shot labels and timestamps in draft descriptions are advisory **Camera and editing notes**. Wording can refer to the edit between clips or even prohibit a cut; never reject a whole shot list from this keyword check. Preserve descriptions exactly and keep Add reviewed shots available. Reopening a previously rejected, complete response reparses its saved output for review without a provider call; incomplete JSON still requires correction.

Source block references are provenance, not a review gate. A proposal that reproduces source UUIDs with drifted characters keeps every reference that resolves to its own scene and drops the rest, reporting them in **Source reference notes**. Keep Add reviewed shots available and preserve the proposed shot. Reject only when no reference in a shot resolves, since that shot has no usable script source. Never fabricate or repair a UUID to fill a dropped reference; the author repairs provenance by editing the shot.

Draft shots includes default editorial guidance for independently generated clips: adjacent shots should have clearly distinct ending/opening compositions that support deliberate cuts, rather than relying on seamless fusion. The assistant chooses motivated camera changes while preserving geography, eyelines, action and dialogue continuity, and describes the intended start and end of each shot. Keep this as planning guidance, with no prescribed list of angles or mechanical acceptance check.

An initial AI composition into an unchanged empty prompt applies automatically, with no acceptance button or automatic video generation. Existing text stays editable while a revision runs. Show a compact **Review changes** action when a suggestion is ready; open a wide diff dialog with complete current/suggested prompts, highlighted deletions/additions, and a persistent **Close / Discard / Apply changes** footer. Close keeps the suggestion available. Keep the AI explanation collapsed. Preserve literal wording, reference tags, dialogue, and blank lines in the comparison. On narrow screens the dialog fills the screen and stacks the two prompts, with actions always reachable. Applying uses the captured-input checks; newer edits remain protected.

Keep quality, resolution, takes, and the effective aspect easy to see. Put seed and technical settings under Advanced/details. Review should begin with the take, not optional refinement-installation advice. Saved takes must have unambiguous labels across batches within a shot; batch-local Take 1 alone is insufficient in a shot-wide list.

### Cut

Keep the viewer and timeline as the main surface. Distinguish selected clip controls from playback state and keep take switching in place. Empty Cut should explain that no clips have been added and offer **Choose takes** in the empty viewer, rather than presenting an unexplained black player. Retain exact numeric trims as an alternative to dragging.

## 8. Responsive and state requirements

- Preserve the existing independent-pane and drawer behavior. On narrow screens, show one main surface and open tools as a drawer with a stable action footer.
- Compact object headers must leave useful room for content. Do not pin an entire metadata form above a small scrolling gallery, especially with a mobile keyboard visible.
- Tabs stay on one row. If labels cannot fit, contain scrolling within the tab row and reveal the active tab; never widen the page or turn tabs into a wrapping button grid.
- Preserve label meaning on mobile. A control may wrap or move to a labelled menu; do not shorten it into an unexplained icon.
- Keep selected object and request context stable across pane changes, resize, navigation recovery, and result arrival. Showing a different view must not change an asset, look, source, model, or prompt.
- Ordinary workspace navigation restores the last browsing position for that project in this browser. Explicit item, setup, view and request links override it. Use readable project links through the shared route service; keep immutable IDs for storage and media. See [workspace navigation](workspace-navigation.md).
- Existing atomic saves, revision checks, draft retention, undo, immutable approvals, exact source identities, Trash recovery, and unavailable-source blocking are invariants. UI cleanup does not authorize relaxing them.

## Review checklist for a UI change

- Can someone distinguish tabs, mode selection, actions, and status without clicking?
- Is the current object and operation clear, including where generated results will be saved?
- Can the author reach the main action while scrolled into the work?
- Are common tasks visible and uncommon controls grouped rather than repeated per row?
- Are blocking issues visible, with a repair action that opens the correct control?
- Does keyboard navigation expose selection, expanded state, and a visible focus indicator?
- Do long names, many objects, short windows, 200% zoom, and mobile input remain usable?
- Do pane/dialog changes preserve drafts, media state, and queued-job snapshots?
- Were layout-only changes checked without live paid generation, using isolated fixtures for mutations?
- Does documentation distinguish observed behavior, proposed changes, and completed validation?

## Script writing

- Put action, captured target, and Instructions in the Script Assistant panel. Keep model options one click away. Collapse reasoning. Author-selected discussion is visible and removable; no hidden creative brief or production profile enters requests.
- Keep Save and its state visible. Disable it when the browser and saved script match; a failed save preserves the browser draft. Put Export Markdown and Recovery… in the Script options menu.
- Outline insertion follows the selected section. Drag handles expose ordering; row menus provide equivalent keyboard/touch moves, rename, insertion, deletion, and scene splitting. New sections appear and focus their heading for naming.
- Default existing writing to focused Revise. Broad Rewrite is explicit. Review shows changed text and a list of specified edits, including moves. Applying is complete only when storage and the editor agree.
- Script has no production approval stage. New downstream work captures a saved script source; existing assets and coverage keep their original provenance.

## Compact text assistance

Use the same **✦ Assist** trigger and compact composer for image prompts, shot prompts, and asset/look/image guidance. Put model selection inside the composer, not beside every trigger. Script keeps a dedicated Assistant panel for its richer actions, targets, instructions, and discussion context, using the same model controls internally. Keep **✦ Draft shots** and **✦ Extract from script** descriptive. Opening does not submit. Closing retains the instructions, target, attachments, and temporary model choice; jobs and recovery continue independently.

Composers use the small dialog width (520px), with scrolling content and a persistent action footer. On narrow screens they become full-width sheets. Show operation and target first, relevant context/options next, then the existing instructions. Do not invent instruction fields for operations without them. Move Direction for AI into the shot prompt composer. Place Shots Assist and Clear prompt in the H3 prompt toolbar beside Undo/Redo. Keep comparison/proposal editors specialized and preserve their application rules, including automatic valid initial shot-prompt application and manual dialogue review.

Inside assistance, the model chip opens options: **Use project default**, configured choices, collapsed supported reasoning, Refresh availability, and Manage models. Label **Global**, **Project**, and **Override** distinctly. An override lasts through failed saves and clears only after successful enqueue. Only **Set as project default** persists it. Project settings has the same control and **Use global default**. All new requests resolve override → project → global; legacy studio choices do not participate. Never substitute an unavailable model. Refresh on page focus and before capture; a changed resolved choice pauses submission for review.

The text action itself follows its captured request across Script, extraction, image enhancement, every guidance field, shot drafting, shot prompts, and reel prompt pairs. Idle opens assistance (Script submits its panel). Preparing disables duplicate submission while composer closing remains available. Queued/running controls stay clickable to inspect the exact request; show a queue icon or spinner and waiting/elapsed time inside the control. A result becomes **Review changes**, **View response** for discussion/clarification, or **Needs attention** for failure/invalid output. Applied, discarded, confirmed cancelled, and automatically applied initial results return to the ordinary action. Closing a review retains its result; preserve existing automatic review opening.

Keep **New request** for terminal results and **Cancel request** for active work in the adjacent request-options menu. New request opens a draft without submitting or discarding earlier output. Cancellation remains **Cancellation requested…** until confirmed. Inspection must work when a model is unavailable; review clicks must not recapture selection or refresh model choices. Full progress, queue position, captured model, errors, retained response, explicit retries, and Request details stay in existing request/review views. Retain save errors and history access. Keep image/video generation visually distinct.

Use the shared request timing formatter. Waiting begins at recorded creation; running elapsed begins at recorded start. Wrap timing inside the control on narrow screens. Announce state changes, not ticks; retain focus and honor reduced motion. Estimates mean remaining time in the named stage, for example **42s elapsed · ≈20s left in sampling**. Never treat a token budget or received-character count as a completion estimate. Historical checkpoints without live estimate metadata show elapsed time only.

ComfyUI estimates require explicit real-work totals. Use the last five strictly advancing intervals, at least three spanning two seconds, and median seconds per unit. Ignore duplicates/heartbeats, reset at counter/total/node/phase/candidate changes, and hide after disconnection or a stall exceeding max(15 seconds, three median update intervals). Require fresh observations to restore an estimate. Age it from its observation timestamp and omit expired estimates rather than leaving zero on screen.

Codex preparation/image-tool/follow-up durations are collapsed by default. The active disclosure title follows observed activity: **Preparing request…**, **Image tool running…**, or **Agent follow-up…**. It has no timer; retain one overall elapsed time alongside candidate count, the progress bar, and cancellation. After completion use **Timing details**. Expansion survives progress and candidate updates for the same request; a different request starts collapsed. Keep detailed durations, help, and unavailable-timing explanations inside.

### Character reference reels

Expose captured custom directions in reel Details with Copy directions. In Assist, offer Reuse directions from a reel across compatible reels in the current project. Selecting a source previews it; an explicit Use these directions action copies only framing and Instructions. Preserve the target's pictures, voice, timing, destination and authored prompt pair, and let the author compose or revise afterwards. Reuse the existing captured recipe; do not rewrite source provenance or invent directions for imported clips.

Keep reels in Assets' mixed gallery and their recipe under **Create → Reference reel** in the third **Asset tools** column. Restore the latest saved recipe or show a fresh recipe immediately, with no prerequisite empty state; opening alone does not save a blank draft. Combine setup and prompts in one scrollable form with a persistent generation footer; use the existing tools drawer at narrow widths. Gallery filtering does not switch tools. Switching creation type hides the other editor without unmounting it. Hiding a drawer must remain possible during request preparation; offer cancellation before enqueue and keep queued jobs independent of the visible pane.

Show reel pictures as compact numbered crop thumbnails with AI hints and guidance summaries. Keep cropping, guidance, ordering and replacement in the picture manager; selecting a summary opens that picture's details, including in the narrow Selected view. Speaking modes offer an optional voice description for the next Assist composition/revision. Existing recordings remain the identity source, and Silent ignores the retained description. Description edits must not overwrite authored prompts; show Check prompts instead.

The **Framing preset** selector belongs inside **Assist**, alongside instructions and a readable view plan. Adding pictures or selecting a preset leaves the prompt pair unchanged. Assist inspects the selected crops and uses their guidance, the framing plan and instructions to compose both texts. **Custom** gives Assist no prescribed framing and keeps existing writing. Valid initial AI pairs apply only to unchanged empty drafts; revisions use the paired Apply/Discard comparison. Manual prompt writing remains available without a text model. Keep prompt Undo across selection and creation-mode changes, and flush browser text before switching assets. Concurrent Details edits preserve fields the author did not change and reject competing edits to the same field.


### Unified shot references

New reel attachments default to Keyframes. Keep representation and audio independent within the reel row: **Visuals: Keyframes / Full reel / None**, **Use audio**, and an excerpt for keyframe/audio-only modes. Count ordinary pictures before reel frames, show the resolved Picture/Video/Audio identifiers, and reject over-limit sets without dropping inputs. Keep legacy attachments in full-reel mode until explicitly changed. Preview controls never change representation.

**Manage keyframes…** edits a reel's default set in Assets, or a copied setup-specific set in Shots. Provide a seekable player, exact preview, frame stepping, notes/crops/order, explicit Auto-pick with local Undo, and reachable Save/Cancel actions. Changing N alone preserves picks. Initial suggestions and analysis are lazy; no analysis on gallery browsing and no extra AI description request. Save failures retain drafts; closing restores focus. Show whether pictures come from original lossless frames or decoded playback video.

Shots has one **Manage references** entry above its compact Images and Reels summaries. The manager browses asset images and reels together, with All / Images / Reels, search, asset and look filters; image Origin appears in Images mode. Keep image-only consumers, including reel-generation pictures, image-only. Use stable media identities and ordering, mark selected cards, and load reel playback only on explicit Preview. Preview and Add are separate actions.

Use separate ordered Images and Reels groups in the selection panel, with actual Picture/Video/Audio identifiers. Keep crops, hints and image guidance, plus reel use guidance, soundtrack, optional speaker mapping and usage snippets in expandable rows. Reels copy their exact media and current guidance into the setup; later library edits do not rewrite them. Warn when conditioning uses a prefix and may omit later views. Standalone voices stay in the tools pane.

Apply both media lists in one serialized save and one Undo step. Cancel changes neither list. Failed saves retain the draft and retry without duplicate additions or Undo entries; competing setup or source edits must not be overwritten. Existing standalone clips remain usable, while new video selections come only from asset reels. MP4 import and take copying are absent from Shots; Assets' existing import workflow remains available.

Keep Browse/Selected tabs on narrow screens and the Apply/Cancel footer reachable. Filters and draft state survive tab switches, keyboard users can reach every control, and closing restores focus to Manage references.

The **Side → rear → face** reel preset defaults to requested 5 seconds (5.167 generated), with editable timing/aspect. Spend 30% on a full-body profile, 40% on a rear view gently raising both arms to shoulder height, and 30% on a frontal face close-up with a restrained push-in. Speech occurs once across the cuts. Assist receives the concrete view instructions and selected cropped pictures, and paired use guidance describes intended appearance details without inheriting the reference performance. Existing presets and Custom retain their editing/review behavior.

Environment assets share the mixed gallery and Create → Reference reel tools. Fresh recipes use 16:9, 5 requested seconds and a 360° turn; one explicitly selected picture is sufficient. Keep camera presets in Assist, including a pan and an author-described custom path, and preserve text until composition/revision is applied. Omit character looks and all voice controls. Environment references supply spatial layout and setting details to Shots; unlike character references, their background is useful reference content. Keep generated environment soundtracks off by default and explain when conditioning only includes the beginning of the camera turn or route.

Prop assets share the environment reel tools and silent recipe. Fresh recipes use 1:1, 15 requested seconds and a 360° orbit; camera presets move around, or turn, one stationary object rather than surveying a space. Omit character looks and all voice controls, keep generated soundtracks off, and label prop reels “Prop reel” in reference pickers.
