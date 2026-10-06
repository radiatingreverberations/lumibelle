using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public interface IAiJobStore
{
    Task<AiJobHeader> ReviewShotPlanningAsync(Guid projectId, Guid jobId, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Reviewing saved shot responses is unavailable.");
    Task ResetProductionAsync(Guid project, CancellationToken ct = default) => Task.CompletedTask;
    Task<AiQueueDocument> ReadAsync(CancellationToken ct = default);
    Task<AiJobHeader> EnqueueAsync(AiJobSubmission request, CancellationToken ct = default);
    Task<JsonElement> ReadSnapshotAsync(Guid id, CancellationToken ct = default);
    // Validates against a header the caller already holds instead of rereading the index.
    Task<JsonElement> ReadSnapshotAsync(AiJobHeader job, CancellationToken ct = default) => ReadSnapshotAsync(job.Id, ct);
    Task<AiJobHeader?> ClaimNextAsync(AiBackend provider, int concurrency, CancellationToken ct = default);
    Task<AiJobHeader> UpdateAsync(Guid id, Func<AiJobHeader, AiJobHeader> update, CancellationToken ct = default);
    Task<IReadOnlyList<AiJobHeader>> ChangeActivityAsync(IReadOnlyList<AiActivityObservation> observed, AiActivityChange change, CancellationToken ct = default);
    Task SetPausedAsync(AiBackend provider, bool paused, CancellationToken ct = default);
    Task MoveAsync(Guid id, int offset, bool first = false, CancellationToken ct = default);
    Task<AiJobHeader> RequeueAsync(Guid id, CancellationToken ct = default);
    Task<AiBatchAppend> ExtendBatchAsync(Guid rootId, Guid commandId, Guid originTabId, CancellationToken ct = default);
    Task WriteArtifactAsync<T>(Guid id, AiJobArtifact artifact, T value, CancellationToken ct = default);
    Task WriteObservedUsageAsync(Guid id, Guid lease, OpenRouterRequestUsage usage, CancellationToken ct = default);
    Task WriteOwnedArtifactAsync<T>(Guid id, Guid lease, AiJobArtifact artifact, T value, CancellationToken ct = default, bool recoveringCancelledOutputs = false);
    Task<T?> ReadArtifactAsync<T>(Guid id, AiJobArtifact artifact, CancellationToken ct = default);
    Task WriteOperationAsync<T>(Guid id, Guid lease, string operation, AiOperationArtifact artifact, T value, CancellationToken ct = default, bool recoveringCancelledOutputs = false);
    Task<T?> ReadOperationAsync<T>(Guid id, string operation, AiOperationArtifact artifact, CancellationToken ct = default);
    IDisposable AcquireExecutionOwner();
    string DirectoryFor(Guid id);
}

public sealed partial class FileAiJobStore : IAiJobStore
{
    private readonly string _root;
    private readonly TimeProvider clock;
    public FileAiJobStore(IHostEnvironment environment, TimeProvider clock) : this(Path.Combine(environment.ContentRootPath, "App_Data", "ai-jobs"), clock) { }
    public FileAiJobStore(string rootDirectory, TimeProvider clock) { _root = Path.GetFullPath(rootDirectory); this.clock = clock; }
    private string Index => Path.Combine(_root, "queue.json");
    public IDisposable AcquireExecutionOwner()
    {
        Directory.CreateDirectory(_root);
        try { return new FileStream(Path.Combine(_root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new WorkspaceStoreException("Another Lumibelle process owns the AI queue, or its worker lock is unavailable.", e); }
    }
    public string DirectoryFor(Guid id) => id == Guid.Empty ? throw new WorkspaceStoreException("Invalid AI job identity.") : Path.Combine(_root, id.ToString("D"));
    private string SnapshotPath(Guid id) => Path.Combine(DirectoryFor(id), "request.json");
    private string OperationPath(Guid id, string operation, AiOperationArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(operation) || operation.Length > 200 || !Enum.IsDefined(artifact)) throw new WorkspaceStoreException("Invalid AI operation artifact.");
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(operation)));
        return Path.Combine(DirectoryFor(id), "operations", key, artifact.ToString().ToLowerInvariant() + ".json");
    }
    private static string Fingerprint(AiJobSubmission request) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, AtomicJsonFile.Options)));
    private string ArtifactPath(Guid id, AiJobArtifact artifact) => Enum.IsDefined(artifact)
        ? Path.Combine(DirectoryFor(id), artifact.ToString().ToLowerInvariant() + ".json") : throw new WorkspaceStoreException("Unknown AI job artifact.");
    public async Task ResetProductionAsync(Guid project, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(Index, ct);
        var d = await ReadIndexAsync(ct);
        var removed = d.Jobs.Where(j => j.Target.ProjectId == project && j.Kind is AiJobKind.Video or AiJobKind.PromptComposition).ToArray();
        if (removed.Any(j => j.LocksTarget || j.RemoteUnconfirmed)) throw new WorkspaceStoreException("Wait for active production requests before resetting.");
        if (removed.Length > 0) await PublishAsync(d with { Jobs = d.Jobs.Except(removed).ToArray() }, ct);
    }
    public async Task<AiQueueDocument> ReadAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_root)) return new();
        // Writers publish by renaming over the index, which Windows refuses while it is open. Share their lock: frequent
        // readers must not hold the file through a slow read and make an enqueue or a worker's state change fail to save.
        using var gate = await ProjectFiles.LockAsync(Index, ct);
        return await ReadIndexAsync(ct);
    }
    // Only for callers already holding the index lock, which is not reentrant.
    private async Task<AiQueueDocument> ReadIndexAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_root)) return new();
        var document = await AtomicJsonFile.ReadAsync<AiQueueDocument>(Index, ct) ?? new();
        Validate(document); return document;
    }
    private static void Validate(AiQueueDocument document)
    {
        if (document.SchemaVersion != 1 || document.Revision < 0 || document.Jobs is null || document.Paused is null ||
            document.Paused.Any(p => !Enum.IsDefined(p)) || document.Paused.Distinct().Count() != document.Paused.Count ||
            document.Jobs.Any(j => j is null || j.Id == Guid.Empty || j.OriginTabId == Guid.Empty || j.Target is null ||
                !Enum.IsDefined(j.Kind) || !Enum.IsDefined(j.Backend) || !Enum.IsDefined(j.State) || !Enum.IsDefined(j.Recovery) ||
                j.RequestFingerprint?.Length != 64 || j.CreatedUtc == default || j.Version < 0 || j.ProjectName is null || string.IsNullOrWhiteSpace(j.TargetName) ||
                j.State == AiJobState.Running && (j.LeaseId is null || j.LeaseId == Guid.Empty || j.StartedUtc is null) || j.RemoteUnconfirmed && j.Backend != AiBackend.ComfyUI ||
                j.ComfyControl is { } control && (j.Backend != AiBackend.ComfyUI || control.RetryId == Guid.Empty || j.CancelRequested)))
            throw new WorkspaceStoreException("The AI queue is invalid. Its requests have not been replaced.");
        if (document.Jobs.Select(j => j.Id).Distinct().Count() != document.Jobs.Count ||
            document.Jobs.Where(j => j.LocksTarget).GroupBy(AiJobLocks.Key).Any(g => g.Count() > 1))
            throw new WorkspaceStoreException("The AI queue contains duplicate active targets.");
        foreach (var job in document.Jobs) job.Target.Validate(job.Kind);
        ValidateBatches(document);
    }
    private async Task PublishAsync(AiQueueDocument document, CancellationToken ct)
    { document = document with { Revision = document.Revision + 1 }; Validate(document); await AtomicJsonFile.WriteAsync(Index, document, ct); }
    public async Task<AiJobHeader> EnqueueAsync(AiJobSubmission request, CancellationToken ct = default)
    {
        // Serialize before the first await, so caller-owned arrays and JsonDocument lifetimes
        // cannot alter a waiting request. The public index never contains the large payload.
        var payload = JsonSerializer.SerializeToElement(request.Snapshot, AtomicJsonFile.Options);
        var captured = request with { Snapshot = payload, Batch = request.Batch?.Capture() };
        if (request.Id == Guid.Empty || request.OriginTabId == Guid.Empty || !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Backend) || request.Target is null ||
            string.IsNullOrWhiteSpace(request.TargetName) || request.TargetName.Length > 2000 || request.ProjectName is null || request.ProjectName.Length > 2000 || payload.ValueKind != JsonValueKind.Object ||
            request.Kind is (AiJobKind.Video or AiJobKind.ReelVideo or AiJobKind.RefModBuild) && request.Backend != AiBackend.ComfyUI ||
            request.Kind is AiJobKind.TextBenchmark or AiJobKind.TextAdvancedTest && request.Backend is not (AiBackend.ComfyUI or AiBackend.OpenRouter) ||
            request.Kind is AiJobKind.ImageCreate or AiJobKind.ImageEdit && request.Backend is not (AiBackend.ComfyUI or AiBackend.Codex))
            throw new WorkspaceStoreException("The AI request is incomplete or has the wrong provider.");
        request.Target.Validate(request.Kind);
        if (captured.Batch is { } batch && (batch.RootId != request.Id || batch.Candidates.Count is < 1 or > 4 ||
            batch.Candidates.Where((c, index) => c.Number != index + 1 || c.AppendCommandId is not null).Any()))
            throw new WorkspaceStoreException("A new batch needs its own identity and one to four ordered candidates.");
        var fingerprint = Fingerprint(captured);
        using var gate = await ProjectFiles.LockAsync(Index, ct);
        var document = await ReadIndexAsync(ct);
        if (document.Jobs.FirstOrDefault(j => j.Id == request.Id) is { } existing)
        {
            if (existing.RequestFingerprint != fingerprint) throw new WorkspaceStoreException("This enqueue identity was already used for a different request.");
            return existing;
        }
        if (document.Jobs.Any(j => j.LocksTarget && AiJobLocks.Key(j) == AiJobLocks.Key(request.Kind, request.Target, captured.Batch)))
            throw new WorkspaceStoreException("This target already has a queued or working AI request. View or cancel that request first.");
        if (AiJobLocks.IsImage(request.Kind) && AiJobLocks.ActiveImageBatches(document.Jobs, request.Target.ProjectId, request.Target.AssetId) >= AiJobLocks.MaxActiveImageBatchesPerAsset)
            throw new WorkspaceStoreException(AiJobLocks.ImageLimitMessage);
        var header = new AiJobHeader { Id = request.Id, Kind = request.Kind, Backend = request.Backend, Target = request.Target, ProjectName = request.ProjectName,
            TargetName = request.TargetName, OriginTabId = request.OriginTabId, RequestFingerprint = fingerprint, CreatedUtc = clock.GetUtcNow(), Batch = captured.Batch };
        var path = SnapshotPath(request.Id);
        if (Directory.Exists(DirectoryFor(request.Id)) && await AtomicJsonFile.ReadAsync<AiJobSnapshot>(path, ct) is { } orphan && orphan.Fingerprint != fingerprint)
            throw new WorkspaceStoreException("A different request already owns this job's saved input.");
        await AtomicJsonFile.WriteAsync(path, new AiJobSnapshot(fingerprint, payload, captured.Batch), ct);
        await PublishAsync(document with { Jobs = [.. document.Jobs, header] }, ct);
        return header;
    }
    public async Task<JsonElement> ReadSnapshotAsync(Guid id, CancellationToken ct = default) =>
        await ReadSnapshotAsync((await ReadAsync(ct)).Jobs.FirstOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("AI request not found."), ct);
    public async Task<JsonElement> ReadSnapshotAsync(AiJobHeader header, CancellationToken ct = default)
    {
        var snapshot = await AtomicJsonFile.ReadAsync<AiJobSnapshot>(SnapshotPath(header.Id), ct);
        if (snapshot is null || snapshot.Fingerprint != header.RequestFingerprint || snapshot.Request.ValueKind != JsonValueKind.Object ||
            Fingerprint(new(header.Id, header.Kind, header.Backend, header.Target, header.ProjectName, header.TargetName, header.OriginTabId, snapshot.Request, snapshot.InitialBatch)) != header.RequestFingerprint)
            throw new WorkspaceStoreException("The saved AI request is missing or invalid. It cannot be submitted.");
        return snapshot.Request;
    }
    public async Task<AiJobHeader?> ClaimNextAsync(AiBackend provider, int concurrency, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(provider) || concurrency is < 1 or > 8 || provider == AiBackend.ComfyUI && concurrency != 1) throw new WorkspaceStoreException("Invalid provider concurrency.");
        using var gate = await ProjectFiles.LockAsync(Index, ct);
        var document = await ReadIndexAsync(ct);
        if (document.Paused.Contains(provider) || document.Jobs.Any(j => j.Backend == provider && j.RemoteUnconfirmed) ||
            document.Jobs.Count(j => j.Backend == provider && j.HoldsProvider) >= concurrency) return null;
        // Priority changes the next selection only; pause, capacity and uncertain
        // remote-work guards above remain authoritative for every request.
        var next = AiQueueOrder.Waiting(document.Jobs, provider).FirstOrDefault();
        if (next is null) return null;
        next = next with { State = AiJobState.Running, StartedUtc = clock.GetUtcNow(), FinishedUtc = null, Error = null,
            ComfyControl = next.Recovery != AiJobRecovery.None && next.ComfyControl is { } control ? control with { PauseRequested = false } : null,
            LeaseId = Guid.NewGuid(), Version = next.Version + 1 };
        await PublishAsync(document with { Jobs = document.Jobs.Select(j => j.Id == next.Id ? next : j).ToArray() }, ct);
        return next;
    }
    public async Task<AiJobHeader> UpdateAsync(Guid id, Func<AiJobHeader, AiJobHeader> update, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(Index, ct);
        var document = await ReadIndexAsync(ct);
        var current = document.Jobs.FirstOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("AI request not found.");
        var next = update(current);
        if (next.Id != current.Id || next.Target != current.Target || next.Kind != current.Kind || next.Backend != current.Backend ||
            next.OriginTabId != current.OriginTabId || next.RequestFingerprint != current.RequestFingerprint || next.CreatedUtc != current.CreatedUtc ||
            next.ProjectName != current.ProjectName || next.TargetName != current.TargetName ||
            !BatchEquals(next.Batch, current.Batch))
            throw new WorkspaceStoreException("An AI job's captured identity and inputs cannot be changed.");
        if (next == current) return current;
        next = next with { Version = current.Version + 1 };
        await PublishAsync(document with { Jobs = document.Jobs.Select(j => j.Id == id ? next : j).ToArray() }, ct);
        return next;
    }
    public async Task SetPausedAsync(AiBackend provider, bool paused, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(provider)) throw new WorkspaceStoreException("Unknown AI provider.");
        using var gate = await ProjectFiles.LockAsync(Index, ct); var document = await ReadIndexAsync(ct);
        var next = paused ? document.Paused.Append(provider).Distinct().ToArray() : document.Paused.Where(p => p != provider).ToArray();
        // Pause and stop-intent share the scheduling lock, so a newly claimed job
        // cannot escape the pause. Waiting jobs keep their position and identity.
        var jobs = provider == AiBackend.ComfyUI && paused ? document.Jobs.Select(j =>
            ComfyQueuePolicy.NeedsPause(j) && j.ComfyControl?.PauseRequested != true
                ? j with { ComfyControl = new(Guid.NewGuid(), PauseRequested: true), ActivityClearedUtc = null, Version = j.Version + 1 }
                : j).ToArray() : document.Jobs;
        if (next.SequenceEqual(document.Paused) && jobs.SequenceEqual(document.Jobs)) return;
        await PublishAsync(document with { Paused = next, Jobs = jobs }, ct);
    }
    public async Task MoveAsync(Guid id, int offset, bool first = false, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(Index, ct); var document = await ReadIndexAsync(ct);
        var job = document.Jobs.FirstOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("AI request not found.");
        if (!AiQueueOrder.IsWaiting(job)) throw new WorkspaceStoreException("Only waiting jobs can be reordered.");
        // Arrows stay within the current priority band. Run next explicitly promotes
        // this one request and moves it ahead of every waiting request on its provider.
        bool Eligible(AiJobHeader j) => j.Backend == job.Backend && AiQueueOrder.IsWaiting(j) && (first || j.Priority == job.Priority);
        var waiting = document.Jobs.Where(Eligible).ToList();
        var position = waiting.FindIndex(j => j.Id == id);
        var destination = first ? 0 : (int)Math.Clamp((long)position + offset, 0, waiting.Count - 1);
        if (destination == position && (!first || job.Priority)) return;
        var moved = first && !job.Priority ? job with { Priority = true, Version = job.Version + 1 } : job;
        waiting.RemoveAt(position); waiting.Insert(destination, moved);
        var i = 0; var ordered = document.Jobs.Select(j => Eligible(j) ? waiting[i++] : j).ToArray();
        await PublishAsync(document with { Jobs = ordered }, ct);
    }
    public async Task WriteArtifactAsync<T>(Guid id, AiJobArtifact artifact, T value, CancellationToken ct = default)
    {
        var captured = JsonSerializer.SerializeToElement(value, AtomicJsonFile.Options);
        // Artifacts are separate from scheduling metadata and large immutable inputs.
        // Check the index before taking the artifact lock: the index lock always comes first.
        var path = ArtifactPath(id, artifact);
        if (!(await ReadAsync(ct)).Jobs.Any(j => j.Id == id)) throw new WorkspaceStoreException("AI request not found.");
        await WriteArtifactFileAsync(path, captured, ct);
    }
    private static async Task WriteArtifactFileAsync<T>(string path, T value, CancellationToken ct)
    { using var gate = await ProjectFiles.LockAsync(path, ct); await AtomicJsonFile.WriteAsync(path, value, ct); }
    public async Task WriteOwnedArtifactAsync<T>(Guid id, Guid lease, AiJobArtifact artifact, T value, CancellationToken ct = default, bool recoveringCancelledOutputs = false)
    {
        var captured = JsonSerializer.SerializeToElement(value, AtomicJsonFile.Options);
        // Hold the scheduling lock through publication: checking a lease then writing
        // outside the lock would let a late worker overwrite a resumed job's result.
        using var gate = await ProjectFiles.LockAsync(Index, ct);
        var job = (await ReadIndexAsync(ct)).Jobs.SingleOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("AI request not found.");
        var receiptAfterCancellation = artifact == AiJobArtifact.Execution && (job.CancelRequested || job.ComfyControl?.PauseRequested == true) && job.RemoteUnconfirmed;
        var recoveredOutput = recoveringCancelledOutputs && CanWriteCancelledOutput(job) && artifact is AiJobArtifact.Execution or AiJobArtifact.Progress or AiJobArtifact.Result;
        if (lease == Guid.Empty || job.LeaseId != lease ||
            !(job.State == AiJobState.Running && (!job.CancelRequested || artifact == AiJobArtifact.Execution) || receiptAfterCancellation || recoveredOutput))
            throw new AiJobLeaseException();
        var path = ArtifactPath(id, artifact);
        using var artifactGate = await ProjectFiles.LockAsync(path, ct);
        await AtomicJsonFile.WriteAsync(path, captured, ct);
    }
    public async Task<AiJobHeader> RequeueAsync(Guid id, CancellationToken ct = default)
    {
        using var gate = await ProjectFiles.LockAsync(Index, ct); var document = await ReadIndexAsync(ct);
        var job = document.Jobs.SingleOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("AI request not found.");
        if (!job.CanRetryCaptured)
            throw new WorkspaceStoreException("This job cannot be resumed. Inspect its result and explicitly generate again when needed.");
        job = job with { State = AiJobState.Waiting, LeaseId = null, StartedUtc = null, FinishedUtc = null, Error = null, Unread = false, ActivityClearedUtc = null, Version = job.Version + 1 };
        await PublishAsync(document with { Jobs = [.. document.Jobs.Where(j => j.Id != id), job] }, ct);
        return job;
    }
    public async Task WriteOperationAsync<T>(Guid id, Guid lease, string operation, AiOperationArtifact artifact, T value, CancellationToken ct = default, bool recoveringCancelledOutputs = false)
    {
        var captured = JsonSerializer.SerializeToElement(value, AtomicJsonFile.Options);
        using var gate = await ProjectFiles.LockAsync(Index, ct);
        var job = (await ReadIndexAsync(ct)).Jobs.SingleOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("AI request not found.");
        var recoveredOutput = recoveringCancelledOutputs && CanWriteCancelledOutput(job) && artifact is AiOperationArtifact.Output or AiOperationArtifact.Video or AiOperationArtifact.Timings;
        if (lease == Guid.Empty || job.LeaseId != lease || job.State != AiJobState.Running || job.CancelRequested && !recoveredOutput) throw new AiJobLeaseException();
        var path = OperationPath(id, operation, artifact);
        if (Directory.Exists(Path.GetDirectoryName(path)) && await AtomicJsonFile.ReadAsync<AiOperationData<JsonElement>>(path, ct) is { } existing)
        {
            if (existing.Operation != operation || !JsonElement.DeepEquals(existing.Data, captured))
                throw new WorkspaceStoreException("This operation already has different saved inputs or outputs. They have not been replaced.");
            return;
        }
        await AtomicJsonFile.WriteAsync(path, new AiOperationData<JsonElement>(operation, captured), ct);
    }
    public async Task<T?> ReadOperationAsync<T>(Guid id, string operation, AiOperationArtifact artifact, CancellationToken ct = default)
    {
        var path = OperationPath(id, operation, artifact);
        if (!Directory.Exists(Path.GetDirectoryName(path))) return default;
        var saved = await AtomicJsonFile.ReadAsync<AiOperationData<T>>(path, ct);
        if (saved is not null && saved.Operation != operation) throw new WorkspaceStoreException("The operation artifact has a different identity.");
        return saved is null ? default : saved.Data;
    }
    public async Task<T?> ReadArtifactAsync<T>(Guid id, AiJobArtifact artifact, CancellationToken ct = default)
    {
        if (!Directory.Exists(DirectoryFor(id))) return default;
        // Writers publish by renaming over the artifact, which Windows refuses while it is open. Share their lock: a page
        // polling a busy request must not hold the file through a slow read and make the job's final result fail to save.
        var path = ArtifactPath(id, artifact); using var gate = await ProjectFiles.LockAsync(path, ct);
        return await AtomicJsonFile.ReadAsync<T>(path, ct);
    }
    private static bool CanWriteCancelledOutput(AiJobHeader job) => job.State == AiJobState.Running && job.CancelRequested &&
        job.Backend == AiBackend.ComfyUI && job.Kind is AiJobKind.Video or AiJobKind.ReelVideo;
}

public sealed class AiJobLeaseException() : OperationCanceledException("This AI worker no longer owns the request.");
