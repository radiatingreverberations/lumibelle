# Source

Use the **Source** tab before Script to adapt an existing story, treatment, or other text.
Paste the original material or import a UTF-8 text/Markdown file, add a title and adaptation
direction, and **Save source**. The original text remains separate from the screenplay.

The workspace follows the other editors: navigation is on the left, the manuscript or
selected outline item is in the center, and the assistant is on the right. The source title
stays above the manuscript while the text scrolls inside its editor. Adaptation direction,
model selection, analysis progress, and review actions live in the assistant sidebar.
Choose an act or scene to edit it in the center; **Back to original** returns to the same
position in the manuscript. The left sidebar shows source sections when reading the
original, and acts and scenes when reviewing the adaptation outline. Section numbers
identify chunks created for analysis, not chapter numbers; choose a section to read its
captured passage and analysis notes. The fixed **Source** and **Outline** tabs at the top
of the left sidebar switch between the two views. Outline explains the planning workflow
before a proposal exists; both tabs stay visible while the navigation list scrolls.
On narrow screens, **Show Source** (or **Show Outline**) and **Show Assistant**
open the corresponding sidebar as a drawer.

**Analyze source** saves current edits and starts a sequence of requests using the selected
text model. Natural chapter and paragraph boundaries guide splitting; long sections are
split further to fit the model's estimated context budget. Each section produces compact
notes. When all notes cannot fit in one planning request, further requests combine them
in groups before proposing an outline for the whole story. Chapters do not prescribe acts.
Progress distinguishes **Read source**, **Build story overview**, and **Propose outline**.
Finishing all source sections completes only the reading step. During combining, the
sidebar shows how many notes have been combined in the current pass, the current batch,
and how many summaries have been saved. A long source can need several combining passes
before the outline request fits; the final outline step shows live request progress.
The summary length is a writing target; longer valid notes are retained. If a previous
section's note would crowd out the next source section, only its orientation excerpt is
shortened. The full note remains available for review and whole-story planning.
Unknown context windows use a conservative fallback; a provider can still reject a request.
The planning budget uses the advertised context window, reserving room for the reply and
up to 32,000 estimated input tokens. When the full notes fit, the outline is proposed
directly without an extra combining pass. Resuming rechecks the model's current capacity
and keeps the existing section numbers and completed notes.

The analysis runs in the background and survives navigation. **Pause after this request**
lets the current request finish and saves its result. **Resume analysis** continues from
completed notes. Failed or interrupted requests pause the sequence for an explicit retry;
it never automatically repeats a failed model call. AI activity contains the individual requests.
While paused, use the model picker to choose a different model and then **Resume analysis**.
Completed notes and source links are kept; the remaining requests use the selected model.
Wait for any current request to finish before switching. The replacement must have enough
context for the remaining source sections. Previous requests retain their original model
and settings in AI activity.

Review the proposed acts and scenes, edit their titles and descriptions, and change their
order. Source links open exact original passages captured for that analysis. Section notes
remain available alongside the outline. Missing source coverage and identical scene summaries
produce review warnings, not automatic rejection. These checks do not establish complete
narrative fidelity: review the original passages when details matter.
Outline-wide notices appear on **Overview**. Its source coverage notice links directly
to passages without scenes. If the material belongs in the adaptation, add its number
to the relevant scene's **Source sections**. Otherwise choose **Mark intentionally omitted**
and **Save review edits**. The decision clears that coverage reminder for this outline;
expand **Intentionally omitted sections** and choose **Review again** to undo it.
Scenes that combine source sections around material used in later scenes also get a
continuity warning. This can be intentional, so it does not block saving. In **Edit outline**,
correct each scene's **Source sections** using comma-separated section numbers. Invalid or
empty links must be corrected before saving; links always address that analysis's captured text.

**Save review edits** keeps a working plan. **Accept outline** marks the reviewed plan as
accepted for later scene drafting; it does not write screenplay blocks. **Discard outline**
retains its history. Changing the saved source or adaptation direction marks older analyses
as out of date and prevents accepting them. Each analysis retains its original source snapshot.

**New outline from saved analysis** creates another proposal from a completed run's notes
and story overview, using the current direction and selected model. The original text must
be unchanged. It keeps the earlier proposal and review edits in history; those edits are
not inputs to the new proposal. **View original analysis** opens the run that supplied the
notes and its saved requests. A smaller model may need another combining pass before planning.
When the complete section notes fit the selected model, they are used directly; otherwise
the saved story overview is reused.
Use **Analyze source** when the source text changed or you want a fresh reading.

Project exports include source material, completed notes, and outline reviews. Unfinished
analyses are transferred paused, without live queue submissions; resuming explicitly starts
the remaining work with the configured provider.
