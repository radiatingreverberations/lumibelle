# Environment-aware drafting: four-request pilot

Exactly four real Claude Code **Opus** drafting requests ran on 2026-10-12. There were no AI selection, retry or judging calls, no applied evaluation drafts, and no media-generation jobs. This is a bounded qualitative comparison of drafting with and without pictures, not an evaluation of the picker, Compose or generated video.

## Frozen comparison

Both arms use the new version 5 drafting instructions and response contract, the same Noxian podium excerpt, Auto, a 15-second clip maximum, and the same authored scale constraint. Within each pair only `imageManifest`, `InspectionImageHashes` and image attachments change. Neither arm receives an image-derived assessment. Candidate names are neutral, notes are empty, and asset guidance is empty. Original reel metadata is preserved in the captured request object for provenance; its descriptive recipe is **not** included in the provider messages.

The full condition uses saved reel frames 0, 132 and 237. The deliberately incomplete condition uses only frame 0. The missing relationship was recorded in [preflight.md](preflight.md) before submission: the route from Riley's standing side of the podium to an open passage, and a usable behind-podium reaction view. Neither the withheld pictures nor descriptions revealing them are supplied to the incomplete pair.

The author tells both arms that Riley is shorter than the podium on its existing base, and forbids shrinking the podium, enlarging her or inventing a platform. Preserving scale is therefore a regression check, not evidence that vision discovered scale.

The frozen selection is an `opus` CLI alias, not a pinned provider revision. No explicit profile temperature, output-token limit or seed is set. Global named reasoning effort was not independently captured at submission; local settings readback after the run was unset. Usage was unavailable from this provider. Those are reproducibility limits.

## Technical reliability

| Request | Pictures | Shots | Planned seconds | Request seconds | Provider/coverage failure |
| --- | ---: | ---: | ---: | ---: | --- |
| Full, text only | 0 | 7 | 47 | 110 | None |
| Full, pictures | 3 | 7 | 48 | 109 | None |
| Incomplete, text only | 0 | 7 | 46 | 102 | None |
| Incomplete, pictures | 1 | 6 | 42 | 93 | None |

All individual clips are within the maximum. All four retain exact dialogue according to the existing checks. Local evidence validation accepts only attached candidate identities: text-only results contain no Supported visual claims; incomplete-picture claims cite only frame 0. The paired-input audit in [validation.json](validation.json) checks unchanged textual inputs and attachment hashes.

The initial parser dropped supplemental assessments when the model returned note lists rather than strings. Coverage remained readable, but this was a product defect. The reader now normalizes text lists without discarding the scene strategy. Original `*-text.json` / `*-pictures.json` files preserve the first parsing outcome; `*-parsed.json` files are a local reparse of those exact raw responses. **No further provider calls were made.**

## Directing assessment

This assessment is a manual code-agent review of the four outputs and three pictures, not a blinded human panel or an automated judging call. One sample per condition cannot establish a general quality improvement.

| Check | Finding |
| --- | --- |
| Scale preservation | All four preserve Riley below the podium top and propose no new standing platform. Side or high reaction coverage works around the obstruction. |
| Useful visual grounding | Full pictures identifies the stepped octagonal base, hard shadows, visible archway and skyline. It flags the script's black fortress against the pale stone in the image. Text-only correctly treats the layout as proposed. |
| Necessary reactions | Full pictures uses an eye-level three-quarter view for Riley's wince and withdrawal, then a wide profile for the final height gag. Full text uses side coverage and a high over-shoulder finish. The images do not prove that either proposed camera position is achievable. |
| Missing-view recognition | Incomplete pictures specifically reports the barred gate, lack of an established open passage, missing bench reverse and missing side profile. It does not merely disclaim geometry in general. |
| Qualified creative proposals | Both visual outputs name additional views needed and distinguish authored scale from observed evidence. The incomplete output suggests the right staircase as a passage but explicitly calls it an assumption, not a decision. That proposal still needs author review. |

### Concrete benefits

- Full-picture shot 4 holds on the apparently empty podium while Riley answers from behind it, then shot 5 changes viewpoint to show her reaction. The set obstruction becomes part of the comic coverage instead of being removed.
- The full-picture assessment ties its observed podium/base claim to the three attached pictures, while treating the archway's use as the story's passage as **Inferred**. This is more useful than asserting a route merely because an opening is visible.
- Incomplete pictures identifies why the left gate is unsuitable as the required open passage. The missing-view request names the required route rather than requesting an unspecified reverse.

### Regressions and unresolved choices

- The full-picture assessment says no judicial bench is visible and proposes placing it on the foreground cobbles. The actual opening and final pictures show a prominent raised bannered structure and stairs, plausibly the intended judicial platform. The model has not established whether that structure can serve the authored bench. Its blanket absence statement is too confident; applying the proposed foreground placement could discard the author's intended staging. Review the structure's function and obtain the useful reverse before accepting that arrangement.
- Incomplete pictures also places the bench in the foreground and suggests the staircase for the escape route. These are qualified assumptions, but still weak foundations for generating the scene. The product must retain their qualification downstream.
- Full pictures describes the guard placing Riley in shot 1 and already seated at the opening of shot 2. Text-only explicitly stages him sitting within shot 2. The visual version preserves the story action across the cut, but is less explicit about the seating transition. This is not a missing placement action.
- Full-picture direction calls a profile the only way to show the final height relationship. Full text proposes a high over-shoulder alternative. The visual result is unnecessarily prescriptive despite the contract allowing creative alternatives.
- The incomplete visual plan compresses several responses and the guard's statement into an 11-second shot. It preserves the lines, but needs timing review; fewer shots is not automatically more economical or feasible.

The pilot supports supplying reviewable visual evidence and explicit missing-view requests. It does **not** establish that pictures consistently produce better direction. Both controls already find purposeful obstruction-aware coverage from the authored scale note. Visual input adds practical detail and more specific limitations, while introducing interpretation errors that must remain visible in review.

## Workflow validation

The Release unit suite passed **3,442 tests**, with 19 opt-in tests skipped. Focused checks cover exact attached evidence, cross-batch identity rejection, canceled inspection accounting, multi-scene capacity, override isolation, selective freshness, historical fingerprints and cross-project resource mapping without rewriting captured evidence.

**23 isolated browser checks passed**, covering scene and single-shot review on desktop and narrow screens, pending-review reload, Back/cancel, undo, source-library reuse, preserved generation references and existing shot-picker behavior (bounded pools, explicit RefMod, Picture 10, failures and request inspection). After guarding final source validation against concurrent edits, the four new drafting browser checks passed again. These browser checks use mocked providers and isolated data, separately from the four real pilot requests.

## Artifacts

- [requests.json](requests.json): exact captured requests and provider messages, including PNG bytes.
- [review-packet.md](review-packet.md): compact paired strategies, shot descriptions and assessments, without repeated media snapshots.
- [validation.json](validation.json): deterministic pair, hash, duration, dialogue and evidence checks.
- `full-text.json`, `full-pictures.json`, `incomplete-text.json`, `incomplete-pictures.json`: untouched raw replies, original parsed proposals, elapsed time, errors and available usage.
- Corresponding `*-parsed.json`: proposals after local parser repair.
- `view-0.png`, `view-132.png`, `view-237.png`: exact frozen saved keyframes.

The opt-in harness is `tests/Lumibelle.Tests/EnvironmentDraftingEvaluationTests.cs`. Its `parse` phase does not contact providers. The `run` phase uses an attempted-output marker to avoid accidental retries; these completed outputs must not be removed to run extra comparisons under this pilot.
