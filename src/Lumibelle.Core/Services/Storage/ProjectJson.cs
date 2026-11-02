using System.Text.Json;
using lumibelle.Services.Story;

namespace lumibelle.Services.Storage;

/// <summary>
/// Compact JSON for stored rows and in-memory copies. <see cref="AtomicJsonFile.Options"/> stays
/// indented and unchanged, because fingerprints and publication receipts hash its exact output.
/// </summary>
public static class ProjectJson
{
    public static readonly JsonSerializerOptions Compact = new(AtomicJsonFile.Options) { WriteIndented = false };

    public static T Copy<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Compact), Compact)!;
}
