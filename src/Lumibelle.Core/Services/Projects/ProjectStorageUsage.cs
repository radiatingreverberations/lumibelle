using System.Text.RegularExpressions;

namespace lumibelle.Services.Projects;

public enum StorageKind { TakeVideos, TakeArchives, TakeData, GenerationRuns, ReelVideos, ReelArchives, ReelFrames, Images, Voices, ProjectData, Other }
public sealed record StorageCategory(StorageKind Kind, long Bytes, int Files);
public sealed record StorageFile(string Path, long Bytes);

/// <summary>
/// What a project's folder holds, by kind, and its largest files: for deciding what to compact,
/// or what to keep out of version control.
/// </summary>
public sealed partial record ProjectStorageUsage(long Total, IReadOnlyList<StorageCategory> Categories, IReadOnlyList<StorageFile> Largest)
{
    /// <summary>Git hosts such as GitHub reject single files over 100 MB unless they use large file storage.</summary>
    public const long LargeFileBytes = 100L * 1000 * 1000;

    public static string Label(StorageKind kind) => kind switch
    {
        StorageKind.TakeVideos => "Take videos",
        StorageKind.TakeArchives => "Lossless take frames",
        StorageKind.TakeData => "Take frames and refinement data",
        StorageKind.GenerationRuns => "Generation working files (inputs and candidates)",
        StorageKind.ReelVideos => "Reel videos",
        StorageKind.ReelArchives => "Lossless reel archives",
        StorageKind.ReelFrames => "Reel frames and previews",
        StorageKind.Images => "Images",
        StorageKind.Voices => "Voice recordings",
        StorageKind.ProjectData => "Script, shots and other project data",
        _ => "Other files"
    };

    public static ProjectStorageUsage Measure(string directory, int largest = 5)
    {
        var root = Path.GetFullPath(directory);
        var totals = new Dictionary<StorageKind, (long Bytes, int Files)>();
        var files = new List<StorageFile>();
        if (Directory.Exists(root))
            // Linked folders are not part of the project's own files.
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                var kind = Classify(relative);
                var (bytes, count) = totals.GetValueOrDefault(kind);
                totals[kind] = (bytes + file.Length, count + 1);
                files.Add(new(relative, file.Length));
            }
        return new(totals.Values.Sum(t => t.Bytes),
            [.. totals.Select(t => new StorageCategory(t.Key, t.Value.Bytes, t.Value.Files)).OrderByDescending(c => c.Bytes)],
            [.. files.OrderByDescending(f => f.Bytes).Take(largest)]);
    }

    internal static StorageKind Classify(string relative)
    {
        var parts = relative.Split('/');
        var name = parts[^1];
        return parts switch
        {
            ["shots", "takes", _, "video.mp4"] => StorageKind.TakeVideos,
            ["shots", "takes", ..] when ArchiveFile().IsMatch(name) => StorageKind.TakeArchives,
            ["shots", "takes", ..] => StorageKind.TakeData,
            ["shots", "runs", ..] or ["reel-runs", ..] => StorageKind.GenerationRuns,
            ["reference-videos", _, "video.mp4"] => StorageKind.ReelVideos,
            ["reference-videos", _, "lossless", ..] => StorageKind.ReelArchives,
            ["reference-videos", ..] or ["refmod-previews", ..] => StorageKind.ReelFrames,
            ["assets", _, "images", ..] => StorageKind.Images,
            ["assets", _, "voices", ..] or ["voice-imports", ..] => StorageKind.Voices,
            [_] when name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => StorageKind.ProjectData,
            ["script-sources" or "script-history" or "script-approved", ..] => StorageKind.ProjectData,
            _ => StorageKind.Other
        };
    }

    [GeneratedRegex(@"^archive-\d{4}\.webp$")]
    private static partial Regex ArchiveFile();
}
