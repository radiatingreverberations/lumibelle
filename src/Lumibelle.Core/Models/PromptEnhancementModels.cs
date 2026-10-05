using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace lumibelle.Models;

public sealed record PromptReference(AssetImageReference Image, string Label, string Notes, ImageCropRegion? Crop, bool Available = true)
{
    public AssetLookContext? Look { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RegionalImageSelection? Region { get; init; }
}

public sealed record PromptEnhancementContext
{
    public AssetLookContext? TargetLook { get; init; }
    public required Guid ProjectId { get; init; }
    public required Guid AssetId { get; init; }
    public required string Prompt { get; init; }
    public string AssetName { get; init; } = "";
    public AssetCategory Category { get; init; }
    public string VisualNotes { get; init; } = "";
    public ImageWorkflow Workflow { get; init; }
    public bool IsEdit { get; init; }
    public string AspectRatio { get; init; } = "1:1";
    public int Resolution { get; init; } = 1024;
    public QwenImage21Options? QwenImage21 { get; init; }
    public int MaximumReferences { get; init; }
    public IReadOnlyList<PromptReference> References { get; init; } = [];
    public IReadOnlyList<LoraSelection> Loras { get; init; } = [];
    public IReadOnlyList<string> ProtectedTriggers { get; init; } = [];
    public PromptEnhancementContext Capture() => this with
    {
        References = Array.AsReadOnly(References.Select(r => r with { Crop = r.Crop is null ? null : r.Crop with { }, Region = r.Region?.Copy() }).ToArray()),
        Loras = Array.AsReadOnly(Loras.ToArray()), ProtectedTriggers = Array.AsReadOnly(ProtectedTriggers.ToArray())
    };
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
}

// Direction is the author's guidance for this rewrite only, so it is not part of the prompt setup a suggestion is checked against.
public sealed record PromptEnhancementRequest(PromptEnhancementContext Context, TextModelReference Model,
    bool InspectImages = false, bool FollowsDefault = false, string Direction = "");
public enum PromptEnhancementKind { Prompt, NeedsInput, NeedsSetup }
public sealed record PromptEnhancementResult(PromptEnhancementKind Kind, string Text);
public sealed record PromptEnhancementUpdate(GenerationProgress? Progress = null, string? Text = null,
    PromptEnhancementResult? Result = null, string? Error = null, bool Complete = false);
