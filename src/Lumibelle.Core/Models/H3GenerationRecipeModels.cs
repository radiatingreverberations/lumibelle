using System.Text.Json;

namespace lumibelle.Models;

public sealed record H3ModelIdentity(string Category, string FileName, string? Sha256 = null,
    long? Bytes = null, string? Quantization = null);
public sealed record H3RuntimeIdentity(IReadOnlyDictionary<string, string?> Versions,
    IReadOnlyDictionary<string, string?> NodeSources);
public sealed record H3RecipeEvidence(IReadOnlyList<H3ModelIdentity> Models, H3RuntimeIdentity Runtime);
public sealed record H3GenerationRecipe(string Version, string Fingerprint, JsonElement Parameters,
    IReadOnlyList<H3ModelIdentity> Models, H3RuntimeIdentity Runtime, bool ProbeAvailable);
public sealed record H3ExecutionReceipt(string Version, string RecipeFingerprint, long Seed, string AttemptId,
    string Status, IReadOnlyList<double> Sigmas, H3RecipeEvidence Evidence, JsonElement Observation)
{
    public string? ComfyPromptId { get; init; }
    public string? ComfyClientId { get; init; }
}
