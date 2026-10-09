using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Projects;

// Only this version's published documents and named media are packageable. In particular,
// never recurse through a project directory, an AI queue, or a global settings directory.
internal static class ProjectPackageFormat
{
    internal const long MaxArchiveBytes = 64L * 1024 * 1024 * 1024;
    internal const long MaxExpandedBytes = 256L * 1024 * 1024 * 1024;
    internal const int MaxFiles = 100_000;
    internal const int MaxJsonBytes = 64 * 1024 * 1024;
    internal const long MaxJsonTotalBytes = 256L * 1024 * 1024;
    internal static readonly string[] Documents = ["project.json", "script.json", "assets.json", "shots.json", "production.json", "cut.json", "ai-preferences.json", "script-assistant.json"];
    internal static readonly string[] ScriptFolders = ["script-sources", "script-approved", "script-history"];
    internal static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    internal static readonly string[] AudioExtensions = [".wav", ".mp3", ".flac", ".m4a", ".ogg"];
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static bool Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    internal static void Relative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || path.Contains('\\') || path.Contains('%') ||
            path.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)) || !path.IsNormalized(NormalizationForm.FormC) || Path.IsPathRooted(path))
            throw new WorkspaceStoreException("The package contains an unsafe file path.");
        foreach (var part in path.Split('/'))
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]))
                throw new WorkspaceStoreException("The package contains a non-portable file path.");
        }
    }
    internal static bool Allowed(string path)
    {
        Relative(path);
        if (Documents.Contains(path, StringComparer.Ordinal)) return true;
        var p = path.Split('/');
        if (p.Length == 2 && ScriptFolders.Contains(p[0]) && p[1].EndsWith(".json", StringComparison.Ordinal) && Id(p[1][..^5])) return true;
        if (p.Length == 4 && p[0] == "assets" && Id(p[1]))
            return p[2] == "images" && ImageExtensions.Contains(Path.GetExtension(p[3]).ToLowerInvariant()) ||
                p[2] == "voices" && AudioExtensions.Contains(Path.GetExtension(p[3]).ToLowerInvariant());
        if (p.Length == 4 && p[0] == "shots" && p[1] == "takes" && Id(p[2]))
            return p[3] is "video.mp4" or "refinement.safetensors" || ArchiveName(p[3]);
        if (p.Length == 5 && p[0] == "shots" && p[1] == "takes" && Id(p[2]) && p[3] == "refinement-inputs")
            return p[4] == Path.GetFileName(p[4]) && Path.GetExtension(p[4]) is ".png" or ".wav" or ".mp4";
        if (p.Length == 3 && p[0] == "reference-videos" && Id(p[1]))
            return p[2] is "media.json" or "video.mp4" or "frame-archive.json" || KeyframeName(p[2]);
        if (p.Length == 4 && p[0] == "reference-videos" && Id(p[1]) && p[2] == "lossless") return ArchiveName(p[3]);
        return p.Length == 3 && p[0] == "refmod-previews" && Hash(p[1]) &&
            p[2].Length == 12 && p[2].StartsWith("frame-", StringComparison.Ordinal) && p[2].EndsWith(".png", StringComparison.Ordinal) &&
            int.TryParse(p[2].AsSpan(6, 2), out var frame) && frame is >= 1 and <= 9;
    }
    private static bool ArchiveName(string name) => name.Length == 17 && name.StartsWith("archive-", StringComparison.Ordinal) &&
        name.EndsWith(".webp", StringComparison.Ordinal) && name.AsSpan(8, 4).ToString().All(char.IsAsciiDigit);
    // A reel keyframe extracted from a lossless archive that the package leaves out.
    private static bool KeyframeName(string name) => name.Length == 81 && name.StartsWith("frame-", StringComparison.Ordinal) &&
        Hash(name[6..70]) && name[70] == '-' && name[71..77].All(char.IsAsciiDigit) && name.EndsWith(".png", StringComparison.Ordinal);
    internal static string Under(string root, string relative)
    {
        Relative(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.Ordinal)) throw new WorkspaceStoreException("A file escapes the package directory.");
        return full;
    }
    internal static void NoLinks(string root, string path)
    {
        var stop = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(path);
        if (current != stop && !current.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new WorkspaceStoreException("A file escapes its project.");
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new WorkspaceStoreException("Project packages do not follow symbolic links or reparse points.");
            if (current == stop) break;
            current = Path.GetDirectoryName(current) ?? throw new WorkspaceStoreException("Invalid project path.");
        }
    }
    internal static void ZipEntry(ZipArchiveEntry entry)
    {
        Relative(entry.FullName);
        var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
        if (unixType is not (0 or 0x8000) || (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new WorkspaceStoreException("Package links, directories and special files are not supported.");
    }
    internal static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, AtomicJsonFile.Options);
    internal static void StrictJson(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxJsonBytes) throw new WorkspaceStoreException("Package metadata exceeds 64 MiB per document.");
        // JsonSerializer otherwise accepts duplicate properties with last-value-wins semantics.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var objects = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: objects.Push(new(StringComparer.OrdinalIgnoreCase)); break;
                case JsonTokenType.StartArray: objects.Push(null); break;
                case JsonTokenType.EndObject: case JsonTokenType.EndArray: objects.Pop(); break;
                case JsonTokenType.PropertyName:
                    if (!objects.Peek()!.Add(reader.GetString()!)) throw new WorkspaceStoreException("Duplicate metadata properties are not supported.");
                    break;
            }
        }
    }
    internal static T Parse<T>(byte[] bytes)
    {
        StrictJson(bytes);
        return JsonSerializer.Deserialize<T>(bytes, AtomicJsonFile.Options) ?? throw new WorkspaceStoreException("Package metadata is null.");
    }
    internal static async Task<T?> ReadAsync<T>(string root, string relative, CancellationToken ct)
    {
        var path = Under(root, relative);
        if (!File.Exists(path)) return default;
        NoLinks(root, path);
        if (new FileInfo(path).Length > MaxJsonBytes) throw new WorkspaceStoreException("Project metadata exceeds the package limit.");
        return Parse<T>(await File.ReadAllBytesAsync(path, ct));
    }
    internal static async Task<ProjectPackageFile> CopyAsync(Stream source, Stream target, string name, long maximum,
        CancellationToken ct, Action<long>? copied = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072]; long bytes = 0; int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            bytes = checked(bytes + read);
            if (bytes > maximum) throw new WorkspaceStoreException("The package exceeds its declared size or transfer limit.");
            hash.AppendData(buffer, 0, read); await target.WriteAsync(buffer.AsMemory(0, read), ct); copied?.Invoke(bytes);
        }
        return new(name, bytes, Convert.ToHexString(hash.GetHashAndReset()));
    }
    internal static async Task VerifyAsync(string root, ProjectPackageFile file, CancellationToken ct)
    {
        var path = Under(root, file.Path); NoLinks(root, path);
        await using var source = File.OpenRead(path);
        if (source.Length != file.Bytes || await CopyAsync(source, Stream.Null, file.Path, file.Bytes, ct) != file)
            throw new WorkspaceStoreException("A package file is missing, changed, or has an invalid checksum.");
    }
}
