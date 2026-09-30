using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

/// <summary>
/// Visual briefs written by the first step of a two-step composition, content-addressed by everything that shaped
/// them: brief profile, model and server, image canvas size and the exact brief messages including image bytes.
/// Revisions and other shots with the same references reuse a brief instead of inspecting the images again.
/// </summary>
public sealed class VisualBriefCache(ApplicationPaths paths)
{
    private sealed record Entry(string Key, string Brief, DateTimeOffset CreatedUtc);

    public static string Key(TextModelReference model, int batchImageSide, IReadOnlyList<AiTextMessage> briefMessages)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile = Production.PromptComposer.BriefProfile,
            server = AiProviderRegistry.NormalizeComfyUrl(model.ComfyUrl ?? ""),
            model = model.Model,
            batchImageSide
        }));
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(briefMessages, AtomicJsonFile.Options));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public async Task<string?> ReadAsync(string key, CancellationToken ct = default)
    {
        try { return (await AtomicJsonFile.ReadAsync<Entry>(Path(key), ct)) is { } entry && entry.Key == key ? entry.Brief : null; }
        // A missing or unreadable entry only means the images are inspected again.
        catch (Exception e) when (e is WorkspaceStoreException or IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public async Task WriteAsync(string key, string brief, CancellationToken ct = default)
    {
        try { await AtomicJsonFile.WriteAsync(Path(key), new Entry(key, brief, DateTimeOffset.UtcNow), ct); }
        catch (Exception e) when (e is WorkspaceStoreException or IOException or UnauthorizedAccessException) { /* The composition itself does not depend on the cache. */ }
    }

    private string Path(string key) => key.Length == 64 && key.All(char.IsAsciiHexDigit)
        ? System.IO.Path.Combine(paths.Data, "cache", "visual-briefs", key + ".json")
        : throw new WorkspaceStoreException("Invalid visual brief key.");
}
