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
choose an **Act**, then a **Sequence to draft**. Changing acts selects its first sequence not yet
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
Applying an outline sequence adds the accepted act heading automatically, including when the
model omits it. Later sequences in the same act keep that heading. Existing act headings and
manual title edits are preserved. For linked sequences drafted before this support was added,
**Add outline acts to script** adds missing headings with a recovery copy of the previous draft.
With vLLM, this runs in two steps using the selected model: prepare a compact sequence brief,
then write from that brief, the focused original passages and the screenplay context. The
writer can check the brief's paraphrases against the original wording and physical details.
The whole adaptation outline and the planner's withheld disclosures stay in the planning
step. The writer receives the selected reviewed sequence summary as its scope authority,
alongside the advisory brief and original passages. Linked passages can overlap neighbouring
units, so that reviewed summary controls where the draft stops. A shortened brief must not
remove its required aftermath. Review should still check reveal order and source facts.
Progress identifies each step. **Sequence brief** in the response review shows the saved beats,
continuity constraints, withheld information and ending. Both steps remain inspectable in
**Request details**. An invalid brief stops before drafting; interrupted requests do not
automatically start another inference request.

The brief also carries exact story details such as names and code phrases. The writer
uses those details literally and refers to missing specifics generically. Withheld answers
remain in the reviewable brief; that list is omitted from the writing input.
The planner can also select short source excerpts for important dialogue, interpretation
and physical continuity. The app checks each quotation against its cited original section
before passing it to the writer. Unverifiable excerpts are omitted with a notice in
**Sequence brief**; the original model response remains available. These checks establish
where a quotation came from, not whether the model interpreted it correctly.
Planning aims for up to twenty beats. Modest overruns are retained, up to thirty-two beats,
within the same bounded brief budget. The planning
instructions preserve the causal and emotional progression through the middle and aftermath,
use original passages directly when available instead of repeating analysis notes, and keep prior preparations separate from
current events. The source's tone is preserved unless the author requests a change. Adaptation
direction and writing instructions control how directly events are depicted; concise planning
notes do not force events offscreen. An offscreen narrator specifies voiceover, not a restriction
on what the screenplay shows. Character choices, relationships, chronology and outcomes must remain
faithful to the source or be identified as deliberate adaptation choices.
For vLLM sequence drafts, an unset reply limit scales with the linked source volume from
8,192 to 16,384 tokens, leaving room for several screenplay scenes and their aftermath.
An explicit model or server-profile reply limit is
preserved. Brief preparation uses at most 4,096 reply tokens. Captured requests retain their budgets.
New vLLM sequence requests capture JSON grammars for the brief and screenplay stages,
alongside their schema descriptions. Separator whitespace is bounded, allowing normal indentation,
while spaces and Unicode within the prose remain unchanged. Existing captured requests keep their original format. Excessive
separator whitespace stops a response with its partial output retained; it never retries inference automatically.
The usual validation and review still apply; a valid structure does not establish story fidelity.
Applied sequence links persist with the screenplay and project exports; proposals that have only
been generated do not count as added sequences. Existing writing is not matched to a plan
automatically, so select where to pick up when continuing an earlier draft.

Outline changes require **Refresh source outline** before submitting a new request. Already
queued requests retain the exact accepted outline and passages they captured. A sequence already
linked to existing writing cannot be drafted again through this action; use **Revise** to edit it.

## Reviewing proposals

**Apply changes / Discard** controls reviewed results. Writing remains available while AI runs. If the captured target changes, the main button becomes **Review with current script**: it opens a comparison using the saved draft without calling the model, then becomes **Apply changes**. Focused edits instead offer **Request fresh changes**, since those edits depend on the original blocks. Invalid, cancelled or truncated responses remain inspectable and never overwrite writing. **Paste / edit JSON** saves a separate corrected proposal for review without calling a model.

A focused response with no edits says **No changes proposed** and offers **New request** rather than applying unchanged writing. For a local correction, select the scene or passage and use that smaller scope.

When revising source-linked writing, the assistant selects the source sequence linked to the target scene or passage. It switches away from the next sequence waiting to be drafted. You can deliberately choose another **Outline context** after selecting the target.

Explicit voiceover/offscreen speaker cues and speaker names already established by those
cues or Character blocks can be read as Character/Dialogue pairs. A format notice identifies
this normalization and the original response stays available. Ambiguous block types remain invalid.

## From script to production

Saved scripts are the source for asset extraction, shot drafting and scene assignments. These operations capture a snapshot of the saved script without a publishing or approval step. Existing assets and shots retain their captured source; editing the script does not rewrite production.
