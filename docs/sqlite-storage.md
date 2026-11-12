# SQLite project storage

Each project folder keeps its documents in one SQLite database, `project.sqlite`, beside
its media. It replaces the per-document JSON files (`shots.json`, `assets.json`,
`production.json` and the rest), which had to be read, validated and copied whole for
every access. A project with a 142 MB `shots.json` took about two seconds to load its
shots and four to select a shot.

## What is stored where

| In `project.sqlite` | Kind |
| --- | --- |
| Shots, takes, Trash, recovery versions and receipts (formerly `shots.json`) | `shots` |
| Composition settings, shared shot content, earlier setup content (`production.json`) | `production` |
| The cut (`cut.json`) | `cut` |
| Assets, image Trash, reels, voices and receipts (`assets.json`) | `assets` |
| Refinement and asset batches (`refinement-batches.json`, `asset-image-batches.json`) | `refinement_batches`, `asset_batches` |
| The script (`script.json`) | `script` |
| Captured sources, recovery drafts, approved snapshots (`script-sources/`, `script-history/`, `script-approved/`) | `script_snapshots` |
| Source material, script assistant history, script draft batches (`source.json`, `script-assistant.json`, `script-draft-batches.json`) | `source`, `script_assistant`, `script_draft_batches` |
| Language settings, dub drafts, project AI preferences (`languages.json`, `dubbing.json`, `ai-preferences.json`) | `languages`, `dubbing`, `ai_preferences` |

These stay as files:

- `project.json`, the folder's manifest. It is how the library, **Open project folder**
  and project creation recognize a project, and listing projects must not open databases.
- Media, and the small manifests published together with media folders by renaming them:
  `shots/runs/<id>/run.json`, `reference-videos/<id>/media.json`, `frame-archive.json`,
  `lossless-removal.json`, `voice-imports/**/draft.json`, RefMod previews, and the
  operation records of shot and asset copies.
- Application data outside projects: the AI queue and its requests, AI settings, global
  generation presets, and the project routes and locations.

## Layout

A document is split into a header (its scalar properties) and one row per element of each
large list, as compact JSON. The C# models and their validators are unchanged; loads
compose the rows back into the document's JSON and deserialize it.

```sql
documents(kind PRIMARY KEY, stamp, updated_utc, header, lists)
parts(kind, list, id, position, payload, hash, PRIMARY KEY(kind, list, id))
migrations(document PRIMARY KEY, state, source, started_utc, completed_utc, files)
meta(key PRIMARY KEY, value)            -- created_utc, instance
```

- `stamp` counts a document's saves. A save rewrites only rows whose JSON changed, moves
  reordered rows, and deletes removed ones. Saving a shot writes that shot's row, not the
  whole shot list.
- Rows are keyed by the element's id (`id`, `shotId`, `takeId` and so on). A list without
  usable ids falls back to positions.
- `lists` records which lists were arrays, so a list saved as `null` stays `null`.
- **Recovery versions** keep their shots, scene setups and strategies in separate rows.
  Loading the shot document leaves them empty; `LoadRecoveryAsync` reads one when it is
  restored, and `LoadCompleteAsync` reads all of them (package export, reference scans).
  In the 142 MB project they were about 70 MB of the 140. A save that carries a recovery
  version without its shots keeps the stored ones.
- Media requests read the single take or asset row they serve, by key.
- Fingerprints and receipts still hash `AtomicJsonFile.Options` (indented) JSON. Stored
  rows and in-memory copies use `ProjectJson.Compact`; copies no longer indent.

## Connections, durability and errors

Connections are private, short-lived and unpooled, so no handle stays open and project
folders can still be moved, compacted or deleted. The database uses SQLite's default
rollback journal with `synchronous=FULL`, the same durability as the former
flush-then-rename writes, and leaves no `-wal`/`-shm` files beside it. Reads never write:
exporting leaves the file byte-for-byte unchanged, and a read-only folder still opens.

Saves keep the existing rules: the project lock serializes each read-modify-write, the
document's revision check raises the usual conflict, and the save commits in one
transaction.

The database is the only copy of the project's documents. A damaged file is reported with
its path and never deleted or rebuilt. Restore `project.sqlite` from a backup; the
project's files from before the upgrade may also still be in its `legacy-json` folder.

## Cache

Loaded documents are cached per process, by database file and document. Every load first
checks the database's instance id and the document's stamp, so a cached document is never
stale, even after another store instance saved or the file was restored. A hit
deserializes the cached JSON into fresh objects that callers may change. The cache is
limited to 384 MiB, least recently used first.

Production reads only the current shots for each composition's coverage, and the first
setup of each shot takes their parts instead of copying them again.

## Project format and upgrades

`PRAGMA user_version` is the project-wide format. Format 2 requires shots, scripts and AI
preferences at document version 2 and production at version 4. The manifest stays at
schema 1, so listing projects needs only `project.json`. A manifest-only project is
current and creates its database on its first save.

Opening an older project shows **Upgrade project** before any studio mounts. The stores
and project file services also enforce the gate, including cached reads, media access,
export and compaction. Queue cancellation and manifest listing remain available. The
upgrade waits for this project's queued or working AI requests to finish or be cancelled.
Background discovery skips gated projects and continues with other projects.

The upgrader holds the project lock and grants old-format access only to its target
folder in its asynchronous scope. Before writing an existing database it publishes a
SQLite backup snapshot at `legacy-json/project-format-{from}.sqlite`. Snapshots preserve
saved contents. SQLite may change database header counters in the copy. Existing valid
backups are retained on retry. Compact project can remove them after upgrading.

Legacy JSON import is a resumable preparation phase. Each document commits its rows
and migration record, then moves its original files into `legacy-json`. After an
interruption, committed rows stay authoritative and remaining moves resume. Conflicting
archived originals are kept with a `.duplicate` suffix. Format steps then prepare JSON
changes and commit all their documents and the new `user_version` in one transaction.
A failed step rolls back. Earlier completed steps and import work are kept for retry.

Format 1 → 2 preserves takes, selections, Trash, cut clips, recovery records and queue
history when initializing production. Compatible old setup IDs and settings survive.
Unmappable old production content is retained in the database snapshot or original JSON.
Linked-folder setup reconciliation resumes independently after the database commit.

### Adding a format

Add an `IProjectFormatStep` with the next contiguous target number, append it to
`ProjectUpgrader.Steps`, and advance `ProjectFormat.Current`. Use raw JSON and explicit
historical rules in the step. Read headers first and load only documents needing work.
Stage replacements through `ProjectUpgradeContext`; the runner owns the transaction and
version change. Keep shipped steps and `FormatFixtures` fixed, and add a fixture for the
new input format. Exercise direct upgrades, interrupted retries, package import and
historical recovery restoration. Package JSON has its own compatibility checks, which
must evolve with any later change to exported document shapes.

### Restoring before an upgrade

Close Lumibelle and keep a copy of the whole project folder before restoring.

- For a SQLite-only project, replace `project.sqlite` with its pre-upgrade snapshot.
  Keep media folders beside it and reopen with the matching application version.
- For a JSON-only project, move the original documents and script snapshot folders from
  `legacy-json` back to their original locations, then remove the upgrade-created
  database from the working folder. Keep that database in the safety copy.
- For a mixed project, restore the SQLite snapshot and return original JSON paths that
  were still awaiting import. For committed imports, the snapshot's `migrations` records
  identify authoritative database documents. Keep those originals archived. When there
  are `.duplicate` files, retain both copies and use the original matching the intended
  saved state.

A retry after a later format step may create a backup named for that intermediate
format. Keep the earlier snapshot when restoring to the project's original format.

## Packages

The package format is unchanged JSON. Export renders the stored documents in the
package's file layout and reads them through the same checks as package files. Import
stages the JSON files, upgrades them in the staged `project.sqlite`, then
deletes the staged JSON: the package is the backup. The receipt checks `project.sqlite`
like any other staged file.

## Measured effect

2026-11-01, Windows, Release build, a copy of a project's documents without media:
34 shots, 57 takes, 18 Trash takes, 30 recovery versions, 142 MB `shots.json`.
Each value is the median of five runs after a warm-up.

| Operation | JSON files | SQLite |
| --- | ---: | ---: |
| Load the shot document | 1,984 ms | 256 ms |
| Select a shot (two production loads) | 4,249 ms | 125 ms |
| Load the asset library | 5 ms | 6 ms |

The first open of that project brings its shot document in once, which took a few seconds.
A full shot document load is still dominated by deserializing about 38 MB of takes and
Trash takes. These figures cover storage only, not rendering the page.

Run the benchmark on a copy of a library, never on the library in use:

```powershell
dotnet run --project tools/storage_benchmark -c Release -- <copied data folder> <project-id> --documents
```

`--documents` skips the media measurements, for a copy without media files.
