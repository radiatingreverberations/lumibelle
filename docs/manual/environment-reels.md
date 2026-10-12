# Environment reference reels

An environment reel generates missing reference angles from a location image in one continuous journey. Save useful frames as asset images or select keyframes for later shots, rather than generating every angle separately and trying to reconcile different layouts.

In Shots, **Environment** chooses useful views from the whole environment library. AI selection also proposes a **Staging interpretation**: positions and facing, movement, camera view, assumptions and coverage concerns. Review and edit it before **Apply views**. For example, a defendant at the Noxian plaza podium should stand on its usable side and face across it toward the judge; a side view should support that blocking and the route toward the passage. This is an interpretation of the scene, not proof that the reel reconstructed the plaza correctly.

The reviewed staging and reasons for the selected views travel into **Compose prompt**, using the final Picture/Video numbering. They stay separate from reusable asset and keyframe notes. Changing the action, directions, library or selected pictures/crops marks the staging for review; Compose does not reuse stale blocking. Copying references to another shot does not copy its blocking. Existing saved selections remain inspectable, and you can author staging when choosing views manually.

Open a location image’s review/details dialog and choose **Create reel from this image** in its footer. This opens a fresh reel recipe with that image selected and preserves any earlier recipe. The same action is available for character and prop images. You can also choose **Reference reel** from the Assets tools’ **Create** selector.

## Choose useful coverage

These top-down diagrams illustrate movement, not a reconstructed floor plan. Lines and arrows show camera travel; shaded cones show viewing direction; numbered circles mark held views. Circuit view 4 returns to the starting position. Clockwise and counterclockwise are measured from above.

### Full set — 15 seconds

![Camera travels around the set perimeter, with four wide inward-facing views.](../../src/Lumibelle.UI/wwwroot/guides/environment-reels/full-set-clockwise.svg)

Use for a room, courtyard or other acting area. Travel around its outside while looking inward across the staging space and backgrounds. Keep enough distance for wide shooting positions, rather than orbiting tightly around one prop or turning outward from the centre. Full set is the default. Optionally name the acting area in Instructions; otherwise it is inferred from references and asset notes.

### Feature study — 15 seconds

![Camera completes an inward-facing orbit of one feature and its immediate surroundings.](../../src/Lumibelle.UI/wwwroot/guides/environment-reels/feature-study-clockwise.svg)

Name a feature in Instructions. Complete a 360° orbit while keeping the whole feature and its immediate surroundings framed, holding four angles. This establishes that feature, not the entire environment.

### Walkthrough — 15 seconds

![Example camera route through connected spaces with a hold at the destination.](../../src/Lumibelle.UI/wwwroot/guides/environment-reels/walkthrough.svg)

Use for a street, corridor or connected rooms. Describe the route and destination, for example: “From the gate, along the arcade, to the courtroom door.” Continuous progress reveals how the spaces join; the final hold provides a sharp destination view. The diagram is an example, not a prescribed route.

### Custom — 10 seconds

![Example authored camera route; Instructions determine the actual movement.](../../src/Lumibelle.UI/wwwroot/guides/environment-reels/custom.svg)

Describe the starting view, route and destination, including views to hold. Your Instructions determine the movement and pace. Aim to complete the intended reference coverage within this one reel.

## Example: the Noxian plaza

A wide entrance image shows the defendant’s podium, raised judicial bench, gate and fortress skyline. For **Full set**, maintain that broad scale and travel around the plaza’s edges, looking across the acting area. The podium is one landmark within the set, rather than something that fills every view.

For **Feature study**, name “the defendant’s podium and its steps” to establish the smaller area’s other faces and immediate backgrounds. For **Walkthrough**, name a connected route such as “from the gate, along the plaza’s edge, to the bench steps.”

## Using the library in a shot

The environment library serves two different decisions. Before **Draft shots** or **Draft shot**, use **Choose environment views** to save a compact inspection set across its stills and reels. These pictures help the director understand acting areas, obstructions, sightlines and routes, together with authored staging constraints. **Use for drafting** does not attach them to generation inputs. Reuse the saved setup without repeating AI selection. A scene strategy's environment assessment records what those exact pictures support and what still needs another view; it is not verified geometry. See [environment-aware drafting](shots.md#inspect-an-environment-before-drafting).

After reviewing the coverage, choose the smaller set needed to generate one shot using **Environment** in that shot's References area. The inspection set and generation set can differ: a scene may need several views to explain its set, while a static shot often needs only one or two pictures. Changing the future drafting setup does not silently rewrite accepted direction or its historical evidence.

Save useful keyframes with view names and notes, then click **Environment** in the shot setup’s References area. In that dialog, choose the owning **Environment** asset and use **Choose views with AI**, or **Browse views** for manual selection. The assistant inspects its images and saved views from all its reels, including their crops, and proposes the smallest useful set for the action and framing. Your scene and neighbouring-shot context settings also apply to this selection. Use environment saves only the library choice. Apply views saves the library and reviewed references together; cancelling changes neither. A camera recipe describes intended coverage; it does not prove the reel’s geometry.

Review thumbnails, source reels, timestamps and concerns before **Apply views**. This saves the references and closes the dialog without composing a prompt. Finish your character, prop and other references using **Edit references**, then choose **Compose prompt** separately. Compose uses the attached viewpoints without selecting them again. You can deselect views, choose replacements from the captured library, or explicitly remove older environment references. Character, prop and continuity references remain in the displayed budget. Selected pictures are the default: one or a few useful views usually suffice for a shot, and each uses a picture slot without consuming a reel slot. The reel camera's motion is not sent with those pictures. **RefMod** needs at least two selected frames from each chosen reel, uses a reel slot, and uses the usual preparation process.

Large libraries are inspected in bounded batches and shortlisted for comparison rather than silently truncated. Missing files, unkeyframed reels, conflicting designs or insufficient capacity are reported. Retry with another image-capable composition model or select manually when the evidence is inadequate. Saved selection requests and responses remain inspectable; changing the setup or sources requires a fresh review.

## References and Instructions

Select your clearest location image and any additional views of the same place. Crops, asset notes and reference guidance define recognizable features and physical relationships. Use the source composition when it suits the route; otherwise allow an opening appropriate to the coverage.

Unseen areas are newly designed by the video model, not a verified reconstruction. Additional references constrain them. All presets preserve stationary scenery and lighting, use clear camera routes and produce silent visual references. Review authored directions so they describe one coherent plan.

Requested duration is adjusted to the model’s frame grid. Shorter reels leave less time to travel and settle. Keep the area or route manageable within the 15-second limit, and review the resulting coverage before relying on it.

## Review and reuse

1. Review the composed prompt and use guidance, then generate the reel when ready.
2. Watch the entire reel. Check fixed landmark placement, connected passages and coverage of all intended sides without crossing scenery. A matching beginning and ending alone is insufficient.
3. Open **Manage keyframes** and choose **Suggest views** for AI assistance. It inspects a shortlist of distinct, relatively sharp frames, available original images and the reel's saved instructions. Add selection instructions when needed, such as “include the podium, judge's platform and reverse toward the gate.” Review the proposed names, notes and coverage warnings; edit or uncheck individual views. **Add selected views** preserves existing picks, crops and notes; **Replace picks with selected views** replaces the pending list. **Undo picks** restores the previous selection. Use **Save keyframes** to keep the changes. **More selection tools → Quick picks** offers a fast local fallback without AI labels. Individual frames can also be saved as independent asset images with their selected view name and notes.
4. Use those images or selected keyframes as shot references. Direct video references use only the beginning of a reel, up to the target shot’s length; that may omit most of a full circuit.
5. Correct use guidance to describe the environment the result actually establishes. Later shots choose their own camera movement, action and dialogue.

View names describe actual visible landmarks and facing directions, not guaranteed angles from the planned route. They and frame notes travel into shot references and Compose in both **Keyframes** and **RefMod** modes. Keyframes use individually numbered picture inputs; RefMod packs two to nine selected still views into one video-reference input and is built automatically as needed. Choose the useful views first, then the delivery mode. Neither mode requires exporting each frame as a separate asset image. Failed AI responses remain inspectable; **Try again / change model** opens a new request without overwriting picks.

Reels without saved viewpoints show **Needs keyframes** in the gallery and details. Select the badge to choose views; after saving it shows the keyframe count. This status describes viewpoint coverage, not whether the clip can be played or used as a direct video reference.

**Views → Automatic** lets AI choose enough complementary views for coverage, up to nine. A numbered limit is a ceiling, not a request to fill every slot. AI suggestions and local **Quick picks** share the same sharpness and diversity analysis; AI additionally interprets what each view shows. Selection notes focus on the suggested views and important coverage gaps. The full candidate contact sheet remains under **Request and response**. Saved responses keep their original wording; request a new suggestion to use updated guidance.

Saved clips and captured recipes remain unchanged when preset choices change. An unfinished recipe using a removed preset asks you to choose a replacement; its references, crops and authored prompts are retained.

## Direction and reference support

Shot direction separates dramatic purpose from proposed positions and camera placement. View selection considers that purpose, the accepted scene strategy and current neighbouring shots. It may adapt the proposed staging to the pictures actually available. Its **reference-supported staging** records claims against captured candidate identities, media hashes and crops, with supported, inferred, missing or conflicting status. A model's interpretation is evidence for review, not verified geometry. Camera recipes remain intended coverage, not proof of the layout.

Changing direction, strategy, references or crops can make this staging need review. Compose receives applicable direction and limitations, preserves the story's intent and qualifies unsupported spatial claims. Inspect conflicts or missing angles rather than asking an environment image to prove an arrangement it does not show. See [reviewable shot direction](shots.md#reviewable-direction).
