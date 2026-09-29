# Portable project packages

Version 1 (`lumibelle-project`, privacy profile `project-loras-only-v1`).

## Export and import

Open **Your projects → Export / import project**. Select a project, choose whether
**Include referenced images from Trash** is enabled and whether to make a
[compact package for sharing](#compact-packages-for-sharing), then choose **Prepare export**.
Preparation reads the saved project. Unsaved editor changes are not included.
Download the prepared ZIP when ready. Preparing another export does not overwrite
an earlier package. Temporary packages can be removed explicitly.

For import, choose the ZIP and press **Inspect package**. This uploads, validates and
stages the project without publishing it. Review the details, then press **Import
project**. Choosing a file is not permission to publish it. Cancellation or a failed
check leaves the existing library untouched.

The project keeps its ID and all its internal object IDs. Version 1 does not clone,
merge or overwrite projects. An existing project ID, previous AI request history for
that project, or previous global-setup import mappings for that project prevents
import into the same library. This avoids accidentally reconnecting the imported
project to old execution records or changed global settings. Use a different library
for a restore test. A retry of the same successfully committed import is idempotent.

## Editing a project outside the library

A package does not have to be imported. Unzip it anywhere and choose **Your projects →
Open project folder…** with the unzipped folder, or the `project` folder inside it. The
project is then edited in place, and the library only records where it is. **Project
settings → Storage** can also move a library project into a folder of your choice, and
remove a linked folder from the library without touching its files. The same project ID
can be in a library only once, whether in the library folder or a linked one, so opening
the export of a project that is still in the library is refused.

Opening a folder from another library recreates missing generation presets from the
settings saved with its shots. Existing presets in the destination are preserved;
the project keeps its prompts, references and takes. Reopening that folder reuses its
resolved presets. A project inside a running library cannot also be opened as a
linked folder elsewhere; close the owning library first.

## Contents

The package contains the saved script, script source/approved/recovery versions,
asset metadata and all active asset images, recordings and reference reels, saved
reel recipes, shot coverage and recovery, prompts and reference selections, all
active takes, the cut and clip trims, and project AI preferences. Local Script
history and terminal Script responses from the queue are materialized as local
history, preserving their text, targets and review decisions but removing remote
job links. Active requests are not transferred.

The effective values of global generation setups used by this project are captured
into its compositions. Live global setup IDs are detached. When Shots is first
opened, its existing setup-initialization flow can register/reuse those exact
settings. Import itself does not write the destination's global settings library.

Lossless take/reel archives and retained refinement data are included when present,
unless the export leaves the archives out.
Exact accepted RefMod PNG inputs and their recipes are included; remote RefMod
caches, cache-location receipts, thumbnails, loose/unregistered media and temporary
builds are not. RefMod caches can be built on the execution server when needed.

Publication receipts, staged outputs, run folders, global job records, global LoRA
catalogs, credentials, connection authentication, installed models and installed
LoRA weights are excluded. This is a **portable project**, not a byte-for-byte
backup of the entire application state. In particular, historical **One more**,
request recovery and queue-only non-Script responses need the original queue and
its captured inputs. New generation uses the imported project after the user checks
models, selected LoRAs and server setup. LoRA definitions are not automatically
registered and no model files are downloaded by import.

## Compact packages for sharing

Two export options, both off by default, make a smaller package, for example to
publish an example project. They change only the exported copy, never the source
project. The manifest records them: `leftOutLosslessArchives` with
`leftOutLosslessFiles` and `leftOutLosslessBytes`, and `compressedImages` with
`imageQuality`, `maxImageDimension`, `recompressedImages` and `resizedImages`. Import
rejects a manifest whose counts disagree with its options.

**Leave out lossless archives** omits the lossless WebP archives of takes and
reference reels. Run folders and reel candidates are never in a package, with or
without this option. Each take records the omission the same way **Storage cleanup →
Lossless archives** does (`frameArchiveRemoval`, with its size removed from the
take's total), so take review shows "the lossless archive was removed" and decodes
paused frames and new stills from the MP4. Reels keep their `frame-archive.json`
index, because saved keyframes and RefMod recipes use its hash as their source
identity. Each keyframe chosen from the archive is included as its extracted PNG
(`reference-videos/<id>/frame-<source>-<index>.png`), taken from the project's
prepared picture or extracted from the archive during export. Reel details, keyframe
previews, prompt references, RefMod rebuilding, take playback and Cut all keep
working. Exporting such a project again works with either setting.

What changes without the archives:

* The keyframe editor offers frames decoded from the reel's MP4 instead of the
  original lossless frames. Existing lossless keyframes stay, and can be mixed with
  new MP4 picks, but replacing one means choosing a frame from the video. Frame
  suggestions are analysed from the MP4.
* Paused take frames, and images saved from them, come from the MP4.
* **Copy to another project** refuses a reel whose keyframes came from the left-out
  archive, until those keyframes are chosen again from the video.
* Take archive cleanup has nothing to remove.

**Compress images for sharing** re-encodes PNG asset images, including images saved
from take or reel frames and crops, as lossy WebP at quality 85. JPEG and WebP images
are re-encoded (as JPEG or lossy WebP) only when they are reduced in size. An image is
kept as it is when re-encoding would not make it smaller. **Largest image side**
(3840, 2560 or 1920 px, or keep original size) reduces imported images and generated
or edited images whose size is not recorded elsewhere. Images keep their size when a
validator ties it to other details: Codex and Qwen-Image outputs, regional edits,
crops and the images they were cropped from, video and reel frames, and sources of
regional selections. The package's `assets.json` gets each image's new file name,
extension, content type, width and height. The manifest lists the new file's length
and SHA-256. Import and **Open project folder** validate the result like any other
package.

Reel keyframe pictures and RefMod inputs stay lossless PNG. Their bytes are part of
recorded identities (RefMod frame hashes, and a fixed `.png` name served as
`image/png`), and together they are small. Voice recordings and videos are unchanged.

After import, compressed images work everywhere a PNG did. New generations use the
compressed pixels. A shot composition's saved input hashes describe the original
files, but video generation recaptures them, so no review is forced. A saved regional
selection over a compressed image cannot be reused, because its recorded source hash
no longer matches; select the area again.

## Referenced Trash

The export option is enabled by default. It includes only image Trash entries
reachable from saved references and retained generation provenance. Roots include
active images, shot/setup/reel selections, retained takes and saved recovery
content. Cropped/edited image ancestors are followed transitively only when the
image they supply is included. A discarded image's own publication receipt is not a
root. GUIDs or filenames appearing in prose do not count as references. References
whose provenance explicitly belongs to another project are not followed.

With the option off, those image files and their Trash records are excluded. Saved
references are not rewritten or silently replaced; the summary reports omissions.
Already-missing logical sources remain unavailable. A required active media file or
an included dependency that is missing, being purged, or fails an available recorded
hash/size check fails the export;
there is no fallback to some other image or reel. Unreferenced trash images are
never copied, including their names and metadata.

Referenced trashed recordings, takes and reels are retained as dependencies; unrelated
ones are excluded. Included images/recordings/takes remain in **Trash** on import,
with a fresh 30-day recovery period. This prevents a stored package immediately
losing its included sources to the destination's expiry cleanup. They are **not**
restored into the active library, and the original project's retention dates never
change. Restore an included source explicitly before using it for a new generation.

## LoRA metadata privacy

Filtering is always enabled and operates on detached, typed copies:

* Keep only enabled, nonzero LoRA selections in project/asset/setup/reel choices and
  LoRAs recorded as actually applied to retained media. Drop disabled/zero choices.
* Keep only the selected acceleration checkpoint in each captured H3 snapshot.
  Unused Turbo 4/Turbo 8/Larry/PDD/HyperFlow paths are cleared, even when the same
  global settings object originally supplied them. Optional style/character LoRAs
  actually used remain in their original order and strength.
* Do not copy captured owners' unrelated image inventories, LoRA defaults, global
  LoRA catalogs, trigger/tag libraries, or project LoRA visibility tag filters.
* An image edit's zero-strength LoRA filename is cleared. The regular metadata
  validator still requires a name for a nonzero-strength Krea edit.

The count shown in the export summary counts removed metadata occurrences, not
unique model files. An enabled nonzero choice is considered an intentional project
setting even when no take has been generated with it yet. Used names and filenames
can themselves be sensitive and remain visible.

A refinement `.safetensors` file embeds a captured snapshot in its JSON header.
Filtering only `shots.json` would both leak inactive paths and break context checks.
The exporter rewrites the bounded metadata header, preserves tensor data bytes
verbatim, revalidates the package and updates its size/hash and child source-package
references. No VAE, model or GPU is invoked. Sanitized snapshot fingerprints are
recomputed where inactive choices affect serialization. Authored prompts, actual
applied weights and the selected preset schedule do not change. Imported prompts
may still need normal configuration/reference review.

**Not an anonymizer:** scripts, prompts, instructions and media are retained as
content. Original image/audio/video bytes can contain EXIF, comments, ComfyUI PNG
workflows or other third-party metadata. These are not scrubbed or re-encoded; doing
so would also change authoritative source bytes and recorded hashes. The exception is
an image re-encoded by **Compress images for sharing**, which drops EXIF, XMP and PNG
text chunks. Inspect that
content before sharing a package that needs broader anonymization. The unused-LoRA
filter covers structured application metadata and supported refinement headers,
not arbitrary text embedded in unrelated binary formats.

## Integrity and safety

ZIP entries are selected through an allowlist and dependency traversal, never a
recursive copy of the whole project directory. The manifest lists relative paths,
lengths and SHA-256 hashes. Hashes detect corruption, not author authenticity: the
format is neither signed nor encrypted.

Import bounds the ZIP/ZIP64 central directory before opening its entries, rejects
multi-disk archives, traversal/absolute paths, backslashes, Windows device names,
case collisions, duplicate JSON properties, symbolic links, directory entries and
unsupported files. It verifies the manifest, every extracted file and semantic
reel/RefMod source hashes. Dependency traversal rejects unreferenced extra media.
Typed metadata is normalized and read through the actual project stores before
publication. Source links/reparse points are not followed during export.

A private `.importing-<token>` directory holds the unpublished project. Confirmation
rechecks staged bytes and refuses added files, then publishes by a same-filesystem
directory rename. Import never resumes an AI request or starts inference. Completed
import acknowledgement can be retried without another copy. The source project is
never modified, including by history projection or privacy filtering.

Limits are 64 GiB compressed, 256 GiB expanded, 100,000 files, 64 MiB per metadata
file, 256 MiB combined JSON and a 128-pass media ancestry limit. Transfers stream
large media to disk. Sufficient temporary disk space is needed for ZIPs, extracted
content and rewritten refinement packages. Transfers have cancellation/progress
and use a separate local transfer gate, not the ComfyUI queue. Export holds the
project save lock while packaging a consistent saved state; saves and publication
for that project can wait until it finishes or is cancelled.

Temporary transfer directories older than 24 hours are cleaned at the next transfer.
The UI can discard a staged import or prepared export. Closing a staged import also
requests cleanup. No additional background worker is created.

## Verification

The implementation bundle records which source files/ranges were checked. The C#
and Razor build, application tests, browser uploads and native file dialogs must
be validated on an installation with the .NET 10 SDK. The supplied tests use isolated
folders, synthetic media and mocked UI boundaries; they do not validate media codec
playback or claim a live ComfyUI/GPU run.

```powershell
dotnet build src/Lumibelle.Web/Lumibelle.Web.csproj -c Release
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj -c Release --filter "FullyQualifiedName~ProjectPackage"
```

Before relying on a package, export a small test project with a cropped image whose
parent is in Trash, one unused trashed image, a speaking reel with RefMod inputs, a
retained refinement take, and a trimmed cut. Include disabled/zero LoRA selections
and unrelated accelerator filenames using a unique test sentinel. Export with the
Trash option both on and off. Inspect the ZIP metadata and refinement headers for
the sentinel, then import into another library. Confirm playback, crop selections,
voice excerpts, Script history, effective generation settings and RefMod rebuilding.
Confirm unreferenced Trash was excluded, included Trash did not become active,
source files did not change, and import did not create a remote request. Test a
cancelled upload, a checksum failure and a duplicate-project import as well.

For a compact package, export the same project with both sharing options, import it
into another library and also open the unzipped folder. Confirm the source files did
not change. Then check take playback and paused frames, reel details and keyframe
previews, adding a keyframe from the video, RefMod rebuilding, asset images and their
crops, and Cut playback.
