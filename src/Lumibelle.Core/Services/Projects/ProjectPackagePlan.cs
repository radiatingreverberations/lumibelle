using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;

namespace lumibelle.Services.Projects;

internal sealed record ProjectPackageSource(string Relative, string Physical, long? ExpectedBytes = null, string? ExpectedHash = null);

internal sealed class ProjectPackagePlan
{
    internal required ProjectPackageState State;
    internal Dictionary<string, ProjectPackageSource> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, byte[]> Metadata { get; } = new(StringComparer.Ordinal);
    internal List<string> Notices { get; } = [];
    internal int ReferencedTrashImages, IncludedTrashImages, LeftOutLosslessFiles;
    internal long LeftOutLosslessBytes;

    // Scratch receives keyframe pictures extracted for a package that leaves out reel archives.
    internal static async Task<ProjectPackagePlan> CreateAsync(string root, ProjectPackageState state, ProjectExportOptions options, CancellationToken ct, string? scratch = null)
    {
        var plan = new ProjectPackagePlan { State = state };
        var assets = state.Assets; var shots = state.Shots;
        var selectedImages = new HashSet<Guid>(); var selectedTakes = new HashSet<Guid>();
        var selectedReels = new HashSet<Guid>(); var selectedVoices = new HashSet<Guid>();
        var seenVideos = new HashSet<Guid>(); var references = new References(state.Project.Id);
        // Inventory and publication receipts are NOT reference roots. Otherwise every deleted
        // generation would keep itself alive merely because its receipt still exists.
        state.Assets = assets with { Trash = [], ReelTrash = [], VoiceTrash = [], ImagePublications = [],
            ImageCopyReceipts = [], ReelPublications = [], VoiceImportReceipts = [] };
        state.Shots = shots with { Trash = [], TakePublications = [] };
        references.Scan(state.Assets); references.Scan(state.Shots); references.Scan(state.Production); references.Scan(state.Cut);
        var changed = true; var rounds = 0;
        while (changed)
        {
            ct.ThrowIfCancellationRequested();
            if (++rounds > 128) throw new WorkspaceStoreException("The media dependency chain is too deep to transfer safely.");
            changed = false;
            foreach (var item in assets.Trash.Where(t => references.Images.Contains(t.Image.Id) && selectedImages.Add(t.Image.Id)))
            {
                plan.ReferencedTrashImages++;
                if (!options.IncludeReferencedTrashImages) continue;
                if (item.State != ImageTrashState.Recoverable) throw new WorkspaceStoreException("A referenced trash image is being purged. Finish cleanup or export without trash images.");
                state.Assets.Trash.Add(item); plan.IncludedTrashImages++;
                references.Scan(item.Image); changed = true;
            }
            foreach (var item in assets.ReelTrash.Where(t => references.Reels.Contains(t.Reel.Id) || references.Videos.Contains(t.Reel.Media.Id)).Where(t => selectedReels.Add(t.Reel.Id)))
            { state.Assets.ReelTrash.Add(item); references.Scan(item.Reel); changed = true; }
            foreach (var item in assets.VoiceTrash.Where(t => references.Voices.Contains(t.Voice.Id) && selectedVoices.Add(t.Voice.Id)))
            {
                if (item.Purging) throw new WorkspaceStoreException("A referenced recording is being purged. Finish cleanup before exporting.");
                state.Assets.VoiceTrash.Add(item); references.Scan(item.Voice); changed = true;
            }
            foreach (var item in shots.Trash.Where(t => t.Take is not null && references.Takes.Contains(t.Take.Id) && selectedTakes.Add(t.Take.Id)))
            {
                if (item.Purging) throw new WorkspaceStoreException("A referenced take is being purged. Finish cleanup before exporting.");
                state.Shots.Trash.Add(item); references.Scan(item.Take); references.Scan(item.Owner); changed = true;
            }
        }
        if (plan.ReferencedTrashImages > plan.IncludedTrashImages)
            plan.Notices.Add($"{plan.ReferencedTrashImages - plan.IncludedTrashImages} referenced trash image(s) were omitted. Their saved links remain unavailable until restored separately.");
        var allImages = state.Assets.Assets.SelectMany(a => a.Images.Select(i => (Owner: a.Id, Image: i)))
            .Concat(state.Assets.Trash.Select(t => (Owner: t.Asset.Id, Image: t.Image))).ToArray();
        foreach (var (owner, image) in allImages)
            plan.Add(root, $"assets/{(image.StorageAssetId ?? owner):D}/images/{image.FileName}");
        foreach (var id in references.Images.Except(allImages.Select(i => i.Image.Id)).Except(assets.Trash.Select(t => t.Image.Id)))
            plan.Note($"Referenced image {id:D} is already unavailable; its link was preserved.");
        var allVoices = state.Assets.Voices.Concat(state.Assets.VoiceTrash.Select(t => t.Voice)).ToArray();
        foreach (var voice in allVoices) plan.Add(root, $"assets/{(voice.StorageAssetId ?? voice.AssetId):D}/voices/{voice.FileName}");
        foreach (var id in references.Voices.Except(allVoices.Select(v => v.Id))) plan.Note($"Referenced recording {id:D} is already unavailable.");
        var allTakes = state.Shots.Takes.Concat(state.Shots.Trash.Where(t => t.Take is not null).Select(t => t.Take!)).ToArray();
        if (options.LeaveOutLosslessArchives && allTakes.Any(t => t.Frames.Count > 0))
            throw new WorkspaceStoreException("This package leaves out lossless archives, but a take still lists its archive.");
        foreach (var take in allTakes)
        {
            plan.Add(root, $"shots/takes/{take.Id:D}/video.mp4");
            foreach (var frame in take.Frames.DistinctBy(f => f.FileName)) plan.Add(root, $"shots/takes/{take.Id:D}/{frame.FileName}", frame.Bytes);
            if (take.RefinementPackage is { } package) plan.Add(root, $"shots/takes/{take.Id:D}/{H3RefinementPackage.FileName}", package.Bytes, package.Sha256);
        }
        foreach (var id in references.Takes.Except(allTakes.Select(t => t.Id))) plan.Note($"Source take {id:D} is unavailable; saved media and provenance remain included.");
        // A binding can outlive its reel's asset-library entry. Its immutable media record
        // remains an authoritative dependency and is not replaced with a different reel.
        foreach (var reel in state.Assets.Reels.Concat(state.Assets.ReelTrash.Select(t => t.Reel))) references.Scan(reel);
        foreach (var id in references.Videos)
        {
            if (!seenVideos.Add(id)) continue;
            var relative = $"reference-videos/{id:D}";
            var record = await ProjectPackageFormat.ReadAsync<ReferenceVideoMedia>(root, relative + "/media.json", ct)
                ?? throw new WorkspaceStoreException("A referenced reel's media record is missing. Restore it before exporting.");
            ReferenceVideos.ValidateMedia(record);
            if (record.Id != id || references.VideoRecords.TryGetValue(id, out var expected) && record != expected)
                throw new WorkspaceStoreException("A referenced reel differs from its captured media identity.");
            plan.Metadata.Add(relative + "/media.json", ProjectPackageFormat.Json(record));
            plan.Add(root, relative + "/video.mp4", record.Bytes, record.Sha256);
            var archive = await ProjectPackageFormat.ReadAsync<ReelFrameArchive>(root, relative + "/frame-archive.json", ct);
            if (archive is not null)
            {
                if (archive.SourceSha256 != record.Sha256 || archive.Width != record.Width || archive.Height != record.Height ||
                    archive.FrameCount != record.Frames || archive.Files is null || archive.Frames is null || archive.Frames.Count != record.Frames ||
                    archive.Files.Count != (record.Frames + 23) / 24 || archive.Files.Select(f => f.FileName).Distinct().Count() != archive.Files.Count ||
                    archive.Files.Where((f, i) => f.FileName != LosslessFrameArchive.FileName(i) || f.Bytes <= 0 || !ProjectPackageFormat.Hash(f.Sha256)).Any() ||
                    archive.Frames.Where((f, i) => f.Index != i || f.FileName != LosslessFrameArchive.FileName(i / 24) || f.ArchiveFrameIndex is < 0 or >= 24).Any())
                    throw new WorkspaceStoreException("A reference reel's lossless frame archive is invalid.");
                // The index stays even without its segments: saved keyframes use its hash as their source.
                plan.Metadata.Add(relative + "/frame-archive.json", ProjectPackageFormat.Json(archive));
                var present = archive.Files.Where(f => File.Exists(ProjectPackageFormat.Under(root, relative + "/lossless/" + f.FileName))).ToArray();
                if (!options.LeaveOutLosslessArchives && present.Length == archive.Files.Count)
                    foreach (var file in archive.Files) plan.Add(root, relative + "/lossless/" + file.FileName, file.Bytes, file.Sha256);
                else if (!options.LeaveOutLosslessArchives && present.Length != 0)
                    throw new WorkspaceStoreException("A reference reel's lossless frame archive is incomplete. Restore it before exporting.");
                else
                {
                    plan.LeftOutLosslessFiles += present.Length; plan.LeftOutLosslessBytes += present.Sum(f => f.Bytes);
                    await plan.AddKeyframesAsync(root, relative, archive, references.Keyframes.Where(k => k.MediaId == id), scratch, ct);
                }
            }
        }
        foreach (var reference in references.RefMods.Values)
        {
            ReelRefMods.Validate(reference);
            for (var i = 0; i < reference.Recipe.LatentFrames; i++)
                plan.Add(root, $"refmod-previews/{reference.Recipe.Key}/{ReelRefModStore.FrameName(i)}", hash: reference.Recipe.FrameHashes[i]);
        }
        if (plan.Sources.Count + state.Other.Count + plan.Metadata.Count + 5 + (state.History is null ? 0 : 1) > ProjectPackageFormat.MaxFiles)
            throw new WorkspaceStoreException("Too many files in this project package.");
        state.Validate();
        return plan;
    }
    internal void Add(string root, string path, long? bytes = null, string? hash = null)
    {
        if (!ProjectPackageFormat.Allowed(path)) throw new WorkspaceStoreException("Unsupported project media path.");
        var full = ProjectPackageFormat.Under(root, path);
        ProjectPackageFormat.NoLinks(root, full);
        if (Sources.TryGetValue(path, out var previous)) {
            if (previous.Relative != path || previous.ExpectedBytes is not null && bytes is not null && previous.ExpectedBytes != bytes ||
                previous.ExpectedHash is not null && hash is not null && previous.ExpectedHash != hash)
                throw new WorkspaceStoreException("Two references disagree about the same media file.");
            Sources[path] = previous with { ExpectedBytes = previous.ExpectedBytes ?? bytes, ExpectedHash = previous.ExpectedHash ?? hash };
        } else Sources.Add(path, new(path, full, bytes, hash));
    }
    private void Note(string text) { if (Notices.Count < 1000) Notices.Add(text); }
    // Without the segments, a keyframe picked from them is only available as its extracted picture.
    private async Task AddKeyframesAsync(string root, string relative, ReelFrameArchive archive, IEnumerable<ReelFrameIdentity> frames, string? scratch, CancellationToken ct)
    {
        var source = Convert.ToHexString(SHA256.HashData(ProjectPackageFormat.Json(archive)));
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in frames.Where(f => f.Source == source).DistinctBy(f => f.Index).OrderBy(f => f.Index))
        {
            ct.ThrowIfCancellationRequested();
            if (frame.Index < 0 || frame.Index >= archive.FrameCount) throw new WorkspaceStoreException("A reel keyframe is outside its lossless archive.");
            var path = relative + "/" + FileReferenceVideoStore.FrameFileName(frame); var full = ProjectPackageFormat.Under(root, path);
            if (File.Exists(full))
            {
                ProjectPackageFormat.NoLinks(root, full);
                var info = await Image.IdentifyAsync(full, ct);
                if (info.Metadata.DecodedImageFormat?.Name != "PNG" || info.Width != archive.Width || info.Height != archive.Height)
                    throw new WorkspaceStoreException("A reel keyframe picture does not match its lossless archive.");
                Add(root, path); continue;
            }
            var entry = archive.Frames[frame.Index]; var file = archive.Files.Single(f => f.FileName == entry.FileName);
            var segment = ProjectPackageFormat.Under(root, relative + "/lossless/" + file.FileName);
            if (scratch is null || !File.Exists(segment)) throw new WorkspaceStoreException("A reel keyframe picture is missing and its lossless frames are unavailable.");
            ProjectPackageFormat.NoLinks(root, segment);
            if (verified.Add(file.FileName))
            {
                await using var input = File.OpenRead(segment);
                if (input.Length != file.Bytes || (await ProjectPackageFormat.CopyAsync(input, Stream.Null, file.FileName, file.Bytes, ct)).Sha256 != file.Sha256)
                    throw new WorkspaceStoreException("A reference reel's lossless archive changed on disk.");
            }
            var output = Path.Combine(scratch, "keyframes", Guid.NewGuid().ToString("N") + ".png"); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using (var png = await LosslessFrameArchive.OpenFrameAsync(segment, entry.ArchiveFrameIndex, archive.Width, archive.Height, ct))
            await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) await png.CopyToAsync(target, ct);
            if (!ProjectPackageFormat.Allowed(path) || Sources.ContainsKey(path)) throw new WorkspaceStoreException("Unsupported reel keyframe path.");
            Sources.Add(path, new(path, output));
        }
    }

    private sealed class References(Guid project)
    {
        internal HashSet<Guid> Images = [], Takes = [], Reels = [], Voices = [], Videos = [];
        internal HashSet<ReelFrameIdentity> Keyframes = [];
        internal Dictionary<Guid, ReferenceVideoMedia> VideoRecords = [];
        internal Dictionary<string, ReelRefModReference> RefMods = new(StringComparer.Ordinal);
        internal void Scan<T>(T source) => Visit(JsonSerializer.SerializeToNode(source, AtomicJsonFile.Options));
        private static Guid? Id(JsonNode? n)
        {
            if (n is not JsonValue value) return null;
            if (value.TryGetValue<Guid>(out var id)) return id == Guid.Empty ? null : id;
            return value.TryGetValue<string>(out var text) && Guid.TryParse(text, out id) && id != Guid.Empty ? id : null;
        }
        private void Visit(JsonNode? node)
        {
            if (node is JsonArray a) { foreach (var item in a) Visit(item); return; }
            if (node is not JsonObject o) return;
            if (Id(o["projectId"]) is { } origin && origin != project) return; // Never traverse another project.
            if (Id(o["imageId"]) is { } image) Images.Add(image);
            if (Id(o["sourceImageId"]) is { } editImage) Images.Add(editImage);
            if (Id(o["mediaId"]) is { } media) {
                if (o["kind"]?.ToString() == "AssetImage") Images.Add(media);
                else if (o.ContainsKey("source") && o.ContainsKey("index") && o.ContainsKey("seconds")) Videos.Add(media);
            }
            foreach (var key in new[] { "takeId", "selectedTakeId", "sourceTakeId", "parentTakeId" }) if (Id(o[key]) is { } take) Takes.Add(take);
            foreach (var key in new[] { "reelId", "sourceReelId" }) if (Id(o[key]) is { } reel) Reels.Add(reel);
            if (Id(o["voiceId"]) is { } voice) Voices.Add(voice);
            if (o.ContainsKey("sha256") && o.ContainsKey("hasAudio") && o.ContainsKey("fps") && o.ContainsKey("frames") && Id(o["id"]) is { } id)
            {
                var record = o.Deserialize<ReferenceVideoMedia>(AtomicJsonFile.Options)!; ReferenceVideos.ValidateMedia(record);
                if (VideoRecords.TryGetValue(id, out var prior) && prior != record) throw new WorkspaceStoreException("Conflicting captured reel media records.");
                VideoRecords[id] = record; Videos.Add(id);
            }
            if (ReelKeyframeScan.Read(o) is { } keyframe) Keyframes.Add(keyframe);
            if (o["refMod"] is JsonObject mod)
            {
                var reference = mod.Deserialize<ReelRefModReference>(AtomicJsonFile.Options)!; ReelRefMods.Validate(reference);
                if (RefMods.TryGetValue(reference.Recipe.Key, out var prior) && !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(prior.Recipe), JsonSerializer.SerializeToElement(reference.Recipe)))
                    throw new WorkspaceStoreException("Conflicting RefMod source recipes.");
                RefMods[reference.Recipe.Key] = reference;
            }
            foreach (var entry in o)
                if (entry.Key is not ("owner" or "asset")) Visit(entry.Value);
        }
    }
}
