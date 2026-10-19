namespace lumibelle.Models;

// Editable, live associations. A drafting snapshot is captured separately and never rewritten.
public sealed record DraftingEnvironmentSetup
{
    public Guid Version { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public string Constraints { get; set; } = "";
    public List<EnvironmentViewCandidate> Views { get; set; } = [];
    public EnvironmentInspectionAudit? Inspection { get; set; }
}
public sealed record EnvironmentSourceIssue(string Id, string Name, string Reason);
public sealed record EnvironmentInspectionAudit(Guid OperationId, IReadOnlyList<string> Inspected,
    IReadOnlyList<string> Uninspected, IReadOnlyList<EnvironmentSourceIssue> Unavailable, bool Complete);
public sealed record DraftingEnvironmentSnapshot(Guid Id, Guid SceneId, Guid SetupVersion, Guid AssetId,
    string AssetName, string Guidance, string Constraints, IReadOnlyList<EnvironmentViewCandidate> Views, string Fingerprint);
public sealed record DraftingEnvironmentAssessment
{
    public string Summary { get; set; } = "";
    public string Assumptions { get; set; } = "";
    public string Concerns { get; set; } = "";
    public string MissingCoverage { get; set; } = "";
    public List<EnvironmentSupportClaim> Claims { get; set; } = [];
}
// Resource mapping is live; Snapshot contains the original captured evidence identities.
public sealed record AcceptedDraftingEnvironment(DraftingEnvironmentSnapshot Snapshot, DraftingEnvironmentAssessment Assessment)
{
    public Dictionary<Guid, Guid> Resources { get; set; } = [];
    // Null means all selected views were attached. An explicit empty manifest means metadata only.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? InspectionImageHashes { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<EnvironmentViewCandidate> InspectedViews => Snapshot.Views.Where(v => InspectionImageHashes is null || InspectionImageHashes.Contains(v.Sha256));
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid LiveAssetId => Resources.GetValueOrDefault(Snapshot.AssetId, Snapshot.AssetId);
}
public sealed record EnvironmentSourcePool(Guid ProjectId, Guid AssetId, string AssetName, string Guidance,
    string SourceFingerprint, IReadOnlyList<EnvironmentViewCandidate> Candidates, IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, byte[]> Pictures)
{
    public IReadOnlyList<EnvironmentSourceIssue> Unavailable { get; init; } = [];
}
public sealed record SceneEnvironmentSelectionRequest(Guid ProjectId, Guid SceneId, Guid? ShotId, string SceneText,
    string Instructions, string Constraints, Guid AssetId, string AssetName, string Guidance,
    IReadOnlyList<EnvironmentViewCandidate> Candidates, bool Final)
{
    public Guid OperationId { get; init; }
    public IReadOnlyList<string> SourceIds { get; init; } = [];
    public IReadOnlyList<string> PreviouslyInspected { get; init; } = [];
    public IReadOnlyList<EnvironmentSourceIssue> Unavailable { get; init; } = [];
}
public sealed record SceneEnvironmentSelectionResult(string Summary, IReadOnlyList<EnvironmentViewChoice> Views,
    IReadOnlyList<string> Warnings);
