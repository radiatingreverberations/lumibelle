# Shot language versions

## Author workflow

Configure **Project settings → Languages and dubs** with an explicit master language, up to 16 distinct dub languages, and optional translation notes/glossary. Codes such as `en-GB`, `sv`, `zh-CN`, and `pt-BR` are accepted (stored lowercase); the language name is used in H3 dialogue instructions. Existing projects have no assumed master language. Configuring languages never translates existing script/shot content. Newly added manual dialogue rows use the configured master-language name; existing rows retain their authored language.

Render a master shot in Shots using its composed H3 prompt. Open **Language versions** from the Shots header or from that shot's Dialogue section. The workspace lists master takes with dialogue on the left, with each take's saved version and rendered takes for the chosen dub language; the centre holds the dialogue editor and **Render <language> take**, and the right pane holds translation assistance. Choose the rendered master take and target language. Either enter each translated line manually, or select a text model/profile and request **Suggest translated dialogue**. The AI receives text only: master prompt, source dialogue, scene excerpt, language pair, duration and notes. No image, video, voice, or RefMod data is sent to this translation request.

Inspect the suggestion and any timing/translation concerns. **Use suggestion in editor** stages it; **Save language version** persists it. Edits in the line editor become manual revisions rather than falsely attributing modified text to the AI response. The master script, master shot coverage, composed master prompt, media and selected take remain unchanged. An AI response arriving after a competing save cannot overwrite the newer version.

**Render <language> take** queues one separate H3 video/audio generation. It copies the source request's prepared input files, verifies their hashes, starts with the source take's seed, and retains the original frame count, dimensions, models, settings and ComfyUI server. It does not reopen mutable asset images to substitute new references. Missing source requests, unavailable master takes, missing or changed captured input files, stale language preferences, or a changed variant version stop capture. The queue's normal pause/retain, retry, priority and cancellation behavior remains in force. Using the existing One more action afterward follows its normal fresh-seed behavior; the Language versions render button itself starts again from the master seed.

This is a new video/audio render, not audio replacement, lip-sync processing, or audio-only dubbing. Identical input references and seed do not guarantee identical frames, motion, accent, pronunciation or speech timing. Models and referenced voices may not work in every configured language. Review generated results.

## What changes in a prompt

The H3 master prompt must already pass its normal structural/dialogue validation. The translator returns only:

```json
{"version":1,"lines":[{"id":"original-dialogue-uuid","text":"Translated speech"}],"notes":[]}
```

IDs must match every source dialogue line exactly once and in order. Unknown/duplicate fields, missing or reordered lines, markup, line breaks and incomplete responses are rejected. Speaker identities cannot be reassigned. Notes are for human review, not executable prompt additions.

The application replaces only existing `<d>[language] ...</d>` blocks. All surrounding prompt bytes—including subject definitions, `(S1)` speaker labels, `<Picture N>`, `<Video N>`, `<Audio N>`, camera instructions, timing directions, effects, music and visible lettering—are retained. Dialogue IDs and speakers are unchanged in the captured shot; only each line's language and text change. If the master has contradictory language instructions outside `<d>` blocks, review those in the master before choosing a source take; this feature does not rewrite them.

## Takes and cuts

Dub takes carry optional `VideoSnapshot.Dub` provenance with source take, source fingerprint, language pair and saved translation version. Take labels and resolution badges include the target language. **Shots → Takes → Language** offers All languages, Master, or individual dub languages.

In Cut's **Choose takes → Latest** controls, Language defaults to Master; newer dubs cannot accidentally replace the master through bulk latest selection. Selecting a dub language chooses only that language's takes, plus master shots that have no authored dialogue. A take with authored dialogue in a different language is not substituted when a dub is missing. No authored dialogue does not prove an audio track is silent; review incidental speech and narration as well.

Bulk selection still stages a proposal and uses the existing Apply/Undo flow. A shot without a matching take is absent from the proposal: existing unselected cut clips remain unchanged. Consequently, applying a partial dub selection does not prove that the entire cut is in the target language. Inspect every clip before MP4 export. This version does not create separate saved cut timelines per language.

Normal resolution regeneration of a dubbed take is disabled with a pointer back to Language versions. Render its saved translation again there, or render a new master at another resolution and translate from that master. This keeps the same-inputs/geometry contract explicit.

## Persistence, recovery and scope

Project metadata lives in `languages.json` and `dubbing.json`. Variant identity is scoped to a master take and target language; saving increments a version. Existing take snapshots and AI request history retain their captured text. Current language variants are not live views over a subsequently edited script or a newer master render: select that newer master explicitly for a new version. There is no batch translation/render of the entire episode in this implementation.

Queued translation responses remain in AI activity; Review opens the exact master/target context. Generated takes are ordinary Video jobs with additional immutable dub provenance. Closing a page does not cancel accepted work. A failed enqueue acknowledgement keeps the exact pending submission for Retry rather than generating another job ID.

Language settings and translation drafts are not yet supported by the portable **project-package ZIP** schema. Export of a project with such data is explicitly blocked rather than silently dropping it. Existing projects without language metadata remain exportable; ordinary **MP4 cut export is unaffected**. Back up the full application-data directory and any separately configured projects directory, including the AI job store and captured `shots/runs` input files. Do not delete these inputs merely because the master video finished.

There is no UI localization, subtitle/SRT export, automatic time stretching, voice conversion, or automatic replacement of voices for different languages. Those are separate operations from translating and rerendering a shot.

## Verification

Automated application tests: `ShotDubbingTests` and `ShotDubbingStoreTests`. They cover strict dialogue contracts, Unicode, speaker/order preservation, unchanged-reference fingerprints, optional legacy serialization, language revisions/conflicts, variant save idempotence, stale settings, removed source takes, prepared pixel/voice file copies and tamper rejection, master/dub latest selection and safe portable-export refusal.

Manual checks:

1. Start an existing project without language preferences; Script and Shots remain unchanged. Configure a master and two dubs. Add a new manual dialogue line and verify its default language; verify existing dialogue is unchanged.
2. Choose a master with multiple ordered dialogue lines, a cropped image, an audio excerpt and a reel reference. Generate and review one text-only translation. Compare the two prompts outside `<d>` blocks and check every reference label.
3. Save, reopen, manually revise, and render the language version. Compare captured input hashes, source IDs, crop/excerpt choices, settings, frame count and seed. Confirm the master selected take is not changed.
4. Test a translation arriving after a second tab saved newer dialogue or changed the relevant language/glossary. Saving the stale response must fail and retain the local editor text.
5. Pause and retain an active dubbed render, resume, cancel, and retry on a disconnected provider. Existing queue safeguards must behave as for any Video request; no language metadata is lost.
6. Remove a captured input or alter its bytes. Render capture must fail before queueing. Restore it and try again. Do not substitute newer assets automatically.
7. Verify take labels, language filtering, master latest selection, dubbed latest selection, silent-shot reuse, missing-dub exclusion, staged Cut application/Undo and ordinary MP4 export.
8. Try portable project export with language metadata; verify the explicit data-preservation message. Verify a project without it remains exportable.
