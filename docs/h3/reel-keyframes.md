# Reel keyframes

A character or environment reel remains one item in Assets. Its saved keyframes are reusable pictures inside that item, not extra gallery images. New shot and reel-recipe attachments inherit the asset's **Default reel usage**, which is **Keyframes** when unset. Older attachments continue to use **Full reel** until explicitly changed.

## Pick and review frames

Choose **Manage keyframes…** from the reel's menu or Details. On first use, Lumibelle suggests up to three distinct frames locally. Change the requested count from one to nine and choose **Auto-pick** to replace the dialog draft; changing the count alone changes nothing. **Undo picks** restores the previous selection. Static or repetitive footage can yield fewer picks; use manual selection when a subtle difference matters.

Use playback and the frame timeline to find a moment, step through adjacent frames, and **Add current frame**. The exact extracted picture appears below playback. Replace a pick, reorder or remove it, and optionally add notes. **Crop keyframe…** opens the same crop editor as image crops, with shapes, a free selection and precise pixel controls; the pick then shows its cropped size. The crop is saved with the keyframe, so it follows the reel through **Replace in shots…** and into Keyframes and RefMod inputs. Timestamps identify moments; automatic picks have no inferred labels such as “rear” or “profile.”

Timeline and step selections stay on the exact decoded frame until you play or scrub the video. Playback seeks inside that frame's display interval to avoid rounding to its predecessor. Imported clips retain their original playback timestamp offset separately from saved relative frame timestamps, so existing picks keep their identities when timestamp caches refresh.

**Save keyframes** changes the reel's default set for future attachments. Cancel keeps the earlier set. Save errors leave the draft open. A setup's **Manage keyframes…** editor instead changes only that setup's copy; **Use saved reel keyframes** explicitly refreshes it from the library. Later library edits, moves or deletion do not alter saved setup inputs.

## Use as pictures for another reel

In reel creation, open **Manage reel references** and browse **Reels**. Adding a reel inherits its asset default. Choose **Keyframes** under **Visuals** to use individual stills; this copies the saved keyframe set or prepares a local suggestion on first use. **Manage keyframes…** adjusts this recipe's selection, order, crops and notes. **Use saved reel keyframes** explicitly reloads the source defaults. Cancel leaves the recipe unchanged; Apply saves the selected references together and marks existing text **Check prompts**.

In **Keyframes** mode these inputs supply still pictures only. **RefMod** and **Full reel** instead supply Video references; see [selected-keyframe RefMods](selected-keyframe-refmods.md) for defaults, overrides and inspection behavior. Reel audio remains separate under the recipe's voice controls. Ordinary images are numbered first, followed by each reel's selected frames, with a combined limit of nine pictures and three source reels. Assist sees the actual cropped PNGs, source names, guidance and notes in its existing request. Generation captures those same pictures; it uploads no input MP4 and adds no video-conditioning nodes. No extra AI analysis or hidden gallery images are created. Saved selections survive source-reel moves or removal from the library.

## Use in Shots

Inside **Manage references**, each reel has **Visuals: Keyframes / RefMod / Full reel / None**. [Character voices](character-voices.md) are selected once per character, independently of the reel's visuals. Environment and legacy clips retain **Use audio**. Keep at least one input enabled for each reel. Playback never changes the selection or representation.

- Keyframes become ordinary H3 pictures: regular images first, then reels in their selected order, then each reel's saved frame order. Full reel and RefMod inputs receive Video numbers.
- Keyframes and None use an independent audio excerpt, initially the available recording capped at 15 seconds. Change its start and duration, preview it, and optionally map it to a dialogue speaker. This does not create a voice asset or shorten the excerpt to the target shot.
- Full reel retains existing prefix conditioning, including paired soundtrack and the warning when later views fall outside the target duration.
- Long environment surveys are primarily for keyframe extraction. A direct video reference supplies only the start of the reel, capped by the target shot's generated duration; selected keyframes can come from later in the reel. See the [environment camera presets](reference-reels.md#create-an-environment-reel).

The manager displays actual Picture, Video and Audio identifiers and effective counts. Limits remain three reel attachments, nine effective pictures, three enabled audio references, twelve combined source files, and the existing video/audio duration limits. Correct an oversized set explicitly; no frames are silently dropped. Combined Apply makes one save and one Undo step, preserves authored prompt text, and marks it **Check prompt**.

Compose receives the selected cropped PNGs, copied reel use guidance, grouping, frame notes and audio mappings in the existing vision request. It uses character frames for appearance and environment frames for layout. Only the selected pictures are visual evidence. Source camera work, actions and sample dialogue are not target-shot instructions. There is no additional AI description call.

Keyframe generation captures only those pictures and enabled audio. It uploads no MP4 for that reel and adds no video-loading or video-conditioning nodes. Retries and added candidates retain captured bytes.

## Storage and extraction

To keep a single angle as an ordinary library picture, open **Manage keyframes…**, seek to the desired moment and choose **Save frame to Assets…** beside the player. The save dialog shows the exact full frame in a crop editor and offers a name, preservation notes, destination asset and character look. It initially targets the reel's current asset and look. Crop to keep only part of the frame, for example one character out of a changing scene; **Full image** saves the whole frame. **Save and edit** opens the new picture for image editing.

**Edit frame…** beside the player does both in one step: it saves the frame, cropped if you choose, as an unapproved image and opens it in the image editor, for example to turn a lamp off before using the room as an opening reference. When no pick changed since the keyframe manager opened, it closes so the editor is in view; otherwise it stays open so the picks are saved or cancelled deliberately. The edit result is a further independent image; the keyframes are not replaced. Each pick also offers **Save as image…**, which starts from that pick's crop, view name and notes.

This explicitly creates an independent PNG; changing or cancelling keyframe picks does not affect it. The source reel's keyframe defaults and attached setup selections are unchanged. The saved image starts unapproved and can be selected, renamed, edited, moved or trashed like other images. Its metadata records the source reel, immutable frame identity, timestamp, the full frame size, the crop when there is one, and whether the source was the lossless archive or decoded MP4. **Saved metadata** shows the crop as the kept pixels within the full frame. A saved image stays fixed when the reel is later replaced or regenerated; use keyframe crops for references that should follow the newer reel. Deleting the reel later does not remove the image. Cancel creates no image, and retrying a failed save retains the same operation identity to avoid duplicates.

New reel requests enable **Keep lossless frames** under advanced Generation settings. Opt out for MP4-only output. The existing segmented lossless WebP archive stores decoded frames at generation resolution before MP4 compression; raw uncompressed files are unnecessary. Playback and audio still use the MP4. Archives increase storage and initial transfer time, while later keyframe requests transfer selected pictures and audio only. Existing captured output policies, retries and One more remain unchanged. **Regenerate reel** starts from the source reel's choice and can turn **Keep lossless frames** on or off for the new reels; requests captured before output policies always keep them.

Validated segments and frame indices publish beside immutable reference media, independently of temporary generation folders. Failed transfers or publication retry from the staged result without regenerating. Previously published archive identities cannot be overwritten by different frames.

Analysis is lazy and cached by immutable source, archive/video representation and selector version. It samples eight low-resolution candidates per second across the clip and scores perceptual difference, sharpness and temporal coverage, discounting blank frames, flashes, fades and transitions. Selection is deterministic, with timestamp ties. Archived frames are preferred; older/imported/MP4-only reels use decoded video frames, never claimed as recovered originals.

Exact extraction uses decoded frame indices and presentation timestamps, preserves variable frame timing, normalizes pixel aspect ratio and rotation, and outputs PNGs. Selected extraction is batched and cached, with at most two frame workers and cancellation of unfinished work. Archive analysis decodes each segment once. These are local operations, not AI generation.

The shared Core services and media routes serve web and desktop hosts. Metadata is optional and versioned; no migration, production reset or sampling-policy change is involved. Automatic picks are suggestions, not a guarantee of useful coverage or spatial consistency.
