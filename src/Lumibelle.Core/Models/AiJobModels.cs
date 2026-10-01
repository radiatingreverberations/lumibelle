using System.Text.Json;
using lumibelle.Services.Story;

namespace lumibelle.Models;

public enum AiJobKind { ScriptAssistant, AssetExtraction, ShotPlanning, PromptEnhancement, Guidance, ImageCreate, ImageEdit, Video, TextBenchmark, TextAdvancedTest, PromptComposition, ReelComposition, ReelVideo, RefModBuild, ContentProbe, AssetPicking, ShotTranslation }
public enum AiJobState { Waiting, Running, NeedsAttention, Completed, Cancelled }
public enum AiJobRecovery { None, CheckStatus, RetryOutput, GenerateAgain }

public sealed record AiJobTarget(Guid? ProjectId = null, Guid? AssetId = null, Guid? ShotId = null,
    GuidanceScope? GuidanceScope = null, Guid? LookId = null, Guid? ImageId = null, string? ModelKey = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? TakeId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? CompositionId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ReelId = null)
{
    public string LockKey(AiJobKind kind) => kind switch
    {
        AiJobKind.ShotTranslation => $"{ProjectId}/shots/{ShotId}/dubs/{TakeId}",
        AiJobKind.AssetPicking => $"{ProjectId}/shots/{ShotId}/asset-picker",
        AiJobKind.ReelComposition => $"{ProjectId}/assets/{AssetId}/reels/{ReelId}/composer",
        AiJobKind.ReelVideo => $"{ProjectId}/assets/{AssetId}/reels/{ReelId}/video",
        AiJobKind.RefModBuild => $"{ProjectId}/assets/{AssetId}/reels/{ReelId}/refmod",
        AiJobKind.ScriptAssistant => $"{ProjectId}/script",
        AiJobKind.AssetExtraction => $"{ProjectId}/extraction",
        AiJobKind.ShotPlanning => $"{ProjectId}/planning",
        AiJobKind.PromptComposition => ShotId is { } shot ? $"{ProjectId}/shots/{shot}/composer" : $"{ProjectId}/production/{CompositionId}/composer",
        AiJobKind.ImageCreate or AiJobKind.ImageEdit or AiJobKind.PromptEnhancement => $"{ProjectId}/assets/{AssetId}/composer",
        AiJobKind.Video => TakeId is { } take ? $"{ProjectId}/shots/{ShotId}/refinement/{take}" : $"{ProjectId}/shots/{ShotId}/video",
        AiJobKind.Guidance => $"{ProjectId}/assets/{AssetId}/guidance/{GuidanceScope}/{LookId}/{ImageId}",
        AiJobKind.TextBenchmark or AiJobKind.TextAdvancedTest => $"model-test/{ModelKey}",
        AiJobKind.ContentProbe => $"content-probe/{ModelKey}",
        _ => throw new WorkspaceStoreException("Unknown AI job type.")
    };
    public void Validate(AiJobKind kind)
    {
        if (new[] { ProjectId, AssetId, ShotId, LookId, ImageId, TakeId, CompositionId, ReelId }.Any(id => id == Guid.Empty) || TakeId is not null && kind is not (AiJobKind.Video or AiJobKind.ShotTranslation)) throw new WorkspaceStoreException("An AI job target has an invalid identity.");
        if (kind == AiJobKind.ShotTranslation && TakeId is null) throw new WorkspaceStoreException("Choose an exact master take for translation.");
        var test = kind is AiJobKind.TextBenchmark or AiJobKind.TextAdvancedTest or AiJobKind.ContentProbe;
        var reel = kind is AiJobKind.ReelComposition or AiJobKind.ReelVideo or AiJobKind.RefModBuild;
        if ((ReelId is not null) != reel) throw new WorkspaceStoreException("Choose an exact reel target.");
        var asset = reel || kind is AiJobKind.ImageCreate or AiJobKind.ImageEdit or AiJobKind.PromptEnhancement or AiJobKind.Guidance;
        if (test ? ProjectId is not null || string.IsNullOrWhiteSpace(ModelKey) || ModelKey.Length > 2048 : ProjectId is null || ModelKey is not null)
            throw new WorkspaceStoreException("The AI request needs an exact project or model-test target.");
        // Shot planning targets the project, or one shot when drafting that shot in place.
        if ((AssetId is not null) != asset || (ShotId is not null) != (kind is AiJobKind.Video or AiJobKind.PromptComposition or AiJobKind.AssetPicking or AiJobKind.ShotTranslation) &&
            kind != AiJobKind.ShotPlanning) throw new WorkspaceStoreException("The AI request has an invalid asset or shot target.");
        if (kind == AiJobKind.PromptComposition && CompositionId is null || CompositionId is not null && kind is not (AiJobKind.Video or AiJobKind.PromptComposition)) throw new WorkspaceStoreException("Choose an exact composition target.");
        if (kind == AiJobKind.Guidance)
        {
            if (GuidanceScope is null || !Enum.IsDefined(GuidanceScope.Value) ||
                (LookId is not null) != (GuidanceScope == Models.GuidanceScope.Look) ||
                (ImageId is not null) != (GuidanceScope is Models.GuidanceScope.Image or Models.GuidanceScope.ImageDescription)) throw new WorkspaceStoreException("Choose an exact guidance field.");
        }
        else if (GuidanceScope is not null || LookId is not null || ImageId is not null) throw new WorkspaceStoreException("Only guidance jobs use a field target.");
    }
}

// Snapshot is produced by the matching typed handler. Credentials are resolved separately
// from the protected settings store; they are never part of an enqueue request.
public sealed record AiJobSubmission(Guid Id, AiJobKind Kind, AiBackend Backend, AiJobTarget Target,
    string ProjectName, string TargetName, Guid OriginTabId, JsonElement Snapshot,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] AiBatchDefinition? Batch = null)
{
    public static AiJobSubmission Create<T>(Guid id, AiJobKind kind, AiBackend backend, AiJobTarget target,
        string projectName, string targetName, Guid originTabId, T snapshot) =>
        new(id, kind, backend, target, projectName, targetName, originTabId, JsonSerializer.SerializeToElement(snapshot, AtomicJsonFile.Options));
}
public sealed record AiJobHeader
{
    public AiBatchDefinition? Batch { get; init; }
    public required Guid Id { get; init; }
    public required AiJobKind Kind { get; init; }
    public required AiBackend Backend { get; init; }
    public required AiJobTarget Target { get; init; }
    public required string ProjectName { get; init; }
    public required string TargetName { get; init; }
    public required Guid OriginTabId { get; init; }
    public required string RequestFingerprint { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }
    public AiJobState State { get; init; } = AiJobState.Waiting;
    public AiJobRecovery Recovery { get; init; }
    public Guid? LeaseId { get; init; }
    public bool CancelRequested { get; init; }
    public bool RemoteUnconfirmed { get; init; }
    // Per-request scheduling only. Retained on pause/retry, never inherited by new requests.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Priority { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ComfyQueueControl? ComfyControl { get; init; }
    public bool Unread { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ActivityClearedUtc { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanClearActivity => State is not (AiJobState.Waiting or AiJobState.Running) && !RemoteUnconfirmed && ComfyControl is null;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanRetryCaptured => State == AiJobState.NeedsAttention && !CancelRequested && !RemoteUnconfirmed &&
        Recovery is AiJobRecovery.CheckStatus or AiJobRecovery.RetryOutput;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanRecoverCancelledOutputs => State == AiJobState.Cancelled && CancelRequested && !RemoteUnconfirmed &&
        Backend == AiBackend.ComfyUI && Kind is AiJobKind.Video or AiJobKind.ReelVideo && Batch is not null;
    public string? Error { get; init; }
    public long Version { get; init; }
    public bool LocksTarget => !CancelRequested && (State is AiJobState.Waiting or AiJobState.Running || RemoteUnconfirmed || ComfyControl is not null);
    public bool HoldsProvider => State == AiJobState.Running || RemoteUnconfirmed;
    public string ReviewUrl => Kind == AiJobKind.ContentProbe ? $"/settings/ai/probes?jobId={Id}"
        : Target.ProjectId is not { } project ? $"/settings/ai?jobId={Id}" : Kind switch
    {
        AiJobKind.ShotTranslation => $"/projects/{project}/dubs?jobId={Id}&shotId={Target.ShotId}&takeId={Target.TakeId}",
        AiJobKind.AssetPicking => $"/projects/{project}/shots?jobId={Id}&shotId={Target.ShotId}&view=References",
        AiJobKind.RefModBuild => $"/projects/{project}/refmods/{Id}",
        AiJobKind.ScriptAssistant => $"/projects/{project}/script?jobId={Id}",
        AiJobKind.ReelComposition or AiJobKind.ReelVideo => $"/projects/{project}/assets?assetId={Target.AssetId}&view=reels&jobId={Id}",
        AiJobKind.Video or AiJobKind.PromptComposition => $"/projects/{project}/shots?jobId={Id}&shotId={Target.ShotId}" + (Target.CompositionId is { } composition ? $"&setupId={composition}" : "") + (Kind == AiJobKind.Video ? "&view=Takes" : "&view=Prompt"),
        AiJobKind.ShotPlanning => $"/projects/{project}/shots?jobId={Id}" + (Target.ShotId is { } shot ? $"&shotId={shot}" : ""),
        _ => $"/projects/{project}/assets?jobId={Id}" + (Target.AssetId is { } asset ? $"&assetId={asset}" : "")
    };
}
public sealed record AiQueueDocument
{
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; }
    public IReadOnlyList<AiBackend> Paused { get; init; } = [];
    // List order is the saved/manual order within each priority band. Dispatch and
    // UI use AiQueueOrder so priority never changes captured inputs or running work.
    public IReadOnlyList<AiJobHeader> Jobs { get; init; } = [];
}
public sealed record AiJobProgress(GenerationProgress Progress, int? Candidate = null, int? CandidateCount = null, string? Notice = null, CodexImageTiming? CodexTiming = null)
{
    // Presentation order is independent of the stable candidate identities used to save outputs.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? CandidateExecutionOrder { get; init; }
}
public enum AiJobArtifact { Progress, Execution, Result, Composer }
public enum AiActivityChange { Read, Clear, Restore }
public sealed record AiActivityObservation(Guid Id, long Version);
public sealed record AiJobSnapshot(string Fingerprint, JsonElement Request, AiBatchDefinition? InitialBatch = null);

// Candidate identity and numbering are independent of visible images and Trash.
// An append command is retained with its candidate so a lost acknowledgement is safe.
public sealed record AiBatchCandidate(Guid Id, int Number, long Seed, Guid? AppendCommandId = null);
public sealed record AiBatchDefinition(Guid RootId, IReadOnlyList<AiBatchCandidate> Candidates)
{
    public AiBatchDefinition Capture() => Candidates is null || Candidates.Any(c => c is null)
        ? throw new WorkspaceStoreException("The batch candidate list is invalid.") : this with { Candidates = Candidates.ToArray() };
    public static AiBatchDefinition Create(Guid id, int count, long? seed = null)
    {
        if (id == Guid.Empty || count is < 1 or > 4 || seed is < 0 || seed > long.MaxValue - count + 1)
            throw new WorkspaceStoreException("Choose one to four candidates and a valid seed.");
        var first = seed ?? Random.Shared.NextInt64(1, long.MaxValue - count);
        return new(id, Enumerable.Range(1, count).Select(n => new AiBatchCandidate(Guid.NewGuid(), n, checked(first + (n - 1)))).ToArray());
    }
}
public sealed record AiBatchAppend(AiJobHeader Job, AiBatchCandidate Candidate, bool Continuation);

public enum AiRemoteState { Submitting, Accepted, Finished, Cancelled }
public sealed record AiRemoteSubmission(string Operation, string ServerUrl, string ClientId, DateTimeOffset StartedUtc,
    AiRemoteState State = AiRemoteState.Submitting, string? PromptId = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtifactOperation { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RetryId { get; init; }
    public bool MayBeRunning => State is AiRemoteState.Submitting or AiRemoteState.Accepted;
}
public sealed record AiJobExecution(IReadOnlyList<AiRemoteSubmission> Submissions)
{
    public bool MayBeRunning => Submissions.Any(s => s.MayBeRunning);
}
public enum AiOperationArtifact { Request, Output, Image, Video, TestPreparation, TestResult, Timings, RetryReceipt }
public sealed record AiOperationData<T>(string Operation, T Data);

// One active request per lock key. Image batches for an asset each hold their own key, so a new
// create or edit queues behind earlier ones instead of waiting for them; every other operation,
// including prompt enhancement, keeps one active request per target.
public static class AiJobLocks
{
    public const int MaxActiveImageBatchesPerAsset = 4;
    public static bool IsImage(AiJobKind kind) => kind is AiJobKind.ImageCreate or AiJobKind.ImageEdit;
    public static string Key(AiJobKind kind, AiJobTarget target, AiBatchDefinition? batch) =>
        IsImage(kind) && batch is not null ? $"{target.ProjectId}/assets/{target.AssetId}/images/{batch.RootId}" : target.LockKey(kind);
    public static string Key(AiJobHeader job) => Key(job.Kind, job.Target, job.Batch);
    public static int ActiveImageBatches(IEnumerable<AiJobHeader> jobs, Guid? projectId, Guid? assetId) =>
        jobs.Where(j => IsImage(j.Kind) && j.LocksTarget && j.Target.ProjectId == projectId && j.Target.AssetId == assetId)
            .Select(j => j.Batch?.RootId ?? j.Id).Distinct().Count();
    public static string ImageLimitMessage => $"This asset already has {MaxActiveImageBatchesPerAsset} image requests queued or working. Wait for one to finish, or cancel one, before queuing another.";
}
