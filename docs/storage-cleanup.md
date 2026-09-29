# Storage cleanup

Open **Trash** in the global navigation. The two views clean different media:

- **Discarded media** restores images, recordings, takes and reference reels, or permanently deletes them. Filter by project to limit **Empty Trash** to that project. Confirmation captures the listed items; later discards are excluded. Existing 30-day automatic expiry remains unchanged.
- **Lossless archives** lists active takes with saved WebP frame archives, largest first. Select individual takes or every page within the selected project scope, then confirm removal. The displayed space is an estimate; archive sizes use distinct existing files, not the number of logical frames.

Archive removal keeps the MP4, take identity, original generation snapshot, publication receipt, Cut edits, already saved Assets, and independent refinement packages. Frame selection and saving continue through FFmpeg extraction from compressed video. These new frame copies are labelled as MP4-derived. Original lossless pixels cannot be recovered from the MP4.

Before removal, Lumibelle probes the MP4's dimensions, frame count, frame rate and audio, then extracts one frame to check FFmpeg access. If those checks fail, the selected project's archives are retained. A concurrent project save also requires refresh before retrying.

Lumibelle saves a removal record and switches frame access to MP4 before deleting only the captured archive files. A failed save deletes nothing. An interrupted removal retains its record, is shown for manual retry, and resumes on startup or the hourly cleanup pass. A missing MP4 pauses further deletion. Finished removals preserve historical generation and archive timing information; they do not change queued jobs or the shot's setting for future takes.

Removed reels are listed with the other discarded media and expire after 30 days. A reel removed before reels had a Trash period has no recorded expiry and stays until it is deleted. Deleting a reel removes its Trash entry. Its `reference-videos/<media-id>` folder goes only when no other document in the project names that media: another reel, a shot, production setup or captured take, or a recording or saved frame made from it. Those keep working from the kept files. Deletion first marks the entries as being deleted, so an interrupted deletion finishes at the next cleanup pass. The listed size is what deletion would free, and 0 when the video stays.

Discarded takes belong to the Trash view: permanently deleting one removes its video, remaining archives and refinement package together. No active take's archive is removed automatically without a previously confirmed removal. These controls do not clean AI job history, benchmark artifacts, model checkpoints or unrelated files.

## Compact project

**Project settings → Storage → Compact project…** frees space in one project in place. It measures, then runs only the confirmed items: this project's Trash (through the same operation as Empty Trash), lossless take archives (through the removal above), lossless reel archives, migration backups (`production-before-*.json`, never read after they are written) and the stale `manifest.json` beside an unzipped package opened as a folder. It refuses while the project has queued or working AI requests.

Reel archive removal follows the take flow. It checks that FFmpeg can read the reel's frame timestamps and decode a frame from the MP4, otherwise the archive is kept. It then extracts every keyframe chosen from the archive, decoding each segment once, that any project document names, including per-shot copies and captured take snapshots, as `frame-<source>-<index>.png`. It writes `lossless-removal.json`, listing the segments and those pictures, before deleting only the listed segments. `frame-archive.json` stays, because saved keyframes and RefMod recipes use its hash as their source. A retry finishes an interrupted removal. Without the segments, the keyframe editor, frame suggestions and newly saved frames use the MP4, and copying a reel to another project is refused while its keyframes come from the removed archive.

Verification uses synthetic MP4/WebP media and an isolated browser host. No GPU generation or deletion of production media is needed.
