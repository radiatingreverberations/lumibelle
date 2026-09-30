# Unified Shots

Shots is the workspace for coverage, prompt writing, references and take review. `/projects/{id}/production` redirects to `/projects/{id}/shots`, including shot, setup and job parameters. Cut playback and film export are outside this implementation pass.

## Author workflow

1. Draft or edit coverage, including silent cast members and exact dialogue. Use the Shot tab for coverage; description and dialogue are not repeated in Prompt or Takes. The three tabs remain visible in the pane header. Sound, music and approved-source context are secondary disclosures.
2. Use the compact Setup menu to create, duplicate, rename, archive or switch alternatives. One empty Default setup is created per shot. Shot duplication copies coverage; setup duplication copies production inputs. Shot options holds reorder, duplicate and delete.
3. Choose numbered images, crops and ordering in References. Assets, Shots and reel recipes share the same gallery, selected-reference rows, crop dialog and ordering controls. Use the crop button directly, or Reference settings for shot use hints and preservation guidance. Replacement preserves the reference slot. Optional AI use hints guide composition; they do not impose independent video constraints. Voice recordings retain speaker mappings and excerpt selection.

   Reels default to [keyframe pictures with separate audio](h3/reel-keyframes.md). **Manage references** resolves ordinary pictures first, then each reel's selected frames, and exposes Full reel or audio-only use without rewriting prompt text. Existing full-reel attachments are unchanged.
4. Write directly in the prompt editor, or provide Direction for AI and Compose prompt. The model sees the ordered cropped PNGs, current coverage, approved scene context, nearby shots, asset guidance, voice mappings, duration and aspect. It receives voice mappings, not an assertion that recordings were listened to. ComfyUI models compose in two steps: a visual brief written from the images, then a text-only composition from that brief (see [development notes](development.md#comfyui-text)).
5. A valid initial composition applies automatically only while its empty target and inputs remain unchanged. Existing-text revisions require Apply changes or Discard. AI explanation is collapsed by default and identifies an explanation attached to an earlier AI composition after manual editing.
6. Generate takes flushes browser edits, saves, validates the generation inputs and captures the displayed prompt unchanged. Prompt-template deviations are advisory, including headings, dialogue wording/format, reference labels and duration wording. **Mark reviewed** in the prompt dialog footer records a manual review and clears an attached failed request from the toolbar; review is not required to generate. The outline's prompt badge opens this dialog directly. Missing scene, direction, or duration has its own action to focus the relevant Shot field and does not prevent prompt review. Shot or reference changes show Check prompt without replacing text.
7. Review the newest-first shot-wide gallery, optionally filter by setup, play a take and choose Use this take. Batch IDs and other technical details stay in disclosures. Retry, One more take, refinement and frame saving remain available.

Clear prompt removes text, attached proposal/error and explanation. It retains Direction for AI, references and settings, and can be undone. Clarifications use a fresh Compose/Revise request with current inputs; transport/save retries retain the captured request.

Retry composition request is available only when the saved job can check an accepted request or recover its output. A stopped request that needs fresh generation offers New composition request, which opens the composer with the current Direction for AI and references; submission remains explicit. Retry errors stay on the page without disconnecting it. Codex reconnect notifications marked `willRetry` remain progress on the accepted turn, while terminal errors still fail the request.

Voice mappings in `subject_definitions` accept the assigned speaker and stable label on either side of the audio identifier: both `Riley (S1) uses <Audio 1>` and `<Audio 1> supplies Riley (S1)'s voice` are valid. A blank `needsInput` field alongside a complete prompt is treated as an ordinary response rather than a clarification request; only a non-blank question asks for input. Complete saved responses rejected by an older validator offer **Recover response as draft**, preserving the exact returned text even when it differs from the recommended template. Review notes stay visible without blocking acceptance. Recovery does not add missing sections, submit another AI request or change the saved response. Recovery retains the existing guards against overwriting newer prompts or changed references.

The center defaults to Shot when there is no remembered view. Creating or drafting shots opens Shot; switching existing shots retains the active view and starts at the top. Tab switches retain drafts, prompt Undo and per-tab scroll without remounting the editor. Explicit `view=Shot`, `view=Prompt` and `view=Takes` links override the remembered view. References and Generation remain available for the selected setup in every view, including through drawers on narrow screens.

In Takes, use **Select multiple**, choose the takes, then **Move to shot…**. Moving changes their library owner together; original prompts, references, seeds, batch identities and media stay intact. A moved production selection is cleared on the source shot, and the destination's selected take is preserved. Existing Cut clips keep pointing to the same video. Regenerating or refining a moved take saves the new version under its current shot while using the captured inputs; comparisons use that shot's current prompt and references. To reverse a move, select the takes and move them back.

## Text fidelity and concurrency

The prompt uses a separate Tiptap/ProseMirror schema alongside the Script editor. Section, dialogue and reference styling uses decorations; persistence and submission remain plain text. Exact text changes presentation of the same document. Picture identifiers open the selected crop. Paste, Unicode, blank lines and literal reference tags are preserved.

Prompt Undo and Redo retain each shot's editing history when closing and reopening the prompt dialog or switching shots. Their buttons are disabled when no corresponding edit is available. History lasts for the current Shots workspace visit; leaving the workspace or reloading starts a new history. Undo saves the restored text and updates take input-change badges.

Each setup has its own optimistic version. It persists production inputs, not another editable copy of coverage. Coverage is hydrated into a transient video-engine adapter when loaded. The shared result-application service compares request intent, prompt, setup version, active request and current context/image hashes under the project lock. Cancellation, a replaced request or changed inputs prevent automatic application. Results remain inspectable. Applied request IDs make repeated completion delivery idempotent, including worker recovery after restart.

Pending browser text is protected from background replacement. Generate and Compose flush acknowledged edits before capture. A generation snapshot records the exact prompt, setup revision, ordered cropped images/audio, hashes and settings. Retries and One more take use that immutable snapshot. Composition-aware generation bypasses legacy character/look assignment validation. Prompt wording is advisory; unavailable media, invalid generation settings and captured-input identity mismatches remain blocking. Blank drafts can be saved, but generation requires nonempty text within the editor limit. Operations that parse dialogue for dubbing still require the structure they consume.

## Cancelled take recovery

Live video progress uses execution order: the first take to start rendering is **1/2**, even if ComfyUI chose the second internal branch first. This order is checkpointed across page reloads and worker recovery. Internal candidate identities stay stable for saving outputs; the separate **takes saved** count reflects local publication.

Cancelling a video batch stops its owned ComfyUI request, then retrieves any fully rendered takes from that request's saved output history. Takes can finish in any order. Only complete output sets required by the captured settings are imported; unfinished sampling or incomplete archives are not presented as completed takes. The batch remains cancelled, and unstarted or interrupted candidates are never resubmitted.

Cancelled batches offer **Recover completed takes** in batch review and AI activity. This retries retrieval from the original server and prompt, including for older cancelled batches. Saved candidate identities and publication receipts prevent duplicate takes. A transfer failure keeps saved takes and offers another retrieval attempt. If ComfyUI no longer has the files or history, unfinished work cannot be recovered this way.

## Experimental reset

Production schema version 2 intentionally resets previous setups, prompts, revisions, generated take records, take selections, production queue history and dependent Cut clips. Scripts, assets, project settings, shot IDs, coverage and planning provenance remain. Dialogue speakers formerly implicit in cast are materialized as cast entries. Media and operation files remain on disk; file cleanup is outside scope.

The reset refuses to run with active production jobs. It writes the schema marker last so interrupted initialization can safely repeat. Shot deletion/recovery and setup archiving account for active requests. New activity links target the unified workspace with shot, setup, job and Prompt/Takes selection.

## Validation

```powershell
dotnet test tests/Lumibelle.Tests -c Release
$env:LUMIBELLE_BROWSER_CONFIGURATION='Release'
npx playwright test tests/browser/unified-shots.spec.js
```

Isolated browser fixtures use mock text/video providers and real stores. They exercise composition/revision, manual editing, immutable captures, take selection, setup independence, coverage planning and recovery, navigation, concurrent drafts, and desktop/narrow layouts. Mock success does not establish live video quality.
