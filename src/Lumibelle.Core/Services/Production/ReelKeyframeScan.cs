using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

// Saved keyframes are copied into shot bindings, production setups and captured take snapshots.
// Each is a "frame" identity beside its "notes"; a saved-frame image's provenance has no notes.
public static class ReelKeyframeScan
{
    public static ReelFrameIdentity? Read(JsonObject o) =>
        o["frame"] is JsonObject frame && o.ContainsKey("notes") && frame.ContainsKey("source") && frame.ContainsKey("index")
            ? frame.Deserialize<ReelFrameIdentity>(AtomicJsonFile.Options) : null;
    public static void Collect(JsonNode? node, ISet<ReelFrameIdentity> found)
    {
        if (node is JsonArray array) { foreach (var item in array) Collect(item, found); return; }
        if (node is not JsonObject o) return;
        if (Read(o) is { } keyframe) found.Add(keyframe);
        foreach (var entry in o) Collect(entry.Value, found);
    }
    // Every top-level project document, including backups: keeping an extra picture is harmless.
    public static async Task<HashSet<ReelFrameIdentity>> ProjectAsync(string directory, CancellationToken ct)
    {
        HashSet<ReelFrameIdentity> found = [];
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            await using var input = File.OpenRead(file);
            Collect(await JsonNode.ParseAsync(input, cancellationToken: ct), found);
        }
        return found;
    }
}
