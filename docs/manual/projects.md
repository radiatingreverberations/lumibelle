# Projects and storage

## Project library

When run from source, the Development profile uses the repository's `App_Data/Projects` library. Installed editions default to `Lumibelle/Projects` under the current user's local application-data directory. `App_Data` is ignored by Git. No data is moved automatically.

Set `Projects:RootDirectory` in `appsettings.json`, or override it for one run:

```powershell
dotnet run --project src/Lumibelle.Web --launch-profile http -- --Projects:RootDirectory="D:\Films\Lumibelle"
```

The equivalent environment variable is `Projects__RootDirectory`. Relative locations resolve against the application content root; absolute locations are used directly. Changing the root selects a different library and does not move existing projects.

Only one Lumibelle process can use a data directory and project library at a time; a second process fails clearly. Multiple browser tabs in one process continue to work.

## Project folders

Each project has a GUID-named folder containing `project.json`:

```json
{
  "schemaVersion": 1,
  "id": "8d23818b-d03d-48a4-b7f1-f83b2ed56ea2",
  "name": "The garden at the end of the world",
  "description": "A small story about finding a way home.",
  "createdUtc": "2026-09-03T12:00:00+00:00"
}
```

Names may contain Unicode and may repeat. Folder names use stable IDs, so project names never become filesystem paths.

Each project folder additionally contains:

```text
script.json                   # typed blocks, revision, historical source pointer
script-assistant.json         # discussion, proposals, request metadata and status
script-history/<id>.json      # recoverable draft snapshots
script-sources/<id>.json      # immutable saved-source captures
script-approved/<id>.json     # historical snapshots, compatibility reader only
assets.json                   # ordered assets, image metadata, tags, and revision
assets/<asset-id>/images/*    # validated project-owned reference images
cut.json                      # the project's saved sequence
```

Files are versioned, readable JSON published through atomic replacement. Existing projects without writing files open as empty workspaces. Corrupt or unsupported documents produce errors and are not replaced.

## Edit project details

Choose **Edit project** on the project overview to change its name or description. Saving keeps the same project ID, creation date, script and assets. The description can be cleared; a name is required. Cancel discards the draft, and save failures keep your edits available for retry. If another tab changed the details first, copy your draft and reopen the refreshed project before saving again.

## Back up and move projects

Back up or move a project by copying its complete folder, retaining its ID and manifest. To reopen a copied project, place that folder in the configured library and refresh the hub. Choose a new library root before copying if that ID already exists there.

Keep complete project folders in backups. Chat and proposals may contain copies of source text supplied to a model.

Invalid, unreadable or unsupported manifests produce warnings while healthy projects remain available. Fix those manifests externally and refresh; Lumibelle does not silently overwrite them. Interrupted project creation is ignored.

## Compact a project

**Project settings → Storage → Compact project…** makes a project smaller in place, for example before publishing its folder. It first shows what takes up space: the project's size by kind, such as take videos, reel archives and generation working files, and its largest files, marking any over 100 MB, which GitHub rejects without Git LFS. It then lists what it found with the space each part frees, and removes only what you select after one confirmation:

- **Empty this project's Trash**: the same as Empty Trash filtered to this project.
- **Lossless take archives** and **Lossless reel archives**: takes and reels keep their MP4. Every reel keyframe already chosen from lossless frames is saved as a picture first, so reference images, RefMod inputs and prompts are unchanged. Paused frames and new keyframes are then decoded from the MP4.
- **Migration backups**: copies of `production.json` saved before an automatic upgrade, which Lumibelle does not read.
- **Package manifest**: an unzipped project package keeps `manifest.json` beside its `project` folder; once the project is edited it no longer matches. The unzipped folder still opens without it.

Removed data cannot be recovered, and the original lossless pixels cannot be rebuilt from the MP4. Close the project in other windows and let queued AI requests finish first. Images are never re-encoded in place; to share smaller images, use **Compress images for sharing** when exporting a package.

## History and recovery

The Script studio's **History** contains snapshots saved before AI application, scene deletion and restoring an older version. Preview and restore a version there; restoring first snapshots the current writing. Autosave is not a snapshot of every keystroke. Closing or reloading before a successful save can lose unsaved text; the editor warns when navigating away with unsaved text. Interrupted generation is marked after an application restart and is not automatically resumed.

Deleted images and reels go to **Trash** for 30 days; see [Assets studio](assets.md#trash).

## Settings and API keys

Global settings are stored in `App_Data/ai-settings.json` beneath the application content root, separately from the project library. API keys are encrypted with ASP.NET Core Data Protection for the account running Lumibelle. The UI shows only whether a saved key is configured and supports replacing or removing it. Moving settings to another account or losing the key ring may require re-entering the key; project writing remains portable. Never commit the settings file or key ring.
