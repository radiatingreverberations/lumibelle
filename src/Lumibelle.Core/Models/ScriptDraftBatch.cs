namespace lumibelle.Models;

public enum ScriptDraftBatchState { Running, Paused, Completed, Stopped }

public sealed record ScriptDraftBatch
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public required Guid PlanId { get; init; }
    public long PlanRevision { get; init; }
    public required Guid TabId { get; init; }
    public required Guid SessionId { get; init; }
    public required TextModelReference Model { get; init; }
    public required AiSettings Settings { get; init; }
    public TextModelSelectionSource SelectionSource { get; init; }
    public string Instructions { get; init; } = "";
    public string? ProjectContext { get; init; }
    public IReadOnlyList<AssetImageReference> Images { get; init; } = [];
    public IReadOnlyList<ConversationMessage> Conversation { get; init; } = [];
    public required IReadOnlyList<ScriptSourceScene> Sequences { get; init; }
    public int Added { get; init; }
    public ScriptDraftBatchState State { get; init; } = ScriptDraftBatchState.Running;
    // Publish the exact next request before enqueuing. Lost acknowledgements reuse its ID.
    public AiJobSubmission? Pending { get; init; }
    public IReadOnlyList<Guid> Jobs { get; init; } = [];
    public string? Error { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Active => State is ScriptDraftBatchState.Running or ScriptDraftBatchState.Paused;
}

public sealed record ScriptDraftBatches
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid ProjectId { get; init; }
    public long Revision { get; init; }
    public IReadOnlyList<ScriptDraftBatch> Runs { get; init; } = [];
}
