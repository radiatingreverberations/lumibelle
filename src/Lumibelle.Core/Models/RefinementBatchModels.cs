namespace lumibelle.Models;

public enum RefinementSourceKind { Take, Reel }
// Held batches prepare media but submit nothing until started.
public enum RefinementBatchState { Held, Running, Paused, Finished }
// Persisted checkpoints. Waiting/Generating/Completed are read from the submitted job.
public enum RefinementItemState { Preparing, Ready, Submitted, Failed, Cancelled }
public enum RefinementItemStage { Preparing, Ready, Waiting, Generating, Completed, NeedsAttention, Failed, Cancelled }

public sealed record RefinementBatchItem
{
    // Also the ID of the item's single generation job, so a lost acknowledgement reuses it.
    public required Guid Id { get; init; }
    public RefinementSourceKind Kind { get; init; }
    public required Guid SourceId { get; init; }
    // Destination shot or reel asset; informational for links and acceptance.
    public required Guid OwnerId { get; init; }
    public required string Name { get; init; }
    // Frozen when queued: reviewed prompt, instructions, references, output settings and seed.
    public required MotionReferenceOptions Options { get; init; }
    public RefinementItemState State { get; init; }
    public AiJobSubmission? Submission { get; init; }
    public string? Error { get; init; }
    // Set while another request holds the destination, so the batch waits instead of failing.
    public string? Notice { get; init; }
    public Guid? ResultId { get; init; }
    public DateTimeOffset? ReviewedUtc { get; init; }
    public DateTimeOffset? AcceptedUtc { get; init; }
    // An earlier clip in this batch whose refined take this clip's shot frames wait for.
    public Guid? WaitingForItemId { get; init; }
    // Frames swapped to refined takes, or kept as drafts because a refined take was unavailable.
    public IReadOnlyList<string> Adjustments { get; init; } = [];
}

public sealed record RefinementBatch
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required Guid TabId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public RefinementBatchState State { get; init; }
    // Pause after the current clip finishes; the running render is not interrupted.
    public bool PauseRequested { get; init; }
    // Shot frames and start frames taken from another clip in this batch use that clip's refined take.
    public bool UseRefinedFrames { get; init; }
    public required IReadOnlyList<RefinementBatchItem> Items { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Active => State != RefinementBatchState.Finished;
}

public sealed record RefinementBatches
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid ProjectId { get; init; }
    public long Revision { get; init; }
    public IReadOnlyList<RefinementBatch> Runs { get; init; } = [];
}

// Reviewed per-item choices from the batch dialog; shared settings are already applied.
public sealed record RefinementBatchRequest(Guid BatchId, string Name, bool StartNow, IReadOnlyList<RefinementBatchEntry> Items, bool UseRefinedFrames = false);
public sealed record RefinementBatchEntry(RefinementSourceKind Kind, Guid SourceId, string Name, MotionReferenceOptions Options);

public sealed record RefinementItemView(RefinementBatchItem Item, RefinementItemStage Stage, AiJobHeader? Job);
public sealed record RefinementBatchCounts(int Completed, int Failed, int Waiting, int Reviewed, int Total);
