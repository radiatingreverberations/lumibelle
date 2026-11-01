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

## Migration from JSON

Each document is brought in the first time it is read, independently of the others:

1. Read the legacy file as before. Unreadable JSON raises the usual error and nothing changes.
2. In one transaction, write the document's rows and a `committed` migration row.
3. Move the legacy files into `<project>/legacy-json/`, then mark the migration `complete`.

Before the commit the JSON stays authoritative and untouched; after it the database is,
and an interrupted move resumes on the next open without reading the JSON again. Legacy
files that appear later are ignored. A document with nothing to bring in writes nothing
until it is first saved.

The legacy JSON is kept as it was, except shots, which pass their usual read-time
upgrades and validation first. One-time production upgrade backups
(`production-before-*.json`) move with `production.json` and are now written into
`legacy-json`. **Compact project** offers the whole `legacy-json` folder under backups;
Lumibelle never reads it again.

A linked project folder or an unzipped package migrates in place the same way.

## Packages

The package format is unchanged JSON. Export renders the stored documents in the
package's file layout and reads them through the same checks as package files. Import
stages the JSON files, brings every document into the staged `project.sqlite`, then
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
