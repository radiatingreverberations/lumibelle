using System.Text.Json.Serialization;

namespace lumibelle.Models;

[JsonConverter(typeof(JsonStringEnumConverter<DirectingStyle>))]
public enum DirectingStyle { Auto, PrestigeDrama, KineticAction, EnsembleClassical, PerformanceComedy }
public sealed record DirectionStrategy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SceneId { get; set; }
    public Guid? SourceId { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public string Approach { get; set; } = "";
    public string DramaticProgression { get; set; } = "";
    public string VisualProgression { get; set; } = "";
    public string AudienceInformation { get; set; } = "";
    public string SpatialAnchors { get; set; } = "";
}
public sealed record DirectionIntent
{
    public string Purpose { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Attention { get; set; } = "";
    public string Relationships { get; set; } = "";
}
public sealed record DirectionStaging
{
    public string Positions { get; set; } = "";
    public string Facing { get; set; } = "";
    public string Eyelines { get; set; } = "";
    public string Movement { get; set; } = "";
    public string Camera { get; set; } = "";
}
public sealed record DirectionEditorial
{
    public string CutIn { get; set; } = "";
    public string CutOut { get; set; } = "";
    public string Timing { get; set; } = "";
}
public sealed record DirectionAdvanced
{
    public string Continuity { get; set; } = "";
    public string Assumptions { get; set; } = "";
    public string LongTakeIdea { get; set; } = "";
}
public sealed record ShotDirection
{
    public DirectionIntent Intent { get; set; } = new();
    public DirectionStaging Staging { get; set; } = new();
    public DirectionEditorial Editorial { get; set; } = new();
    public DirectionAdvanced Advanced { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DirectionDependencies? Dependencies { get; set; }
}
// Separate snapshots let authored intent survive a reference-only change.
public sealed record DirectionDependencies(string Story, string Strategy, string Cast, string Dialogue,
    string Duration, string Neighbours, string References);
public sealed record DirectionFreshness(bool Intent, bool Staging, bool Editorial, bool Continuity, bool ReferenceSupport,
    IReadOnlyList<string> Limitations);
public sealed record ShotDirectionContext(DirectionStrategy? Strategy, DirectionIntent? Intent, DirectionStaging? ProposedStaging,
    DirectionEditorial? Editorial, DirectionAdvanced? Advanced, IReadOnlyList<string> Limitations);

[JsonConverter(typeof(JsonStringEnumConverter<ReferenceSupportStatus>))]
public enum ReferenceSupportStatus { Supported, Inferred, Missing, Conflicting }
public sealed record EnvironmentSupportClaim(string Claim, ReferenceSupportStatus Status, IReadOnlyList<string> CandidateIds);
public sealed record EnvironmentSupportEvidence(string CandidateId, string Sha256, Guid MediaId, Guid? KeyframeId, ImageCropRegion? Crop);
