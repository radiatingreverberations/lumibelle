using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class FileAssetStore
{
    private async Task<AssetReusePackage> CaptureReusePackageAsync(Guid id, string name, AssetReuseContent content,
        string projectDirectory, string staging, CancellationToken ct)
    {
        var entries = new Dictionary<string, AssetReuseFile>(StringComparer.Ordinal);
        long total = 0;
        async Task Add(string source, string relative, long maximum, string? expectedHash = null, long? expectedBytes = null)
        {
            if (entries.ContainsKey(relative)) return;
            if (entries.Count >= AssetReusePolicy.MaximumPackageFiles) throw new WorkspaceStoreException("This asset has too many media files to copy in one operation.");
            var file = await CopyReuseFileAsync(source, ReusePath(staging, relative), maximum, ct, expectedHash, expectedBytes);
            total = checked(total + file.Bytes);
            if (total > AssetReusePolicy.MaximumPackageBytes) throw new WorkspaceStoreException("This asset exceeds the 8 GiB transfer limit; copy smaller selections.");
            entries.Add(relative, file with { Path = relative });
        }
        foreach (var image in content.Asset.Images)
            await Add(ReusePath(projectDirectory, Path.GetRelativePath(projectDirectory, ImagePath(projectDirectory, content.Asset.Id, image)).Replace('\\', '/')), ReuseImagePath(image), MaximumImageBytes);
        foreach (var voice in content.Voices)
            await Add(ReusePath(projectDirectory, Path.GetRelativePath(projectDirectory, VoicePath(projectDirectory, voice)).Replace('\\', '/')), ReuseVoicePath(voice), 50L * 1024 * 1024);
        foreach (var reel in content.Reels.DistinctBy(r => r.Media.Id))
        {
            var root = ReusePath(projectDirectory, "reference-videos/" + reel.Media.Id.ToString("D"));
            var media = await AtomicJsonFile.ReadAsync<ReferenceVideoMedia>(ReusePath(root, "media.json"), ct);
            if (media != reel.Media) throw new WorkspaceStoreException("A reel's stored media record changed or is missing.");
            var prefix = "reels/" + reel.Media.Id.ToString("D") + "/";
            await Add(ReusePath(root, "video.mp4"), prefix + "video.mp4", ReferenceVideos.MaximumBytes, reel.Media.Sha256, reel.Media.Bytes);
            var archivePath = ReusePath(root, "frame-archive.json");
            string? archiveIdentity = null, leftOut = null;
            if (File.Exists(archivePath))
            {
                var archive = await AtomicJsonFile.ReadAsync<ReelFrameArchive>(archivePath, ct)
                    ?? throw new WorkspaceStoreException("The reel frame archive is missing.");
                if (archive.SourceSha256 != reel.Media.Sha256 || archive.Width != reel.Media.Width || archive.Height != reel.Media.Height ||
                    archive.FrameCount != reel.Media.Frames || archive.Frames.Count != reel.Media.Frames)
                    throw new WorkspaceStoreException("The reel's lossless frames do not match its video.");
                if (archive.Files.Any(f => f.FileName != Path.GetFileName(f.FileName) || f.FileName.Contains('\\')))
                    throw new WorkspaceStoreException("The reel archive contains an invalid segment name.");
                // A compact project package keeps only the index. Copy the reel without it.
                if (archive.Files.Any(f => !File.Exists(ReusePath(root, "lossless/" + f.FileName)))) leftOut = AssetReusePolicy.Hash(archive);
                else
                {
                    archiveIdentity = AssetReusePolicy.Hash(archive);
                    await Add(archivePath, prefix + "frame-archive.json", 4 * 1024 * 1024);
                    foreach (var segment in archive.Files)
                        await Add(ReusePath(root, "lossless/" + segment.FileName), prefix + "lossless/" + segment.FileName,
                            AssetReusePolicy.MaximumPackageBytes, segment.Sha256, segment.Bytes);
                }
            }
            var times = ReusePath(root, "frame-times-v2.json");
            if (File.Exists(times)) await Add(times, prefix + "frame-times-v2.json", 4 * 1024 * 1024);
            foreach (var view in content.Reels.Where(r => r.Media.Id == reel.Media.Id)) {
                if (view.Media != reel.Media) throw new WorkspaceStoreException("Two reels disagree about their shared media.");
                if (view.Keyframes is null) continue;
                ReferenceVideos.ValidateKeyframes(view.Media, view.Keyframes);
                if (leftOut is not null && view.Keyframes.Frames.Any(f => f.Frame.Source == leftOut))
                    throw new WorkspaceStoreException($"“{view.Name}” has keyframes from lossless frames that are not in this project. Choose its keyframes again from the video before copying it.");
                if (view.Keyframes.Frames.Any(f => f.Frame.Source != reel.Media.Sha256 && f.Frame.Source != archiveIdentity))
                    throw new WorkspaceStoreException("A selected keyframe's source archive is missing or changed.");
            }
        }
        var package = new AssetReusePackage(1, id, name.Trim(), clock.GetUtcNow(), AssetReusePolicy.Hash(content), content, entries.Values.ToArray());
        ValidateReusePackage(package);
        return package;
    }
    private static string ReuseImagePath(AssetImage image) => "images/" + image.Id.ToString("N") + Path.GetExtension(image.FileName);
    private static string ReuseVoicePath(VoiceReference voice) => "voices/" + voice.Id.ToString("N") + Path.GetExtension(voice.FileName);
    private static async Task<AssetReusePackage> ReadReusePackageAsync(string root, CancellationToken ct)
    {
        var package = await AtomicJsonFile.ReadAsync<AssetReusePackage>(ReusePath(root, "manifest.json"), ct)
            ?? throw new WorkspaceStoreException("The shared asset snapshot is unavailable.");
        ValidateReusePackage(package); return package;
    }
    internal static void ValidateReusePackage(AssetReusePackage package)
    {
        if (package is null || package.Version != 1 || package.Id == Guid.Empty || string.IsNullOrWhiteSpace(package.Name) || package.Name.Length > 240 ||
            package.CreatedUtc == default || package.Content is not { } content || content.Source is null || content.Asset is null ||
            content.Reels is null || content.Voices is null || package.Files is null || package.Files.Count > AssetReusePolicy.MaximumPackageFiles ||
            !AssetReusePolicy.IsHash(package.SourceFingerprint) || AssetReusePolicy.Hash(content) != package.SourceFingerprint)
            throw new WorkspaceStoreException("The shared asset manifest is invalid or has changed.");
        AssetReusePolicy.Validate(content.Source);
        if (content.Asset.Id != content.Source.AssetId || content.Reels.Any(r => r.AssetId != content.Asset.Id) || content.Voices.Any(v => v.AssetId != content.Asset.Id))
            throw new WorkspaceStoreException("The copied asset has invalid media ownership.");
        Validate(new AssetLibrary { ProjectId = content.Source.ProjectId, Assets = [content.Asset], Reels = content.Reels.ToList(), Voices = content.Voices.ToList() }, content.Source.ProjectId);
        if (content.Source.Kind != AssetReuseKind.Asset && (content.Asset.Images.Count + content.Reels.Count + content.Voices.Count != 1 ||
            content.Source.Kind == AssetReuseKind.Image && content.Asset.Images.SingleOrDefault()?.Id != content.Source.MediaId ||
            content.Source.Kind == AssetReuseKind.Reel && content.Reels.SingleOrDefault()?.Id != content.Source.MediaId ||
            content.Source.Kind == AssetReuseKind.Voice && content.Voices.SingleOrDefault()?.Id != content.Source.MediaId))
            throw new WorkspaceStoreException("The shared selection contains unexpected media.");
        var known = content.Asset.Images.Select(ReuseImagePath).Concat(content.Voices.Select(ReuseVoicePath)).ToHashSet(StringComparer.Ordinal);
        var prefixes = content.Reels.Select(r => "reels/" + r.Media.Id.ToString("D") + "/").Distinct().ToArray();
        foreach (var prefix in prefixes) known.Add(prefix + "video.mp4");
        long total = 0;
        foreach (var entry in package.Files)
        {
            if (entry is null || !ReuseRelativePath(entry.Path) || entry.Bytes <= 0 || entry.Bytes > AssetReusePolicy.MaximumPackageBytes || !AssetReusePolicy.IsHash(entry.Sha256) ||
                !known.Contains(entry.Path) && !prefixes.Any(p => entry.Path == p + "frame-archive.json" || entry.Path == p + "frame-times-v2.json" ||
                    entry.Path.StartsWith(p + "lossless/", StringComparison.Ordinal) && !entry.Path[(p.Length + 9)..].Contains('/')))
                throw new WorkspaceStoreException("The asset package contains an invalid media file.");
            total = checked(total + entry.Bytes);
            if (total > AssetReusePolicy.MaximumPackageBytes) throw new WorkspaceStoreException("The asset exceeds the 8 GiB transfer limit.");
        }
        if (package.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != package.Files.Count || known.Any(p => package.Files.All(f => f.Path != p)))
            throw new WorkspaceStoreException("The asset package has missing or duplicate media files.");
    }
    private static async Task<IReadOnlyList<AssetReuseFile>> InstallReuseFilesAsync(AssetReusePackage package, string contentRoot, string destination,
        AssetReuseCommand command, CancellationToken ct)
    {
        ValidateReusePackage(package);
        var assetId = command.Destination.AssetId ?? AssetReusePolicy.Identity(command.Id, "asset", package.Content.Asset.Id);
        await ValidateReuseArchivesAsync(package, contentRoot, ct);
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        var installed = new List<AssetReuseFile>();
        foreach (var image in package.Content.Asset.Images)
            mappings[ReuseImagePath(image)] = $"assets/{assetId:D}/images/{AssetReusePolicy.Identity(command.Id, "image", image.Id):N}{Path.GetExtension(image.FileName)}";
        foreach (var voice in package.Content.Voices)
            mappings[ReuseVoicePath(voice)] = $"assets/{assetId:D}/voices/{AssetReusePolicy.Identity(command.Id, "voice", voice.Id):N}{Path.GetExtension(voice.FileName)}";
        foreach (var reel in package.Content.Reels.DistinctBy(r => r.Media.Id))
        {
            var prefix = "reels/" + reel.Media.Id.ToString("D") + "/";
            var mediaId = AssetReusePolicy.Identity(command.Id, "reel-media", reel.Media.Id);
            foreach (var entry in package.Files.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal)))
                mappings[entry.Path] = $"reference-videos/{mediaId:D}/" + entry.Path[prefix.Length..];
        }
        foreach (var entry in package.Files)
        {
            if (!mappings.TryGetValue(entry.Path, out var target)) throw new WorkspaceStoreException("Unexpected package media.");
            var output = ReusePath(destination, target);
            installed.Add(entry with { Path = target });
            if (File.Exists(output))
            {
                // Crash recovery may find an installed but unpublished file. Never overwrite a different file.
                if (new FileInfo(output).Length != entry.Bytes || !string.Equals(await ReuseFileHashAsync(output, ct), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new WorkspaceStoreException("A different media file already occupies this copy's destination.");
                // Still verify the retained source; a broken shared entry is not silently accepted.
                var source = ReusePath(contentRoot, entry.Path);
                if (new FileInfo(source).Length != entry.Bytes || !string.Equals(await ReuseFileHashAsync(source, ct), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new WorkspaceStoreException("The shared media changed or is damaged.");
                continue;
            }
            var temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await CopyReuseFileAsync(ReusePath(contentRoot, entry.Path), temp, AssetReusePolicy.MaximumPackageBytes, ct, entry.Sha256, entry.Bytes);
                ct.ThrowIfCancellationRequested(); DurableFile.Flush(temp); File.Move(temp, output);
            }
            finally { TryDelete(temp); }
        }
        foreach (var reel in package.Content.Reels.DistinctBy(r => r.Media.Id))
        {
            var media = reel.Media with { Id = AssetReusePolicy.Identity(command.Id, "reel-media", reel.Media.Id) };
            var path = ReusePath(destination, $"reference-videos/{media.Id:D}/media.json");
            if (File.Exists(path) && await AtomicJsonFile.ReadAsync<ReferenceVideoMedia>(path, ct) != media)
                throw new WorkspaceStoreException("A different reel media record occupies the copy destination.");
            await AtomicJsonFile.WriteAsync(path, media, ct);
            installed.Add(new($"reference-videos/{media.Id:D}/media.json", new FileInfo(path).Length, await ReuseFileHashAsync(path, ct)));
        }
        return installed;
    }
    private static async Task ValidateReuseArchivesAsync(AssetReusePackage package, string root, CancellationToken ct)
    {
        foreach (var group in package.Content.Reels.GroupBy(r => r.Media.Id))
        {
            var media = group.First().Media;
            if (group.Any(r => r.Media != media)) throw new WorkspaceStoreException("Shared reels disagree about their media record.");
            var prefix = "reels/" + media.Id.ToString("D") + "/";
            var video = package.Files.Single(f => f.Path == prefix + "video.mp4");
            if (video.Bytes != media.Bytes || !video.Sha256.Equals(media.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceStoreException("The shared video does not match its media record.");
            var archiveFile = package.Files.SingleOrDefault(f => f.Path == prefix + "frame-archive.json");
            string? archiveIdentity = null;
            if (archiveFile is not null)
            {
                if (archiveFile.Bytes > 4 * 1024 * 1024) throw new WorkspaceStoreException("The reel archive index is too large.");
                var path = ReusePath(root, archiveFile.Path);
                if (new FileInfo(path).Length != archiveFile.Bytes || !string.Equals(await ReuseFileHashAsync(path, ct), archiveFile.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new WorkspaceStoreException("The shared reel archive index changed.");
                var archive = await AtomicJsonFile.ReadAsync<ReelFrameArchive>(path, ct);
                if (archive is null || archive.SourceSha256 != media.Sha256 || archive.Width != media.Width || archive.Height != media.Height ||
                    archive.FrameCount != media.Frames || archive.Frames is null || archive.Frames.Count != media.Frames || archive.Files is null ||
                    archive.Files.Any(f => f is null || !ReuseRelativePath(f.FileName) || f.FileName.Contains('/')) ||
                    archive.Files.Select(f => f.FileName).Distinct(StringComparer.Ordinal).Count() != archive.Files.Count ||
                    archive.Frames.Any(f => f is null || !archive.Files.Any(s => s.FileName == f.FileName)) ||
                    !archive.Frames.Select(f => f.Index).SequenceEqual(Enumerable.Range(0, media.Frames)))
                    throw new WorkspaceStoreException("The shared lossless archive does not match its reel.");
                foreach (var segment in archive.Files) {
                    var entry = package.Files.SingleOrDefault(f => f.Path == prefix + "lossless/" + segment.FileName);
                    if (entry is null || entry.Bytes != segment.Bytes || !entry.Sha256.Equals(segment.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new WorkspaceStoreException("The shared lossless archive is incomplete or changed.");
                }
                archiveIdentity = AssetReusePolicy.Hash(archive);
            }
            foreach (var reel in group.Where(r => r.Keyframes is not null)) {
                ReferenceVideos.ValidateKeyframes(media, reel.Keyframes!);
                if (reel.Keyframes!.Frames.Any(f => f.Frame.Source != media.Sha256 && f.Frame.Source != archiveIdentity))
                    throw new WorkspaceStoreException("A shared keyframe's source archive is unavailable.");
            }
        }
    }
    private static async Task VerifyReuseDestinationAsync(string destination, AssetReuseCommand command,
        AssetReuseContent content, AssetLibrary library, CancellationToken ct)
    {
        var assetId = command.Destination.AssetId ?? AssetReusePolicy.Identity(command.Id, "asset", content.Asset.Id);
        var owner = library.Assets.SingleOrDefault(a => a.Id == assetId);
        if (owner is null || content.Asset.Images.Any(i => !owner.Images.Any(c => c.Id == AssetReusePolicy.Identity(command.Id, "image", i.Id))) ||
            content.Reels.Any(r => !library.Reels.Any(c => c.AssetId == assetId && c.Id == AssetReusePolicy.Identity(command.Id, "reel", r.Id))) ||
            content.Voices.Any(v => !library.Voices.Any(c => c.AssetId == assetId && c.Id == AssetReusePolicy.Identity(command.Id, "voice", v.Id))))
            throw new WorkspaceStoreException("Some destination media was moved or removed. The source has been kept.");
        var manifest = await AtomicJsonFile.ReadAsync<AssetReuseInstalledFiles>(ReusePath(destination,
            $"asset-reuse-operations/{command.Id:D}.json.files.json"), ct);
        if (manifest is null || manifest.CommandId != command.Id || manifest.Fingerprint != AssetReusePolicy.Hash(command) || manifest.Files is null ||
            manifest.Files.Any(f => f is null || !ReuseRelativePath(f.Path) || f.Bytes <= 0 || !AssetReusePolicy.IsHash(f.Sha256)))
            throw new WorkspaceStoreException("The destination's media-copy receipt is unavailable. The source has been kept.");
        var required = content.Asset.Images.Select(i => $"assets/{assetId:D}/images/{AssetReusePolicy.Identity(command.Id, "image", i.Id):N}{Path.GetExtension(i.FileName)}")
            .Concat(content.Voices.Select(v => $"assets/{assetId:D}/voices/{AssetReusePolicy.Identity(command.Id, "voice", v.Id):N}{Path.GetExtension(v.FileName)}"))
            .Concat(content.Reels.SelectMany(r => new[] { "video.mp4", "media.json" }.Select(name =>
                $"reference-videos/{AssetReusePolicy.Identity(command.Id, "reel-media", r.Media.Id):D}/{name}")));
        if (required.Any(path => !manifest.Files.Any(f => f.Path == path)))
            throw new WorkspaceStoreException("The destination's media-copy receipt is incomplete. The source has been kept.");
        foreach (var file in manifest.Files)
        {
            var path = ReusePath(destination, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Bytes ||
                !string.Equals(await ReuseFileHashAsync(path, ct), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new WorkspaceStoreException("The destination media is missing or changed. The source has been kept.");
        }
    }
    internal static bool ReuseRelativePath(string? path) => path is { Length: > 0 and <= 512 } &&
        !path.StartsWith('/') && !path.Contains('\\') && path.Split('/').All(p => p.Length > 0 && p is not ("." or "..") &&
            p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));
    private static string ReusePath(string root, string relative)
    {
        if (!ReuseRelativePath(relative)) throw new WorkspaceStoreException("Invalid asset media path.");
        var current = Path.GetFullPath(root);
        EnsureNoReuseLink(current);
        foreach (var part in relative.Split('/')) { current = Path.Combine(current, part); EnsureNoReuseLink(current); }
        return current;
    }
    private static void EnsureNoReuseLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new WorkspaceStoreException("Linked media paths cannot be copied into the shared library. Import the actual file first.");
    }
    private static async Task<string> ReuseFileHashAsync(string path, CancellationToken ct)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)); }
    private static async Task<AssetReuseFile> CopyReuseFileAsync(string source, string destination, long maximum,
        CancellationToken ct, string? expectedHash = null, long? expectedBytes = null)
    {
        EnsureNoReuseLink(source);
        var length = new FileInfo(source).Length;
        if (length <= 0 || length > maximum || expectedBytes is { } expected && length != expected)
            throw new WorkspaceStoreException("An asset media file is empty, oversized, or has changed.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long copied = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            copied += read;
            if (copied > maximum || copied > length) throw new WorkspaceStoreException("The source media changed while copying.");
            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await output.FlushAsync(ct);
        var digest = Convert.ToHexString(hash.GetHashAndReset());
        if (copied != length || expectedHash is not null && !string.Equals(digest, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new WorkspaceStoreException("Asset media failed its integrity check. No library entry has been published.");
        return new("", copied, digest);
    }
}
