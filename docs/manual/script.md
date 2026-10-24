# Script studio

Adapting an existing story? Start in [Source](source.md) to analyze the original material in sections and review an adaptation outline before writing scenes.

Open a project's **Script** tab to write directly or describe a draft in **Instructions**. **Suggest outline** creates headings and action beats; **Discuss idea** stays exploratory. Script supplies screenplay formatting and editing guidance, without an Idea brief, default duration, genre, tone or H3 profile. Production direction belongs in [Shots](shots.md).

## Structure

The screenplay is one continuous document. Scene and optional act headings create the outline. **+ Scene** inserts after the selected scene or at the end of its act. **+ Act** inserts after the selected act and its contents; with no selection, both append.

Each outline row has a drag handle and a menu for insertion, rename, **Move to…** and deletion. Move scenes across acts or move an entire act with its contents. **Start new act here** groups a scene and subsequent scenes before the next act. New headings are revealed and selected for naming. Deleting an act that contains scenes confirms its scene count and remains recoverable.

## Writing

| Shortcut | Action |
| --- | --- |
| **Ctrl/⌘ + Alt + 1–7** | Element type |
| **Ctrl/⌘ + B / I** | Bold / italic |
| **Ctrl/⌘ + S** | Save |

Enter after a scene heading creates action, after a character creates dialogue, and after dialogue creates a character. **Split scene at cursor** is in the scene menu. Structural edits and proposal application form one Undo step.

**Save** stays visible and is gray when the browser matches the saved script, including after Undo. Autosave preserves newer keystrokes during storage writes. The **Script** menu contains **Export Markdown** and **Recovery**.

## Assistant

Script keeps a dedicated **Assistant** panel with action, scope, Instructions, attached discussions and compact model options. **Requests** opens discussion and proposal history. On narrow screens the panel uses a drawer. Manual editing, outlining, saving and export work without a model.

**Revise** is a focused edit to **Whole script**, **Current scene**, **Current act** or **Selected passage**. Unmentioned text, order and emphasis are preserved. **Rewrite selected scope** explicitly permits a broad replacement. **Continue** inserts after the target.

The current script, selected target, Instructions and explicitly attached references and discussions enter new requests. Attached exchanges are removable. **Request details** shows the saved source revision, model and exact input.

**References → Manage references** uses the same thumbnail browser and selected-reference controls as Shots. Browse **Images**, **Shots**, or **Asset details**, reorder or remove selections, then **Apply changes**. Cancel keeps the previous selection. **Refresh saved details** reloads project descriptions for review.

Images send the actual selected pictures and their asset, look and preservation notes to the chosen vision model (up to nine images). Shots and asset details send saved text; their thumbnails are browsing aids. A model that cannot read images requires switching models or removing the image references before submission. Each queued request captures its images, so later library changes do not alter its input. References inform a proposal; the script changes only when you apply the reviewed result.

## Using a Source outline

Accept an adaptation outline in [Source](source.md), then open Script. **Use accepted outline**
attaches its reviewed structure and direction to writing requests. For sequence drafting,
choose an **Act**, then a **Sequence**. Changing acts selects its first sequence not yet
added to the script. Progress counts sequences; one sequence may produce several
screenplay scenes. For other writing actions, choose **Whole outline** under **Outline context**
for context or focus on a planned sequence to include its saved notes and original source passages.
The complete novel is not attached to every request. Uncheck the option to write independently.

**Draft sequence** proposes one selected sequence as an addition at the end of the script.
It is the default for an empty screenplay with an accepted outline, including when you
turn **Use accepted outline** back on. Deliberately chosen discussion and editing actions
remain available. Applying a sequence selects the next undrafted sequence; reopening an active
request restores the sequence being drafted.
The request carries the whole plan, linked source evidence, current screenplay headings,
and a bounded excerpt of recent writing for continuity. Review the result before applying it.

**Redraft sequence** creates a fresh alternative for a sequence already linked to the script.
Added sequences carry a **✓** in the sequence picker. Selecting one automatically switches
to **Redraft sequence**; selecting an unadded sequence switches back to **Draft sequence**.
Only the relevant sequence action appears in the action menu. A complete act
selects an added sequence for redrafting. Model availability and other prerequisites appear
beside the action button when a new request cannot start.
Choose the sequence, update the instructions or model, and review the
comparison. **Replace sequence** replaces only that sequence at its existing position. Earlier
and later writing and existing act headings stay in place; the previous version is saved in
**Recovery**. Discarding the proposal leaves the script unchanged. The request uses the source
passages and surrounding screenplay continuity, excluding the old sequence draft so it does
not steer the fresh version. Substantial changes may require later continuity edits; these
are not applied automatically. Moved, shared or deleted sequence boundaries must be resolved
before replacement. If the sequence changes while a request runs, review the current version
again before applying. Use **Revise** for focused corrections that should retain existing prose.

Applying an outline sequence adds the accepted act heading automatically, including when the
model omits it. Later sequences in the same act keep that heading. Existing act headings and
manual title edits are preserved. For linked sequences drafted before this support was added,
**Add outline acts to script** adds missing headings with a recovery copy of the previous draft.
New vLLM requests use three stages with the selected model: prepare a compact sequence brief,
write from that brief, then automatically review the screenplay. The
writer can check the brief's paraphrases against the original wording and physical details.
The whole adaptation outline stays in the planning step. The brief describes permitted audience
knowledge in spoiler-free states, which are supplied to the writer alongside indexed required beats.
The writer receives the selected reviewed sequence summary as its scope authority,
alongside the advisory brief and original passages. Linked passages can overlap neighbouring
units, so that reviewed summary controls where the draft stops. A shortened brief must not
remove its required aftermath. Review should still check reveal order and source facts.
Progress identifies planning, writing, checking and any repair. **Sequence brief** in the response review shows the saved beats,
continuity constraints, audience knowledge and ending. The captured stages remain inspectable in
**Request details**. During the first step, the review window is titled **Review sequence brief**;
it contains planning rather than screenplay blocks. Missing fields, malformed JSON or an unfinished response stop before drafting; interrupted requests do not automatically start another inference request.
Planning review shows saved notes as numbered beats with the ending and continuity alongside
them. **Response details** and **Request details** expand the original JSON, failure message
and captured input. Planning uses the same response frame, **Paste / edit JSON** editor and
**Review corrected JSON** controls as screenplay responses. A corrected brief is saved separately;
the original response remains accessible. Reviewing JSON sends no model request.

A complete brief that exceeds recommended size or concision limits shows specific warnings.
**Use brief and continue** records approval of that exact brief and starts writing without
repeating planning. It checks the captured model's context window first. A later JSON correction
needs its own approval. **Retry planning** starts a fresh request for the same sequence;
**Adjust request** reopens its instructions and controls without submitting. The footer remains
available while the notes scroll.

The brief also carries exact story details such as names and code phrases. The writer
uses those details literally and refers to missing specifics generically. Audience-knowledge
states describe what is established at this point rather than spelling out a hidden answer.
The planner can also select short source excerpts for important dialogue, interpretation
and physical continuity. The app checks each quotation against its cited original section
before passing it to the writer. Unverifiable excerpts are omitted with a notice in
**Sequence brief**; optional excerpts longer than 800 characters are also omitted without
rejecting an otherwise usable brief. The writer still receives the selected original passages,
and the original model response remains available. These checks establish
where a quotation came from, not whether the model interpreted it correctly.
Planning aims for up to twenty beats. Modest overruns are retained, up to thirty-two beats.
Larger complete briefs can be explicitly approved within the model's available context. The planning
instructions preserve the causal and emotional progression through the middle and aftermath,
use original passages directly when available instead of repeating analysis notes, and keep prior preparations separate from
current events. The source's tone is preserved unless the author requests a change. Adaptation
direction and writing instructions control how directly events are depicted; concise planning
notes do not force events offscreen. An offscreen narrator specifies voiceover, not a restriction
on what the screenplay shows. Character choices, relationships, chronology and outcomes must remain
faithful to the source or be identified as deliberate adaptation choices.
For OpenAI-compatible sequence drafts, an unset reply limit scales with the linked source volume from
8,192 to 16,384 tokens, leaving room for several screenplay scenes and their aftermath.
An explicit model or server-profile reply limit is
preserved. Planning captures its own reply budget, scaled from 8,192 to 16,384 tokens
according to source volume and bounded by the chosen reply limit. This is generation
headroom; the brief still consists of concise, separate event summaries. **Request details**
shows the planning reply limit. Historical captures keep their original budgets, including
the earlier 4,096-token planning cap.
The planner receives original passages, the accepted outline and sequence progress.
The writer and reviewer receive the actual screenplay for continuity. New vLLM planning
grammars use only the supplied source-section identifiers
for optional citations. Complete saved or manually corrected briefs can still be reviewed
and approved with size warnings.
While a step streams, live cards show the arriving beats, screenplay scenes or review
findings. The current card updates as text arrives and is marked **Still writing**.
These are unvalidated previews; the original JSON remains in **Response details** and
the normal completion and review checks still run before applying.
New vLLM sequence requests capture JSON grammars for the brief and screenplay stages,
alongside their schema descriptions. Separator whitespace is bounded, allowing normal indentation,
while spaces and Unicode within the prose remain unchanged. Existing captured requests keep their original format. Excessive
separator whitespace stops a response with its partial output retained; it never retries inference automatically.
The writer returns grouped scenes with separate headings. The app creates actual Scene blocks,
so headings do not depend on the model selecting the right block kind. Scenes name the required
beat IDs they dramatize, including **END** for the required ending and aftermath.

**Automatic review** checks each required beat and ending against cited draft evidence. It also
checks source facts, continuity, permitted audience knowledge, scope, duplication and playable
screenplay form while respecting the author's adaptation and narration choices. The app verifies
that cited quotations exist at their numbered draft blocks and source sections. Style suggestions
are notes; material contradictions or missing events require correction. A malformed review gets
one evidence-correction attempt. A draft with material findings gets one targeted repair followed
by another review. Unresolved findings leave the draft saved and pause a batch; they are not
silently accepted. The original draft, reviews, repair and captured requests remain available.
An automatic pass is model assessment supported by quotations, not proof of interpretation or
creative quality. Manual sequence proposals still require the author's apply decision.
The app supplies the reviewer with the exact beat checklist and numbered screenplay passages.
It verifies quotations against those passages before using a finding to request changes. A
malformed review gets one correction attempt; it does not immediately trigger a screenplay rewrite.

Saved requests retain their captured profile: older two-step jobs keep their original array
format and do not acquire additional model calls. Review/repair output is checkpointed; restart
recovery reuses completed stages and never initiates an unfinished model call. Explicit retry can
reuse the saved initial draft and completed checks. The context-window preflight also reserves
space for review and repair rather than truncating source passages.
An interrupted review offers **Continue review**. It keeps the saved screenplay and completes
only the unfinished checks or repair. Reopening the result alone does not start generation.
If the review response cannot be validated, the dialog explains the cause separately from
screenplay findings. **Recheck saved review** validates the saved draft and review again using
the current validator, without another AI call. Redundant citations and unmatched extra citations
are warnings when verified evidence remains; a covered beat still requires a verified quotation.
**Review draft manually** opens a separate proposal for the author's review and normal **Apply
changes** decision. It does not claim automatic approval or replace the original saved request.
Applied sequence links persist with the screenplay and project exports; proposals that have only
been generated do not count as added sequences. Existing writing is not matched to a plan
automatically, so select where to pick up when continuing an earlier draft.

Outline changes require **Refresh source outline** before submitting a new request. Already
queued requests retain the exact accepted outline and passages they captured. A sequence already
linked to existing writing uses **Redraft sequence** for replacement or **Revise** for focused edits.

**Draft remaining sequences** fills the unstarted remainder of the accepted outline as a
background batch. It skips the already added prefix, then writes one sequence at a time in
outline order. New vLLM sequences pass automatic review before being saved directly in the main screenplay; the batch does not
wait in a proposal modal. Existing writing is kept. A recovery copy is saved before the first
addition, and individual requests remain in **Requests** with their captured inputs and output.
This action adds new sequences; redrafting existing writing still requires review.

The batch captures the model, settings, instructions, references and outline revision when
started. Each next sequence uses the saved screenplay for continuity. **Pause after this sequence**
finishes and saves the current sequence, then waits. **Resume batch** continues from its checkpoint;
**Stop batch** cancels unfinished work and keeps the sequences already added. You can leave the tab
for overnight processing, but Lumibelle and the model server must remain running.

Invalid or interrupted requests pause the batch. **Retry and continue** explicitly retries the
stopped sequence; completed sequences are not regenerated. Editing the saved script while a
request runs pauses automatic addition rather than overwriting those edits. Resume drafts that
sequence again with the new saved context. If the accepted outline changes, stop the batch and
review existing writing before batching the new outline. Batching does not automatically match
writing linked to an older outline revision. Earlier gaps must be resolved before appending later
sequences. Unsaved editor changes are kept if background additions arrive; use the normal
save-conflict and reload controls to reconcile them.

If a batch pauses during planning, its response offers the same JSON editor and brief warnings.
Continuity, withheld information and story details have no fixed entry-count limits; these lists
follow the selected sequence. Overall brief size guidance and model context checks still apply.
**Use brief and resume batch** continues the saved request after approval, preserving completed
sequences. Changing a batch's captured model or author instructions requires stopping it and
starting another batch.

## Reviewing proposals

**Apply changes / Discard** controls reviewed results. Writing remains available while AI runs. If the captured target changes, the main button becomes **Review with current script**: it opens a comparison using the saved draft without calling the model, then becomes **Apply changes**. Focused edits instead offer **Request fresh changes**, since those edits depend on the original blocks. Invalid, cancelled or truncated responses remain inspectable and never overwrite writing. **Paste / edit JSON** saves a separate corrected proposal for review without calling a model.

A focused response with no edits says **No changes proposed** and offers **New request** rather than applying unchanged writing. For a local correction, select the scene or passage and use that smaller scope.

When revising source-linked writing, the assistant selects the source sequence linked to the target scene or passage. It switches away from the next sequence waiting to be drafted. You can deliberately choose another **Outline context** after selecting the target.

Explicit voiceover/offscreen speaker cues and speaker names already established by those
cues or Character blocks can be read as Character/Dialogue pairs. A format notice identifies
this normalization and the original response stays available. Ambiguous block types remain invalid.

## From script to production

Saved scripts are the source for asset extraction, shot drafting and scene assignments. These operations capture a snapshot of the saved script without a publishing or approval step. Existing assets and shots retain their captured source; editing the script does not rewrite production.
