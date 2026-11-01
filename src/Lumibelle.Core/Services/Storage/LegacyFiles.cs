namespace lumibelle.Services.Storage;

/// <summary>
/// Project JSON written before SQLite storage. After a document is brought into the database its
/// files are moved to <c>&lt;project&gt;/legacy-json/</c> as a backup and never read again.
/// </summary>
internal static class LegacyFiles
{
    public const string Folder = "legacy-json";

    public static bool Any(string directory, IReadOnlyList<string> legacy) =>
        legacy.Any(relative => File.Exists(Path.Combine(directory, relative)) || Directory.Exists(Path.Combine(directory, relative)));

    /// <summary>Existing legacy paths, relative to the project folder, including unpublished temporary files beside them.</summary>
    public static List<string> Existing(string directory, IReadOnlyList<string> legacy)
    {
        List<string> found = [];
        foreach (var relative in legacy)
        {
            var path = Path.Combine(directory, relative);
            if (File.Exists(path) || Directory.Exists(path)) found.Add(relative);
            var parent = Path.GetDirectoryName(path)!;
            if (Directory.Exists(parent) && !Directory.Exists(path))
                found.AddRange(Directory.EnumerateFiles(parent, Path.GetFileName(path) + ".*.tmp")
                    .Select(f => Path.GetRelativePath(directory, f)));
        }
        return found;
    }

    /// <summary>Moves each path into the legacy folder. Repeating it after an interruption is safe.</summary>
    public static async Task MoveAsync(string directory, IReadOnlyList<string> relatives, CancellationToken ct)
    {
        foreach (var relative in relatives)
        {
            var source = Path.Combine(directory, relative);
            var target = Path.Combine(directory, Folder, relative);
            if (Directory.Exists(source))
            {
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList())
                    await MoveFileAsync(file, Path.Combine(target, Path.GetRelativePath(source, file)), ct);
                try { Directory.Delete(source, recursive: false); }
                catch (IOException) { /* A folder that is not yet empty is left in place; its files were moved. */ }
            }
            else if (File.Exists(source)) await MoveFileAsync(source, target, ct);
        }
    }

    private static async Task MoveFileAsync(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            // An earlier, interrupted move already placed this copy.
            if (new FileInfo(target).Length == new FileInfo(source).Length) { File.Delete(source); return; }
            target = $"{target}.{DateTime.UtcNow.Ticks}.duplicate";
        }
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { File.Move(source, target); return; }
            // Windows scanners can briefly hold a file open.
            catch (Exception e) when (OperatingSystem.IsWindows() && attempt < 4 && e is IOException or UnauthorizedAccessException && File.Exists(source))
            { await Task.Delay(TimeSpan.FromMilliseconds(20 * (1 << attempt)), ct); }
        }
    }
}
