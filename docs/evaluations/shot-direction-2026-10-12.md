# Shot direction comparison — 2026-10-12

[Blinded A/B packet](shot-direction-2026-10-12/blinded-review.md) · [Arm key](shot-direction-2026-10-12/review-key.json) · [Metrics](shot-direction-2026-10-12/metrics.json) · [Frozen source corpus](shot-direction-2026-10-12/corpus.json) · [File hashes](shot-direction-2026-10-12/manifest.json)

## Method and limits

Ten frozen scenes: the opening Noxian podium excerpt from the saved SparkleRiley screenplay, plus nine authored scenes. Each received one baseline and one updated **Auto** request: **20 actual text requests**, with no evaluation/judging calls or retries. Sources, empty authored instructions, maximum 15 seconds, model selection and provider settings were identical between arms. Baseline messages were frozen before modifying the drafter; both message sets, the original planner, raw responses and parsed proposals are included beside this report. Responses were never applied to a live project. No media generation was queued.

The current project drafting selection was **Claude Code / opus (Opus)**. This is a provider alias; a resolved model build and token usage were not returned. Requests used the existing text-generation options, with no added sampling override. Two requests ran concurrently; baseline ran before updated, so order and service load can affect latency. The later UI refinement requesting an explicit `approach` explanation was added after this comparison; no further requests were made. The comparison tests the substantive direction/strategy contract, not that final explanation wording.

The packet presents identical coverage fields for both arms and omits metadata that would immediately identify the updated arm. It is suitable for a separate blinded human review, though wording can still reveal the treatment. The following assessment is the implementing agent's **unblinded qualitative review of the actual plans**, not an independent or statistically powered score. It assesses planning, not video quality.

## Technical reliability

- 20/20 provider calls completed, and 20/20 responses parsed without fatal errors.
- Both arms covered the captured dialogue without parser-reported missing lines or dialogue differences. All clips respected the 15-second maximum.
- Baseline: 53 shots, 254 seconds total planned coverage. Updated: 43 shots, 236.5 seconds. Reduced shot count alone is not a quality metric.
- Median request latency: baseline 23.21 seconds; updated 43.15 seconds. Supplemental strategy/direction costs response length and time.
- Baseline reconciliation descriptions produced three advisory “Cut to” wording notes; the updated arm produced none. These were review notes, not rejected outputs.
- Usage/cost was unavailable from the CLI provider. No totals are inferred.

## Directing quality

| Dimension | Observed result and limit |
| --- | --- |
| Dramatic clarity | Usually comparable, with clearer declared intent in the updated strategy. The accusation still reveals the youngest councillor's suspicious look before the collective rejection. No evidence that metadata alone improves the performed result. |
| Shot necessity | Updated plans combine exchanges or related actions in eight scenes; no fixed three-shot target. Noxian drops 10→7, physical comedy 4→3 and reconciliation 5→4 without parser-reported dialogue loss. |
| Visual progression | Updated reconciliation moves from the empty gap, to the cup between them, to joined hands, then shared outward attention. Sustained dialogue holds on the younger's response during off-screen reassurance instead of mechanically reversing. |
| Spatial coherence | Mixed. Pursuit explicitly establishes left-to-right travel, but both arms leave the boat-to-north-bank transition unshown. Updated reconciliation invents that the sisters finish “seated close,” despite no move in the source. |
| Editorial motivation | Strongest gain: more cuts follow changes in attention or knowledge. Updated Noxian holds the charge/deflection exchange, and the council's turning and Mara remaining upright share one wide shot. Some inserts still interrupt an otherwise effective held performance. |
| Generation feasibility | Fewer pans, drifts and cranes often simplify clips. However, duration is still estimated, the six-second tray catch packs several actions together, and geometry remains proposed. This is not proof that the video model can execute any plan. |

## Paired scene findings

| Scene | Concrete gain | Regression or unresolved concern |
| --- | --- | --- |
| Noxian podium | “Top Lane” keeps the concession, second charge and deflection in a nine-second shared composition; “No Exit” holds the chair gag laterally. Baseline repeatedly adds gentle pushes or drifting movement. | The final high view behind the magistrate needs a real supporting environment angle. Several intent reasons still mention a particular angle despite the requested intent/staging separation. |
| Ensemble accusation | The collective turn and Mara's refusal to sit stay together in the final wide, replacing a separate isolation shot. | “Your Seal” introduces a decorative rack focus. The youngest's close-up loses the baseline's visible Len in the background, weakening the visual relationship that motivates suspicion. |
| Delayed comic reaction | A static profile two-shot protects the routine question and innocent answer while the fish is already visible to the audience. | Cutting to the stamp before tilting to her face may interrupt the delayed reaction. A held performance could be stronger than either plan's close reframing. |
| Physical comedy | The wide first shot retains the door, tray tilt and head catch in one visible cause-and-effect gag. | Six seconds may be tight for approach, door reveal, catch and hold. The handle breaking remains a difficult multi-object interaction. |
| Pursuit geography | Explicit left-to-right lateral travel and a still final over-shoulder view reduce ambiguity and replace the baseline's substantial camera arc. | Neither plan shows how the courier gets off the boat onto the north bank. The script itself leaves this transition implicit; direction must flag it rather than claim complete geography. |
| Concealed recognition | Ada's answer and concealment of her own ticket share a wider single, making her action the consequence of the line. | Both use a scar insert followed by a reaction. Cuff movement and the exact scar reveal need continuity and reference review. |
| Reconciliation | Both dialogue lines remain together with the repaired cup between the sisters; the cut to hand-taking marks an actual relationship change. | The final description moves them “close” without a scripted move. Earlier shared framing was already clear in the baseline. |
| Prop handoff | A balanced opening establishes the key between both people. The hand insert preserves right-to-left ownership and pocket continuity. | Four shots remain in both arms; updated intent does not establish a decisive improvement. Tracking the hand into the pocket still risks an awkward crop. |
| Interruption | The whistle and the daughter's refusal to let it end the conversation play in one shot, avoiding a redundant kettle setup. | A slight push still appears without much need. The final chair's exact relation to the father needs reference evidence. |
| Sustained dialogue | The older's reassurance is heard off-screen while the younger's reaction remains visible; the edit follows emotional information rather than speaker changes. | The updated ending explicitly withholds what is under the cover, an added interpretation not required by the source. The long spoken counsel still needs performance timing review. |

## Decision

The updated plans provide more economical coverage and clearer editorial reasons in this small sample. They also expose useful assumptions for review, but do not solve missing geography or prevent invented staging. Keep the reviewable strategy/direction design and reference-support qualification. Treat fewer shots as a tendency, not a target. A separate human can use the blinded packet to challenge this assessment; no independent human scores are claimed.

## Implementation validation

Final unit suite: 3,415 passed, 18 opt-in tests skipped. Workflow browser suite: 24 passed across drafting, saved reviews, cancellation, stale targets and environment selection/composition. Five additional direction-layout checks passed after field styling, including narrow dark-theme review. Providers in browser tests were mocked and their project storage was isolated; these establish workflow reliability, not directing quality. The real-model comparison above remains separate.
