# Continuity: lessons from *The Open Window*

What finishing one film by hand showed about keeping AI-generated shots consistent. It is the evidence for a continuity sheet, automated checks and the overseer design. Written 2026-11-06.

**Status: nothing proposed here is built.** An agent did the work through the normal UI, with Playwright scripts and read-only queries of `project.sqlite`, and the director approved each batch of renders and watched each cut. UI problems met along the way are in [UI notes](film-ui-notes.md).

**Limits of the evidence:**

- One film, with one main room and one light per scene.
- Quick takes only (608×352). The 1 MP refine has not been run.
- The agent reviewed still frames. It didn't judge motion or sound, and the director caught most of what mattered by watching.
- Several findings rest on a single shot, or on a retake that changed more than one thing. These say so.

## Summary

- **Each shot's prompt describes the world on its own, so descriptions drift.** Identity, costume, light, furniture and seats all disagreed between shots, and with the chosen images.
- **Retakes fell sharply once the staging was written down.** With the room order and seats fixed, prompts agreeing with them and frames from earlier shots, wave three needed new attempts on only 2 of 30 shots. The wave doesn't separate those effects.
- **Shot frames hold a scene together, and they hold everything else in them too.** A frame carries a seat, a prop's state and sometimes its own composition into the new shot.
- **The hardest errors are between shots.** A shot can match every reference and still be wrong next to its neighbours: people in the wrong place, looking the wrong way, or doing something the next shot contradicts.
- **The director's viewing can't be replaced yet.** All 17 notes on rough cuts 2 to 6 were things the agent's frame review had not fixed, and most of them could be seen in stills.
- **Tracing a note is a good job for an agent.** That means mapping a timecode to a shot, finding the cause, editing the prompt and listing what depends on it.

## The result

| | |
| --- | --- |
| Length | 59 shots, 8 scenes, 6:48 |
| Rough cuts | 7 |
| Takes in the project | 302, a median of 5 per shot and 17 on #55 |
| Shots rendered more than once | 54 of 59 |
| Shots using shot frames | 43, with 47 frames in all |
| Two Quick takes of one shot | About 3 minutes (median of the 90 runs that were timed, 4.6 hours in total) |
| Director's notes per cut | Cut 2: 6. Cut 3: 7. Cut 4: 2. Cut 5: 1. Cut 6: 1. Cut 7: none given yet |

The director's comment on cut 4 was "I can almost not find faults anymore". Cut 7 hasn't been approved or rejected: the refine was set aside before it had notes.

### How the work went

| Round | Started by | Shots | Method |
| --- | --- | --- | --- |
| 1 | Frame review of the first cut | 17 | Prompts only |
| 2 | Viewing the corrected sequence | 5 | Prompts only |
| Frame test | Director's question about shot frames | #17, #20 | Full frames and crops compared |
| Wave 1 | Voices and seats | 5 wides | Voices, corrected prompts, first frames |
| Wave 2 | Each camera side's first shot | 11 | Frames from the wides |
| Wave 3 | The remaining shots | 30 | Frames from waves 1 and 2 |
| Wave 4 | Notes on cut 2 | 12 | Room picture corrected with H3, frames in dependency order |
| Wave 5 | Notes on cut 3 | 16 | Staging across scenes, costume, trims |
| Wave 6 | Notes on cut 4 | 2 | One view of the room for #55 and #58 |
| Wave 7 | Notes on cuts 5 and 6 | #43, twice | Hand-over from #42 and to #44 |

## Why it drifted

| What drifted | Example |
| --- | --- |
| Identity | Mr. Sappleton has a moustache and a heavy build in #42, #43 and #55; his reference is lean and clean-shaven. Vera is 15 on her sheet, "about nineteen" in #53 |
| Costume | Mrs. Sappleton in dark clothes (#34); Vera in a pale dress (#59) |
| Light and time | Golden daylight between dusk shots (#44, #45, #49–51, #53, #54) |
| Room state | A lit fire appears in #58 and #59 only |
| Geography | A gravel drive right outside the window (#51); the house visible through its own window (#42) |
| Relationships | The brothers called Mr. Sappleton's "sons" (#55) |
| Script errors | A stage direction written as a spoken line (#4, #28) |
| Seats | Framton on the sofa in scene 3, the right armchair in scenes 4–5, back on the sofa in scene 6, with no move shown |
| Voices | No voice references, so H3 invented each character's voice per shot |

Two things in the composition data may add to this, though neither was tested. `environmentStaging` was empty on all 59 shots, so nothing placed people. `PromptComposer` asks each shot not to repeat the previous shot's "blocking emphasis", which may encourage movement between shots.

## What was learned

### Stage the room before writing prompts

- **Fix one order along the far wall and use it everywhere.** Here it was door, chest and lamp, floral armchair, window with round table and boots, carved chair, fireplace. Wave two needed one retake in eleven shots once prompts agreed with this, although the wave didn't separate this effect from the frames'.
- **State which side the window is on for each camera side.** "Frame right" in a prompt moved #17's window to the right in both takes. The reference hadn't lost; the prompt had overruled it.
- **Views the room picture doesn't show are hard to get.** "From the fireplace side" kept the near-end view and moved Vera to the chair it could see. "Behind her chair" failed the same way. Staging inside the picture's own view worked at once (#55, #58).
- **A prompt's camera can make a story beat impossible.** #40 asked Vera to stare out of a window behind her while facing the camera.
- **A few words about furniture can beat a frame.** "The armchair's arm" gave Vera a second chair in #19 while hers stood empty. Naming the carved chair fixed both new takes.
- **A window stays a window only if something says so.** With the lawn at their feet, men standing in the opening made it read as a door. A low panel under the sill solved it, and the men climb over it once, in #49.

### Say who is present at this moment

- **Each prompt needs the scene's state at that moment, including people out of frame or in the distance.** #44 was fine until it followed #43 with the men at the window, and then showed an empty room.
- **Leaving someone out can show their seat empty.** #35 never mentioned Vera, so her chair was empty in the wide part of the shot. Her picture and one sentence fixed it.
- **Eyelines need the same care as seats.** The men in #55 looked towards the camera while Vera sat by the window. Putting all of them in one view solved it.
- **Costume needs the whole figure.** Vera's reference is waist-up. Removing the boots revealed bare knees, which needed a skirt length, stockings and shoes on the sheet.

### Hand over between shots

- **A shot must end where the next begins, in both position and action.** Copying #42's positions fixed the opening of #43, but #43 still ended with the men coming in, while #44 and #45 have them outside. That took a second retake.
- **Check each take between its neighbours.** This found more than any other agent check. #53 and #54 each look fine alone and only read as wrong in sequence.
- **Re-check the sequence after retakes, because fixing one shot exposes the next.** Round 2 only existed because the corrected sequence was viewed as a whole.

### Shot frames

- **Frames add to the references and replace none of them.** References hold identity and the set. A frame carries what differs from them, such as who sits where. The most useful source is usually the last shot from the same side, not the previous shot.
- **A frame holds a seat firmly enough to beat the prompt.** #29's prompt moved Vera, and both takes put her back where the #14 frame had her. A character who has moved needs another frame.
- **A frame also sets where a seat is.** A #49 frame with Vera at frame left put her armchair in the foreground of #53. A frame from earlier in the same take fixed it.
- **A frame carries every prop in it.** A frame from #59 fixed Vera's seat in #55 but put the coat on the sofa too early. A frame from #49 didn't. Removing a frame can also lose a detail: #55's white coat came out dark until it was named and given a picture.
- **A frame can become the opening image.** #16 opened on the #14 wide, and #40 opened on the #38 wide and rushed in. An environment picture can do it too: one #52 take opened on the afternoon room in a dusk scene. Trimming fixed these.
- **A frame can't fix what the prompt asks for or what happens during a move.** Room drift during #20's camera arc and an oversized pipe that the prompt requested both stayed.
- **Crops weren't clearly better than full frames.** On #17 and #20 they looked about equally good, though Vera's crop gave the clearest match on the chair. That test shared one room and one light, where a full frame competes least.
- **Order matters when frames chain.** #9 before #7 before #11, and #31 before #33, #34 and #37.

### References

- **Some errors start in the chosen reference.** The room picture had three pairs of boots, so no wording would give one pair. A review that treats references as the truth passes every shot that matches them.
- **A corrected reference doesn't reach what was made from it.** The room's reel, the old picture and frames from earlier takes all keep the boots. A correction needs a list of what it affects.
- **Multi-panel references need a stated panel.** Without one, H3 drifted to the living room sheet's golden panel. A crop per state would be sturdier than a sentence.

### Prompt wording

- **Some errors are simply requested.** "Two pairs" of boots in #6, a gate that "swings across behind his profile" in #47, men standing behind her in #57.
- **Saying what something isn't can backfire.** "Not a top hat" gave a top hat in both #45 takes, while a frame of the hat from #44 fixed it. #44 itself came out right with the same words, so this is one shot's evidence.
- **Description can outweigh camera instructions.** "Almost imperceptible" pushes became real zooms in #14 and #19. #23 held still once the prompt said the camera doesn't move and dropped the close detail about her face. Several things changed at once, so this is a lead rather than a result.
- **Keyword rules aren't enough.** "Golden" appears harmlessly in "the golden panel is not used".

### Takes, trims and image edits

- **Two takes per version earned their cost.** A take could be wrong in one way only: short curls on Vera in #26, a duplicate coat in #59, the wrong room at the start of #52. The second take usually gave a usable choice.
- **Trimming can fix what prompting doesn't.** #47 kept a gatepost that is in the exterior picture until its first second was cut.
- **H3 image edits remove objects far better than they add them.** Asked to keep one pair of boots, it removed too many or too few, but the rest of the room stayed unchanged. Added boots came out at the wrong scale twice, even with wording about size. What worked was removing one pair at a time from a take, keeping an original pair.

### Voices

- **A missing voice can only be fixed by rendering the shot again**, so it has to be caught first. The reference editor warns about it, but only inside the editor, and all 59 shots went to render without one. The director heard it on first viewing; no frame check could have found it.

## What caught what

| Check | Needs | Found |
| --- | --- | --- |
| Plain data checks | No model | #56 had no references; #57 over the refine limit; the cut ignoring 46 new selections |
| Prompt against the sheet | Text model | Most identity, costume, room-state and geography errors, and errors the prompt asks for |
| Frames against the sheet | Vision model | #16 hair, #25 giant hand, #15 oversized pipe, #47 too young |
| First and last frame of each take, scene by scene | Vision model or a person | #45's top hat |
| Neighbouring shots together | Vision model or a person | The moustache, #53, #54 |
| Watching the cut with sound | A person | The voices and nearly all the notes below |

"Text model" and "vision model" describe what the check would need. An agent did these reviews by hand on this film, and no self-hosted model has been tried on them.

## The director's notes, as a test set

The overseer design proposes a fixture made from this film's known answers. These are the notes, in order. Times refer to that cut. The last column is a judgment about which check might find it, and none of these checks exists yet.

| Cut | Time | Shot | The director saw | Cause | Might be found by |
| --- | --- | --- | --- | --- | --- |
| 1 | – | All | Voices change between shots | No voice references | Data check |
| 2 | Start | #7, #9, #11 and later wides | Double boots | The room picture has three pairs, and prompts ask for more | Prompt against sheet; a person for the picture |
| 2 | 3:54 | #31, #33, #34, #37 | Table disappears | Mrs. Sappleton stands where it goes, and frames carry that on | Neighbours; frame dependency list |
| 2 | 4:02 | #33 | Extra boots | The prompt opens on boots in the foreground | Prompt against sheet |
| 2 | 5:01 | #40 | Vera and Framton look away from the window | The camera has the window behind them | Blocking check |
| 2 | 6:18 | #55 | Brothers still coming in through the window | They climb in 40 seconds after their father | Neighbours; scene timeline |
| 2 | 6:48 | #59 | Woman in the window | She stands in the middle of it, seen from behind | Director's intent |
| 3 | 0:39 | #6 | Double boots and no table | The prompt asks for "two pairs" | Prompt against sheet |
| 3 | 1:01 | #9 | Vera far away, and the man looks outside the room | #7 ends with Vera close, and a dark strip of wall reads as outside | Neighbours |
| 3 | 1:01 | #14, #15, #20, #29, #35 | Vera keeps wearing boots | The old room picture has a pair at her feet | Superseded reference list |
| 3 | 5:20 | #44, #45 | Framton escapes from an empty room | The prompts leave out Vera and the men | Scene timeline |
| 3 | 5:30 | #47, #48 | Gate swings through him | The prompt asks for it, and he goes through twice | Prompt against sheet; motion needs a viewer |
| 3 | 6:17 | #55, #58 | The window is a door, the siblings are back | They stand in the opening, and the table is missing | Sheet; neighbours |
| 3 | 6:24 | #57 | They jump to stand behind her | Prompt and frame put them there, and the shots around it show no one | Neighbours |
| 4 | 6:15 | #55 | Men look as if she is at the door | Eyelines go to the camera, and she sits by the window | Blocking check |
| 4 | 6:38 | #58 | She is back next to the window | A reversed view put her in the foreground | Blocking check |
| 5 | 5:11 | #43 | Siblings further away and standing differently | #43 doesn't continue #42's positions | Neighbours |
| 6 | 5:21 | #43, #44, #45 | Still outside, but they started walking in | #43 ends on the men coming in | Neighbours; scene timeline |

The agent added its own findings while tracing these: Framton's hat in #44, #45, #47 and #48, the cyclist's direction in #48, a stray boot in #51. Its frame review had raised only one of these problems first, the extra boots, which were known and only partly fixed.

Most of these can be seen in stills. They weren't missed for lack of video. They aren't mismatches against a reference, so a review has to ask a different question: does this make sense after the last shot?

## Proposed feature

The overseer design covers the agent, its tools, approval and the plain checks. This section covers what it would check against.

**1. Continuity sheet.** Built after assets are chosen, from the chosen images and not the script, and editable by the director. A render that departs from the script is accepted by default. This film ended up needing:

- **Cast:** visible face, hair and the whole costume down to the shoes, plus a "never" line for known wrong versions.
- **Each environment:** what stays fixed, what can change, and the order along the walls from one named view.
- **State per scene:** light, what is lit, who is in the room.
- **Seats per scene:** who sits or stands where, including moves within a scene.
- **Camera per side:** for each camera position, what is at frame left and frame right.
- **Prop timeline:** where each prop is and when it moves. Here, the coat stays on the carved chair from #49 until #59, and the hat stays on Framton's head from #44 to #48.

`AssetImage.VisualDescription` has the right shape for the descriptions, but the code marks it retired and nothing reads it.

**2. Prevention.** Give the sheet to prompt composition, so shots reuse one description instead of inventing their own. This should remove most of the prompt check's work, but it wasn't tested, because prompts here were corrected after being written.

**3. Prompt check, before rendering.** Read each prompt against the sheet and the scene's state, and offer a diff. Check that required details are present as well as flagging wrong ones: #44 never mentioned dusk, so there was nothing wrong to flag. Ask one decision per conflict and apply it across the scene or film.

**4. Take check, after rendering.** Compare frames with the sheet and with the shots on either side. Propose a fix and show the new takes between their neighbours. Re-run over the sequence after retakes.

**5. Hand-over check.** For each cut between shots, compare the end of one prompt with the start of the next: where each person is, what they are doing, what the next shot needs them to have done. This is text only and would have addressed several of the later notes, but it hasn't been tried.

**6. Shot frames by default.** Suggest frames when a shot's references are set: one that establishes the scene's seats and light, plus the last shot from the same camera side. Warn when a frame shows a person or prop in a state that doesn't match the sheet at that moment. Render order follows from the frames: wides, then each side's first shot, then the rest.

**Severity:** treat costume, hair, light and presence as serious. Age wording mostly isn't, because the image outweighs it.

**Director's decisions, not fixes.** The sheet can flag these, but only the director can settle them. On this film: Mr. Sappleton, Bertie and Ronnie look alike and resemble Framton, and the Letter image shows a painting.

### Suggested order

1. Plain checks, as in the overseer design's build order. Small, and they would have caught several problems before any render.
2. The sheet, with state, seats and props per scene.
3. The sheet fed into prompt composition.
4. Prompt check and hand-over check, both text only.
5. Suggested shot frames.
6. Take check against neighbours. Largest, and it needs a vision model that can take enough frames per shot.

## Where the film stands

The cut holds Quick takes only, and all 59 clips use the selected takes. The refine was set aside on 2026-11-06.

### Before a refine

The recorder command is in the [demo recorder README](../tools/demo/README.md#finishing-the-film). Its refine steps haven't been rehearsed, so a short test on a few shots should come first.

- **Trims:** #16, #40 and #47 use trimmed takes. The refine dialog says trims are reapplied; this hasn't been checked. #47 is 2.04 s, just above the 2 s minimum.
- **Frames from takes no longer selected:** ten shot frames still point at a source shot's older take, so the refine won't swap them for refined frames:

  | Shot | Frame from |
  | --- | --- |
  | #7 | #9 |
  | #13 | #7 |
  | #16, #18, #23 | #14 |
  | #30 | #29 |
  | #36 | #31 |
  | #50, #53, #59 | #49 |

  None of these shots has an open note, so this is about quality, not a known continuity error. Whether draft-quality frames harm a refine hasn't been tested.
- **Old room picture:** 28 shots still list the three-pair room picture, although their takes passed review. Whether a refine can bring the boots back hasn't been tested, so the test should include some of them.

### Known issues

- **#44 and #45** are noticeably brighter and greener than #42 and #43, so the light jumps at 5:19 and again at #49.
- **#43 into #44:** the men stand close to the sill at the end of #43 and look further back in #44.
- **#46** ends at the gate before #47 and #48, a slight repeat, and has tiny figures near the far gate.
- **#49:** the window looks taller late in the shot.
- **#57:** the carved chair shows no coat for the first 2 s.
- **#38, #41:** the old boots may be visible, though dusk hides them.
- **#55, #58:** similar framing, with the men in profile.
- **#59:** the closing line is quieter than the rest.
- **#43:** Ronnie's lip sync hasn't been checked.
- **Vera's legs:** #15 still shows knees, and in wides the dark stockings may read as boots. An ankle-length skirt would mean retaking about nine shots.

### Open questions for the director

- #13 to #14 jumps from standing to seated.
- Mrs. Sappleton is absent from #55.
- The light jumps between scenes 5 and 6.
- The three men and Framton look alike.
- The Letter image shows a painting, not a letter.
