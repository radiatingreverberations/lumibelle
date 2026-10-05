using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

/// <summary>
/// Prepared generation inputs, stored once per project by content in <c>shots/input-store/&lt;SHA-256&gt;.&lt;ext&gt;</c>.
/// Every run used to keep its own copy of the same cropped references and voice excerpts. A run's request records
/// each input's name and hash, so its files are found by hash; the run's own <c>inputs</c> folder is looked at first,
/// for runs captured before the store, and stays (even empty) as the record that the run's inputs were captured.
/// </summary>
public static class CapturedInputStore
{
    public const string Folder = "input-store";
    // Refinement inputs are verified and uploaded by their own paths.
    private static readonly string[] Kept = [H3RefinementPackage.FileName, "source.mp4"];

    public static bool Shares(string fileName) => !Kept.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    public static string Root(string projectDirectory) => Path.Combine(projectDirectory, "shots", Folder);

    /// <summary>The project folder holding a run folder, such as <c>shots/runs/&lt;id&gt;</c> or <c>reel-runs/&lt;id&gt;</c>.</summary>
    public static string? ProjectRoot(string runDirectory)
    {
        var run = new DirectoryInfo(Path.GetFullPath(runDirectory));
        return run.Parent is { Name: "runs", Parent: { Name: "shots", Parent: { } shotsProject } } ? shotsProject.FullName
            : run.Parent is { Name: "reel-runs", Parent: { } reelsProject } ? reelsProject.FullName : null;
    }
    /// <summary>Whether two run folders can share stored inputs: both are generation folders of the same project.</summary>
    public static bool SameProject(string runDirectory, string otherRunDirectory) =>
        ProjectRoot(runDirectory) is { } project && string.Equals(project, ProjectRoot(otherRunDirectory), StringComparison.OrdinalIgnoreCase);

    public static string StoredPath(string projectDirectory, string fileName, string sha256) =>
        Path.Combine(Root(projectDirectory), sha256.ToUpperInvariant() + Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Where a captured input's bytes are: the run's own copy if it still has one, otherwise the store.</summary>
    public static string Resolve(string runDirectory, string fileName, string? sha256)
    {
        var local = Path.Combine(runDirectory, "inputs", fileName);
        return File.Exists(local) || sha256 is null || !Shares(fileName) || ProjectRoot(runDirectory) is not { } project ? local : StoredPath(project, fileName, sha256);
    }

    /// <summary>Moves a run's captured files into the store, keeping one copy of each content.</summary>
    /// <returns>The bytes no longer stored twice.</returns>
    public static long Share(string runDirectory, IEnumerable<(string FileName, string Sha256)> inputs)
    {
        // Outside a project's generation folders there is no store to share; the files stay.
        if (ProjectRoot(runDirectory) is not { } project) return 0;
        long freed = 0;
        foreach (var (name, sha) in inputs)
        {
            var local = Path.Combine(runDirectory, "inputs", name);
            if (!Shares(name) || !File.Exists(local)) continue;
            var stored = StoredPath(project, name, sha);
            Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
            var bytes = new FileInfo(local).Length;
            if (File.Exists(stored)) { File.Delete(local); freed += bytes; continue; }
            try { File.Move(local, stored); }
            // Another run stored the same content meanwhile.
            catch (IOException) when (File.Exists(stored)) { File.Delete(local); freed += bytes; }
        }
        return freed;
    }

    public static long Share(string runDirectory, IEnumerable<AiVideoInput> inputs) => Share(runDirectory, inputs.Select(i => (i.FileName, i.Sha256)));

    /// <summary>The shareable files a run still keeps in its own inputs folder.</summary>
    public static IEnumerable<FileInfo> LocalFiles(string runDirectory)
    {
        var folder = Path.Combine(runDirectory, "inputs");
        return Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles().Where(f => Shares(f.Name) && !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            : [];
    }

    /// <summary>For runs captured before the store: hashes the files still in their inputs folder and shares them.</summary>
    public static async Task<long> ShareExistingAsync(string runDirectory, CancellationToken ct)
    {
        List<(string, string)> files = [];
        foreach (var file in LocalFiles(runDirectory).ToArray())
            files.Add((file.Name, await HashAsync(file.FullName, ct)));
        return Share(runDirectory, files);
    }

    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
}
