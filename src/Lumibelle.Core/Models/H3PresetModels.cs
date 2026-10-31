using System.Text.Json;

namespace lumibelle.Models;

// Null on earlier snapshots: those requests retain their original graph and archives.
public sealed record H3PresetProfile(string Version, string Key, string? Checkpoint, JsonElement Inputs);
public sealed record H3OutputPolicy(bool SaveLosslessFrames)
{
    // Single output frames kept losslessly as PNGs when the full archive is off, such as frames a later
    // clip in a refinement batch uses as references. Absent on other requests, so their graphs are unchanged.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? KeptFrames { get; init; }
    public const int MaximumKeptFrames = 16;
    public bool Equals(H3OutputPolicy? other) => other is not null && SaveLosslessFrames == other.SaveLosslessFrames &&
        (KeptFrames ?? []).SequenceEqual(other.KeptFrames ?? []);
    public override int GetHashCode() => HashCode.Combine(SaveLosslessFrames, KeptFrames?.Count ?? 0);
}
public sealed record H3PresetSetup(string Key, string? Issue, IReadOnlyList<string> Files);
