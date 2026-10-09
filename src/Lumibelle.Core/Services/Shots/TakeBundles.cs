using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class TakeBundles
{
    public static IEnumerable<(ShotTake Take, string Prefix)> Contexts(ShotTake take, string prefix = "")
    {
        yield return (take, prefix);
        foreach (var segment in take.Composition?.Segments ?? []) yield return (segment.Source, prefix + "segments/" + segment.Key.ToString("D") + "/");
        if (take.Extension is { } extension)
            foreach (var item in Contexts(extension.Source, prefix + H3Motion.SourceFolder + "/")) yield return item;
    }
    public static IReadOnlyList<FrameArchiveFile> RemoveArchives(ShotTake take, DateTimeOffset now)
    {
        var removed = new List<FrameArchiveFile>();
        foreach (var (context, prefix) in Contexts(take)) {
            var files = context.Frames.DistinctBy(f => f.FileName).Select(f => new FrameArchiveFile(f.FileName, f.Bytes)).ToArray();
            if (files.Length == 0) continue;
            removed.AddRange(files.Select(f => f with { FileName = prefix + f.FileName }));
            context.FrameArchiveRemoval = new(now, files, now); context.Frames = [];
        }
        if (take.Extension is { } extension) take.Extension = extension with { SourceFiles = extension.SourceFiles.Where(f => !removed.Any(r => r.FileName == H3Motion.SourceFolder + "/" + f.FileName)).ToArray() };
        return removed;
    }
    public static IEnumerable<string> Files(ShotTake take, string prefix = "")
    {
        yield return prefix + "video.mp4";
        if (take.Composition?.HasJoinPreview == true) yield return prefix + "join-preview.mp4";
        foreach (var frame in take.Frames.DistinctBy(f => f.FileName)) yield return prefix + frame.FileName;
        if (take.RefinementPackage is not null) yield return prefix + H3RefinementPackage.FileName;
        if (take.RetainedSource?.RefinementInput is not null) yield return prefix + TakeTrimming.InputsFolder + "/" + TakeTrimming.RefinementInputFile;
        foreach (var input in take.RetainedSource?.Inputs ?? []) yield return prefix + TakeTrimming.InputsFolder + "/" + input.FileName;
        if (take.RetainedSource is not null) foreach (var input in take.Snapshot.Motion?.Files ?? []) yield return prefix + TakeTrimming.InputsFolder + "/" + input.FileName;
        foreach (var segment in take.Composition?.Segments ?? [])
            foreach (var file in Files(segment.Source, prefix + "segments/" + segment.Key.ToString("D") + "/")) yield return file;
        if (take.Extension is { } extension)
            foreach (var file in extension.SourceFiles) yield return prefix + H3Motion.SourceFolder + "/" + file.FileName;
    }
    public static string Under(string directory, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is "" or "." or ".."))
            throw new WorkspaceStoreException("Invalid retained take bundle path.");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new WorkspaceStoreException("Invalid retained take bundle path.");
        for (var entry = path; entry.Length >= root.Length; entry = Path.GetDirectoryName(entry)!)
            if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new WorkspaceStoreException("Retained media cannot follow linked files.");
        return path;
    }
    public static async Task<IReadOnlyList<CapturedMotionFile>> CopyAsync(ShotTake take, string source, string target, CancellationToken ct)
    {
        List<CapturedMotionFile> files = [];
        foreach (var (context, prefix) in Contexts(take)) {
            if (context.RefinementPackage is { } package) await RefinementPackages.VerifyFileAsync(Under(source, prefix + H3RefinementPackage.FileName), package.Bytes, package.Sha256, ct);
            if (context.RetainedSource?.RefinementInput is { } original) await RefinementPackages.VerifyFileAsync(Under(source, prefix + TakeTrimming.InputsFolder + "/" + TakeTrimming.RefinementInputFile), original.Bytes, original.Sha256, ct);
            foreach (var input in context.RetainedSource?.Inputs ?? []) await RefinementPackages.VerifyFileAsync(Under(source, prefix + TakeTrimming.InputsFolder + "/" + input.FileName), input.Bytes, input.Sha256, ct);
            if (context.RetainedSource is not null) foreach (var input in context.Snapshot.Motion?.Files ?? []) await RefinementPackages.VerifyFileAsync(Under(source, prefix + TakeTrimming.InputsFolder + "/" + input.FileName), input.Bytes, input.Sha256, ct);
        }
        foreach (var relative in Files(take).Distinct(StringComparer.Ordinal)) {
            var path = Under(source, relative);
            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            var output = Under(target, relative); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await TakeTrimming.CopyVerifiedAsync(path, output, stream.Length, hash, ct);
            files.Add(new(relative, stream.Length, hash));
        }
        return files;
    }
    public static async Task CopyCapturedAsync(IReadOnlyList<CapturedMotionFile> files, string source, string target, CancellationToken ct)
    {
        foreach (var file in files) {
            var output = Under(target, file.FileName); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await TakeTrimming.CopyVerifiedAsync(Under(source, file.FileName), output, file.Bytes, file.Sha256, ct);
        }
    }
    public static void Validate(ShotTake take)
    {
        if (take.Extension is { } extension) Validate(extension, take.Snapshot.ProjectId);
        if (take.Composition is not { } composition) return;
        if (composition.Segments is not { Count: > 0 } || composition.JoinFrame < 0 || composition.JoinFrame >= take.FrameCount ||
            composition.Segments.Any(s => s.Key == Guid.Empty || s.Source is null || s.Source.Composition is not null || s.Source.Extension is not null ||
                s.Source.Width != take.Width || s.Source.Height != take.Height || s.Source.Fps != take.Fps || s.StartFrame < 0 || s.EndFrameExclusive <= s.StartFrame || s.EndFrameExclusive > s.Source.FrameCount) ||
            composition.Segments.DistinctBy(s => s.Key).Count() != composition.Segments.Count)
            throw new WorkspaceStoreException("Invalid retained take segments.");
        foreach (var segment in composition.Segments) FileShotStore.Validate(new() { ProjectId = take.Snapshot.ProjectId, Takes = [segment.Source] }, take.Snapshot.ProjectId);
    }
    public static void Validate(TakeExtensionRequest extension, Guid projectId)
    {
        if (extension.Source is null || extension.Source.Extension is not null || extension.Source.Snapshot.ProjectId != projectId || extension.SourceFiles is null ||
            extension.SourceFiles.DistinctBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).Count() != extension.SourceFiles.Count ||
            !Files(extension.Source).ToHashSet(StringComparer.Ordinal).SetEquals(extension.SourceFiles.Select(f => f.FileName)))
            throw new WorkspaceStoreException("Invalid captured extension bundle.");
        TakeTrimming.Range(extension.PrefixStartFrame, extension.RetainedFrames, extension.Source.FrameCount);
        FileShotStore.Validate(new() { ProjectId = projectId, Takes = [extension.Source] }, projectId);
        foreach (var file in extension.SourceFiles) {
            _ = Under(Path.GetTempPath(), file.FileName);
            if (file.Bytes <= 0 || !RefinementPolicy.Hash(file.Sha256)) throw new WorkspaceStoreException("Invalid captured extension file.");
        }
    }
    public static IReadOnlyList<TakeSegment> Range(ShotTake source, int start, int end)
    {
        TakeTrimming.Range(start, end, source.FrameCount);
        if (source.Composition is null) {
            var leaf = ShotCopy.Of(source); leaf.Extension = null;
            return [new(Guid.NewGuid(), leaf, start, end)];
        }
        var result = new List<TakeSegment>(); var cursor = 0;
        foreach (var segment in source.Composition.Segments) {
            var length = segment.EndFrameExclusive - segment.StartFrame;
            if (start < cursor + length && end > cursor) result.Add(segment with { Key = Guid.NewGuid(),
                StartFrame = segment.StartFrame + Math.Max(0, start - cursor), EndFrameExclusive = segment.StartFrame + Math.Min(length, end - cursor) });
            cursor += length;
        }
        return result;
    }
}
