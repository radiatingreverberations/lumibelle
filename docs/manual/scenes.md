# Scenes

**Scenes** prepares each script scene for production: the environment it plays in, the shots it needs and the strategy behind them. Open **Scenes** in the project navigation to prepare a script scene, including scenes without shots. Scenes follow approved-script order and remember your selection. Each page shows the script excerpt, environment setup, accepted direction history and its shots. **Edit in Script** changes the screenplay; shot links open production in [Shots](shots.md).

## Scene setup

Use **Choose environment views** to select an Environment asset and inspect its stills and saved keyframes across all reels. Add **Staging constraints** for authored facts the empty set cannot establish. AI can suggest complementary inspection views, or you can choose them manually. **Save scene setup** saves this reusable selection; **Cancel** discards changes. Unkeyframed reels link to Assets. Full videos are not inspected and no new frames are extracted.

Image capacity is separate from the nine-picture generation budget. The complete drafting request must fit the selected scene, its inspection pictures, screenplay, constraints and the structured reply. If it does not fit, reduce inspection views or choose another model. Drafting remains one request; pictures are never silently dropped. Large picker libraries use bounded inspection and comparison rounds, recording inspected, unavailable and uninspected sources. A stopped scan remains incomplete; manual selection is still available.

## Draft shots

**Draft shots**, at the bottom of the environment sidebar, uses the selected scene's saved setup without repeating view selection. Review its strategy, assessment and shot proposals before adding them. These inspection views explain the set; they are not a storyboard or generation references. New shots inherit the environment association with their generation references empty. In **Shots**, use **Copy…** or **Edit references…** to attach only the pictures needed for that shot. In the reference editor, **Environment** shows scene inspection views first and can browse the environment's other stills and saved reel views. Compact cards keep view notes under **View notes**. Each selected frame uses one picture slot and no reel slot. Nothing is attached automatically.

**Draft shots** writes a concise scene strategy alongside its coverage in the same request. Adding reviewed shots inserts each scene's coverage in saved script order, so redrafting an earlier scene places it before later scenes already in the list. Additional coverage for an existing scene follows its current shots; existing shots keep their relative order. **Direction style** defaults to **Auto — adapt to the scene**; the last submitted style is remembered per project. Auto can blend approaches across dramatic beats, rather than classify a genre. Its chosen approach and short explanation appear in **Scene strategy**. Your instructions override every style preference. Optional television examples illustrate an approach, not a template to imitate.

| Approach | Preference |
| --- | --- |
| Auto | Adapt framing, staging and cuts to each beat and explain the choice. Recommended for most scenes. |
| Ensemble / Classical | Keep relationships visible in shared compositions with clear geography and restrained movement. |
| Performance Comedy | Protect setups, reactions, physical jokes and awkward pauses. |
| Prestige Drama | Develop emotion, power relationships and reveals through changes in proximity and attention. |
| Kinetic Action | Build urgency while preserving objectives, cause and effect, and readable geography. |

Review and edit each **Scene strategy** once above its proposed shots: dramatic progression, visual progression, audience information and spatial anchors. Applying stores a distinct strategy linked to those shots; another breakdown creates a new strategy without rewriting existing shots. **Draft shot** consumes its existing strategy or proposes one for that shot alone. Closing a review preserves pending edits; it does not apply them. Request inspection and retry use the existing drafting controls.

## Environment assessment

Review the **Environment assessment** once beside the scene strategy. It separates authored constraints, reference-supported claims, assumptions, conflicts and **Additional environment view needed**. Expand **Evidence and assumptions**, then a claim, to correct its text, downgrade its support, choose different inspected evidence or remove it. Claims can cite only pictures actually attached to that request; Supported requires at least one. Pending edits survive closing and reopening review; **Response details** keeps the original model output separately. An unidentified or obscured feature is not evidence of its absence. An unseen angle may be proposed, but is not observed geometry. Important reactions and exact story events must survive inconvenient set scale or obstructions; the drafter should not invent a platform to fix its preferred framing.

Applying stores the accepted assessment and exact evidence snapshot separately from the editable setup used by future requests. Later setup changes flag dependent staging, continuity and reference support while preserving intent and the earlier text. Before Compose, inspected sources and environment guidance are checked against Assets. Changed or unavailable evidence excludes the earlier staging and assessment from current context with a review limitation; adding unrelated views does not invalidate it. Changing review messages or applying a selection does not invalidate it. Undo, recovery and copying preserve these associations; project copies remap live resources and retain original historical evidence identities with an explicit resource mapping. Request inspection retains the actual cropped picture bytes.

Each drafted shot keeps its direction, which stays editable in Shots. See [reviewable direction](shots.md#reviewable-direction).
