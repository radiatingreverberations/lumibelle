namespace lumibelle.Models;

public sealed record EnvironmentViewCandidate(string Id, string Name, bool Approved, string Notes,
    ShotImageBinding? Image, AssetReferenceReel? Reel, ReelKeyframe? Keyframe, string Sha256);
public sealed record EnvironmentViewRequest(int Version, Guid ProjectId, Guid CompositionId, Guid RunId,
    Guid AssetId, string AssetName, string Guidance, Shot Shot, string DirectingNotes, string SceneContext,
    IReadOnlyList<string> NearbyShots, string ContextFingerprint, string SourceFingerprint,
    IReadOnlyList<EnvironmentViewCandidate> Pool, IReadOnlyList<string> Candidates,
    IReadOnlyList<Guid> RemovedBindings, int MaximumViews, bool Final, IReadOnlyList<string> Warnings)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ShotDirectionContext? DirectionContext { get; init; }
    public bool IncludeSceneText { get; init; }
    public bool IncludeNearbyShots { get; init; }
    public string CurrentPrompt { get; init; } = "";
    public string RevisionNotes { get; init; } = "";
}
public sealed record EnvironmentViewChoice(string CandidateId, string Reason);
public sealed record EnvironmentViewResult(string Summary, IReadOnlyList<EnvironmentViewChoice> Views, IReadOnlyList<string> Warnings)
{
    public EnvironmentStagingPlan? Staging { get; init; }
}
public sealed record EnvironmentStagingPlan(string Blocking, string Camera, string Assumptions = "", string Concerns = "")
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EnvironmentSupportClaim>? ReferenceSupport { get; init; }
}
// A shot-specific reviewed interpretation, separate from reusable asset notes and media.
public sealed record EnvironmentShotStaging(Guid ShotId, Guid AssetId, Guid SelectionId, string ActionFingerprint,
    string DirectingNotes, string RevisionNotes, EnvironmentStagingPlan Plan, IReadOnlyList<EnvironmentStagingView> Views)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<EnvironmentSupportEvidence>? Evidence { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DirectionContextFingerprint { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DirectionFingerprint { get; init; }
    // Absent on historical staging; v1 hashes semantic content and explicitly captures no direction.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? DirectionDependencyVersion { get; init; }
}
public sealed record EnvironmentStagingView(Guid BindingId, Guid MediaId, Guid? KeyframeId, int? FrameIndex,
    ImageCropRegion? Crop, string Reason);
// Bytes are held only while preparing a run; each durable job captures its own inspected subset.
public sealed record EnvironmentViewCapture(EnvironmentViewRequest Request, IReadOnlyDictionary<string, byte[]> Pictures,
    AiSettings Settings);
public sealed record EnvironmentViewApplication(EnvironmentViewRequest Request, IReadOnlyList<string> Selected,
    IReadOnlyList<Guid> RemovedBindings, bool RefMod)
{
    public EnvironmentStagingPlan? Staging { get; init; }
    public IReadOnlyList<EnvironmentViewChoice> ViewReasons { get; init; } = [];
}
