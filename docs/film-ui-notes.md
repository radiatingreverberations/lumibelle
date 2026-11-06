# UI notes from finishing *The Open Window*

Problems met while using the app to finish a 59-shot film, written 2026-11-06. These aren't an audit. Each one came up during the work, and those found while scripting the UI rather than using it by hand are marked. The order within each section is rough priority, by judgment. See [Continuity lessons](continuity-lessons.md) for the film itself.

## Likely bugs

- **A silent character with a default voice blocks Apply.** Adding Mrs. Sappleton to #49, where she has no lines, left her voice as **Choose voice…**, and Apply refused until None was chosen. [Character voices](h3/character-voices.md) says characters without dialogue start with None.
- **"Refresh Video models" can be misleading.** After a server start, **Generate takes** on a shot with a RefMod reel showed a banner saying to refresh the models, and queued nothing. The banner has no button, and about two minutes later the same click queued normally with nothing refreshed. "Still checking" would describe that state better.
- **One shot frame is called the end of the previous shot.** With exactly one frame, `PromptComposer` describes it as a still from the end of the previous shot's chosen take, even when it comes from another shot or moment. Two or more frames get accurate wording.
- **Reel tools can stay hidden.** On one attempt, **Create reel from this image** left the tools pane hidden for at least 30 seconds, and a second attempt worked. Not reproduced, and it may be a timing issue.

## Silent failures

These let a director carry on without knowing something is wrong.

- **Choosing a take in Shots doesn't reach the cut.** After 46 shots were reselected, **Choose takes…** opened with every row on the take already in the cut and **Apply changes (0)**. **Select latest for all shots** picks the newest take, which undoes any choice of an earlier one. A **Use selected takes for all shots** button would solve this, and the Cut studio could show how many clips differ from the selection.
- **A missing voice only warns inside the reference editor.** All 59 shots had no voice and nothing showed it. It should also appear on the shot list and at **Generate takes**, because changing a voice means rendering again.
- **Nothing shows what an updated reference affects.** After the room picture was corrected, every shot and reel kept the old one. That is the right default, but shots can only be swapped by hand, one at a time. Reels already offer **Also replace it in N other shots**.
- **Nothing shows that a frame's source take was replaced.** On this film ten shot frames point at takes that are no longer selected. The reference editor shows a hint per shot, but there's no overview.
- **The refine limit isn't checked early.** #57 at 14.5 s rounds up to 15.083 s, over the limit, and nothing warned until refine would have rejected it.
- **Stage directions become spoken lines.** #28's dialogue had `*(quietly, to himself)*` as its own line, and composition put it in the prompt as speech. #4 had the same error.
- **Leftover JSON can reach a prompt.** #38's saved prompt had raw JSON and "placeholder" sound sections, and it was marked reviewed and rendered. The prompt review already flags repeated sections, so it could flag these too.

## Shot frames

- **Choosing the source shot is slow.** Shots come from a dropdown of titles, so you can't see a frame until you select its shot. A filmstrip of the scene's selected takes would be much faster.
- **The default source is the nearest earlier shot.** In dialogue that cuts back and forth, the useful frame is usually the last shot from the same side. Same cast or same framing would be a better default.
- **Choosing a frame is slider-only.** **Choose frame** has no number field or step buttons, so landing on an exact frame is fiddly. Found while scripting.
- **Removing and re-adding a frame renumbers pictures.** The new frame goes last, which silently changes what `<Picture 4>` and `<Picture 5>` mean in the prompt. The picker's replace keeps the slot, but it's easy to miss.
- **Shot frames have no crop.** `ShotContinuityFrame` has no crop, and the picker refuses a frame that is already attached, so several crops of one frame would need that rule changed too. A crop on the record would follow the refined frame, while a cropped frame saved to Assets stays at draft quality.

## Comparing takes

- **There's no side-by-side with neighbouring shots.** Viewing each take between the end of the previous shot and the start of the next caught more than any other agent check, and those sheets had to be built outside the app.
- **There's no way to label a take.** Testing versions of a shot means remembering which was which, or reading its snapshot. A short note would do, ideally defaulting to what changed since the previous take.

## Editing images

- **Edited takes join the gallery straight away.** New images wait in **Pending review**, but these edits count among the asset's images at once. Trying nine versions took the room from 2 images to 13 (plus the reel), and it isn't clear which is meant to be used.
- **Edit image forgets the workflow.** After an H3 edit, **Edit image** on one of its takes went back to Krea 2 with Takes at 1. Editing a result again usually means keeping the workflow that produced it.

## References and assets

- **The picker hides part of the library behind Show more.** Adding Vera to #44 took several presses. Assets already used in the scene could be listed first. The search box helps if you know the name.
- **Imported images have no name of their own.** Six imports all appear under the asset's name, told apart only by a hash in the tooltip. Keeping the file name would help. Found while scripting; a person would see the thumbnails.
- **A picked card's state isn't exposed.** Once an image is added, its card stays `aria-pressed="false"`, and only a class and the "✓ Added" badge change. Found in the markup; it affects screen readers and scripts.
- **Clicking a picture chip in the prompt opens a preview.** A click meant to place the cursor can land on one. Found while scripting.

## Voice reference reels

- **The refusal says where you already are.** "Choose a voice or None for Mrs. Sappleton in Manage references" appears inside Manage references, while the voice row is on the Voices tab. The message could open that tab.
- **There's no path from a reel to a default voice.** Making a voice usable in shots takes **Save voice…**, **Make default voice**, then a choice in each shot. A shot explicitly set to None keeps None, so a new default doesn't reach it.
- **A single reel can't use the preset's own text.** The Voice reference preset has a fixed prompt builder (`ReferenceReels.Preset`), but only bulk reels call it, and they force Silent. A single reel needs **Compose pair**, which requires a vision model, or hand-written text. A **Use preset text** button would remove that need.
- **Settings are spread across the panel and dialog.** Framing and voice are in the dialog under "AI assist", although they also matter without AI. Seconds is on the panel behind it, and the suggested passage depends on it, so you close the dialog to change the duration.
- **Voice settings start collapsed.** Choosing the Voice reference framing leaves **Voice and dialogue** closed, although it's now the main thing to fill in.

## Wording

- **Three names for one thing.** The picker says **Shot frames**. Assets, `ReferenceCopyPanel` ("Continuity frame") and the composer ("continuity picture") say continuity.
- **Edit or Manage references.** The Shots button says **Edit references…** and the dialog it opens is titled **Manage references**. The manual uses both, and reels use **Manage references…** for the button too.
- **Asset kind wording.** The create dialog offers plurals ("References"), while the asset's badge is singular ("Reference").
