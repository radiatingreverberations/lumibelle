using System.Text.Json.Serialization;

namespace lumibelle.Models;

public sealed record TextModelReference(AiBackend Backend, string Model, string Name, string? ComfyUrl = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VllmUrl { get; init; }
    // A selection is a value snapshot, not a live link to the profile library.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ProfileId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReasoningMaxTokens { get; init; }
}
public enum TextAssistantStudio { Story, AssetExtraction, PromptEnhancement, Shots, Production }
public enum TextModelSelectionSource { GlobalDefault, ProjectDefault, RequestOverride }

public sealed record ProjectAiPreferences
{
    public int SchemaVersion { get; init; } = 2;
    public required Guid ProjectId { get; init; }
    public TextModelReference? TextDefault { get; init; }
    public long TextDefaultRevision { get; init; }
    public ImageWorkflow? ImageDefault { get; init; }
    public long ImageDefaultRevision { get; init; }
    public VisualStyle VisualStyle { get; init; } = new();
    public long VisualStyleRevision { get; init; }
    public TextModelReference? Production { get; init; }
    public TextModelReference? Shots { get; init; }
    public TextModelReference? Story { get; init; }
    public TextModelReference? AssetExtraction { get; init; }
    public TextModelReference? PromptEnhancement { get; init; }
    public LoraVisibility LoraVisibility { get; init; } = new();
    public long LoraVisibilityRevision { get; init; }
    public TextModelReference? Selection(TextAssistantStudio studio) => studio switch
    {
        TextAssistantStudio.Shots => Shots,
        TextAssistantStudio.Production => Production,
        TextAssistantStudio.Story => Story,
        TextAssistantStudio.AssetExtraction => AssetExtraction,
        TextAssistantStudio.PromptEnhancement => PromptEnhancement,
        _ => throw new ArgumentOutOfRangeException(nameof(studio))
    };
}

public sealed record TextModelSelectionState(TextModelReference Model, bool Ready, bool SupportsImages = false, bool FollowsDefault = false,
    TextModelSelectionSource Source = TextModelSelectionSource.RequestOverride, string? UnavailableReason = null)
{
    public bool ImageInputKnown { get; init; } = true;
}
