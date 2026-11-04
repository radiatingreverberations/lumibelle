using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed record AiJobOutcome(AiJobState State, bool Reviewable = false, string? Error = null, AiJobRecovery Recovery = AiJobRecovery.None, int? CompletedCandidates = null)
{
    public static AiJobOutcome Complete(bool reviewable = true) => new(AiJobState.Completed, reviewable);
    public static AiJobOutcome BatchCheckpoint(int completed) => new(AiJobState.Completed, true, CompletedCandidates: completed);
    public static AiJobOutcome CancelledCheckpoint(int completed) => new(AiJobState.Cancelled, true, CompletedCandidates: completed);
    public static AiJobOutcome Attention(string error, AiJobRecovery recovery, bool reviewable = true) => new(AiJobState.NeedsAttention, reviewable, error, recovery);
}
public sealed class AiJobRecoveryException(string message, AiJobRecovery recovery, Exception? inner = null) : Exception(message, inner)
{
    public AiJobRecovery Recovery { get; } = recovery;
}

public interface IAiJobHandler
{
    IReadOnlyCollection<AiJobKind> Kinds { get; }
    Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct);
    // Recover may observe an accepted request or retrieve saved output. It cannot
    // submit arbitrary inference. Explicit ComfyUI stop/retry authorization permits
    // only the transport's exact captured-workflow replay; BeginRemote stays guarded.
    Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct);
    Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct);
}
public interface IAiBatchJobHandler : IAiJobHandler
{
    Task ValidateExtensionAsync(AiJobHeader root, JsonElement snapshot, CancellationToken ct);
}
public interface IAiCancelledOutputHandler
{
    // Retrieve already-rendered output only, after the owned remote work has stopped.
    Task<AiJobOutcome> RecoverCancelledOutputsAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct);
}

public sealed partial class AiJobContext
{
    private readonly IAiJobStore _store;
    private readonly TimeProvider _clock;
    private readonly Action<Guid, AiJobProgress> _onProgress;
    private readonly Action<Exception?> _onCheckpointError;
    private readonly SemaphoreSlim _journalGate = new(1);
    private DateTimeOffset _progressSaved, _resultSaved;
    private GenerationPhase? _phase;
    private List<int>? _candidateOrder;
    public AiJobHeader Job { get; }
    public bool Recovering { get; }
    internal bool RecoveringCancelledOutputs { get; }
    // Set when a readable ComfyUI queue and history had no record of this operation.
    // Kept here because handlers may catch or replace the exception that reports it.
    internal string? MissingRemoteOperation { get; set; }
    public CancellationToken Cancellation { get; }
    internal TimeProvider Clock => _clock;
    public string Directory => _store.DirectoryFor(Job.Id);

    internal AiJobContext(AiJobHeader job, bool recovering, IAiJobStore store, TimeProvider clock,
        Action<Guid, AiJobProgress> onProgress, Action<Exception?> onCheckpointError, CancellationToken cancellation, bool recoveringCancelledOutputs = false)
    {
        if (job.LeaseId is null) throw new ArgumentException("The job has not been claimed.", nameof(job));
        if (recoveringCancelledOutputs && (!recovering || !job.CancelRequested || job.Kind is not (AiJobKind.Video or AiJobKind.ReelVideo)))
            throw new ArgumentException("Cancelled-output recovery requires a cancelled video request.", nameof(job));
        RecoveringCancelledOutputs = recoveringCancelledOutputs;
        Job = job; Recovering = recovering; _store = store; _clock = clock;
        _onProgress = onProgress; _onCheckpointError = onCheckpointError; Cancellation = cancellation;
    }

    public Task<T?> ReadAsync<T>(AiJobArtifact artifact, CancellationToken ct = default) => _store.ReadArtifactAsync<T>(Job.Id, artifact, ct);
    public async Task<AiJobHeader> CurrentAsync(CancellationToken ct = default)
    {
        var current = (await _store.ReadAsync(ct)).Jobs.Single(j => j.Id == Job.Id);
        if (current.LeaseId != Job.LeaseId || current.State != AiJobState.Running || current.CancelRequested && !RecoveringCancelledOutputs || current.ComfyControl?.PauseRequested == true) throw new AiJobLeaseException();
        return current;
    }
    public async Task MarkReviewableAsync(CancellationToken ct = default)
    {
        // Even an already unread request needs a new observation version for its next candidate.
        await _store.UpdateAsync(Job.Id, j => j.LeaseId == Job.LeaseId && j.State == AiJobState.Running && !j.CancelRequested ? j with { Unread = true, Version = j.Version + 1 } : j, ct);
    }
    public async Task<AiJobExecution> ExecutionAsync(CancellationToken ct = default)
    {
        var execution = await ReadAsync<AiJobExecution>(AiJobArtifact.Execution, ct) ?? new([]);
        if (execution.Submissions is null || execution.Submissions.Any(s => s is null || string.IsNullOrWhiteSpace(s.Operation) ||
                !Guid.TryParse(s.ClientId, out _) || s.StartedUtc == default || !Enum.IsDefined(s.State) ||
                !Uri.TryCreate(s.ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                s.State == AiRemoteState.Accepted && string.IsNullOrWhiteSpace(s.PromptId) ||
                s.RetryId == Guid.Empty || (s.RetryId is null) != (s.ArtifactOperation is null) ||
                s.RetryId is { } retry && s.ArtifactOperation != ComfyQueuePolicy.AttemptOperation(s.Operation, retry)) ||
            execution.Submissions.Select(s => s.Operation).Distinct(StringComparer.Ordinal).Count() != execution.Submissions.Count)
            throw new WorkspaceStoreException("The saved submission receipt is invalid. Check the remote job before submitting anything else.");
        return execution;
    }

    public async Task<AiRemoteSubmission> BeginRemoteAsync(string operation, string serverUrl, string? clientId = null)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (Recovering) throw new AiGenerationException("Recovery cannot submit another inference request. Use Generate again explicitly.");
        if (Job.Backend != AiBackend.ComfyUI || string.IsNullOrWhiteSpace(operation) || operation.Length > 200 || clientId is not null && !Guid.TryParse(clientId, out _) ||
            !Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new AiGenerationException("A ComfyUI submission needs an exact operation and server.");
        await _journalGate.WaitAsync(Cancellation);
        try
        {
            await CurrentAsync(Cancellation);
            var execution = await ExecutionAsync(Cancellation);
            if (execution.Submissions.Any(s => s.Operation == operation || s.MayBeRunning))
                throw new AiGenerationException("This batch already has an accepted or uncertain submission. Check its status instead of submitting again.");
            var submission = new AiRemoteSubmission(operation, uri.AbsoluteUri.TrimEnd('/'), clientId ?? Guid.NewGuid().ToString("D"), _clock.GetUtcNow());
            // Publication precedes the network call. If acceptance is lost, restart sees
            // an uncertain submission rather than a request that appears safe to repeat.
            await WriteAsync(AiJobArtifact.Execution, execution with { Submissions = [.. execution.Submissions, submission] }, Cancellation);
            Cancellation.ThrowIfCancellationRequested();
            return submission;
        }
        finally { _journalGate.Release(); }
    }
    public Task AcceptRemoteAsync(string operation, string promptId) => ChangeSubmissionAsync(operation, s =>
    {
        if (string.IsNullOrWhiteSpace(promptId) || promptId.Length > 200 || s.PromptId is not null && s.PromptId != promptId)
            throw new AiGenerationException("The ComfyUI acceptance receipt does not match this submission.");
        return s with { PromptId = promptId, State = s.State == AiRemoteState.Submitting ? AiRemoteState.Accepted : s.State };
    });
    public Task FinishRemoteAsync(string operation, bool cancelled = false) => ChangeSubmissionAsync(operation, s =>
        s with { State = cancelled ? AiRemoteState.Cancelled : AiRemoteState.Finished });
    private async Task ChangeSubmissionAsync(string operation, Func<AiRemoteSubmission, AiRemoteSubmission> change)
    {
        // An acceptance receipt must survive user cancellation. The lease check still
        // prevents a superseded worker from changing a newer execution's journal.
        await _journalGate.WaitAsync();
        try
        {
            var execution = await ExecutionAsync();
            if (!execution.Submissions.Any(s => s.Operation == operation)) throw new AiGenerationException("Submission intent was not saved.");
            await WriteAsync(AiJobArtifact.Execution, execution with { Submissions = execution.Submissions.Select(s => s.Operation == operation ? change(s) : s).ToArray() });
        }
        finally { _journalGate.Release(); }
    }
    public async Task ReportAsync(AiJobProgress progress, bool checkpoint = false)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (Job.Batch is not null)
        {
            if (_candidateOrder is null)
            {
                try { _candidateOrder = [.. (await ReadAsync<AiJobProgress>(AiJobArtifact.Progress, Cancellation))?.CandidateExecutionOrder ?? []]; }
                catch (Exception e) when (e is WorkspaceStoreException or IOException)
                { _candidateOrder = []; _onCheckpointError(e); }
            }
            // Preparation can mention the first requested candidate before ComfyUI
            // chooses a branch. Count only work that has actually started rendering.
            if (progress.Candidate is > 0 && progress.Progress.Phase is GenerationPhase.Generating or GenerationPhase.Finalizing &&
                !_candidateOrder.Contains(progress.Candidate.Value))
            {
                _candidateOrder.Add(progress.Candidate.Value);
                checkpoint = true;
            }
            progress = progress with { CandidateExecutionOrder = _candidateOrder.ToArray() };
        }
        _onProgress(Job.Id, progress);
        var now = _clock.GetUtcNow();
        if (!checkpoint && _phase == progress.Progress.Phase && now - _progressSaved < TimeSpan.FromSeconds(2)) return;
        try
        {
            await WriteAsync(AiJobArtifact.Progress, progress, Cancellation);
            _progressSaved = now; _phase = progress.Progress.Phase;
            _onCheckpointError(null);
        }
        catch (Exception e) when (e is WorkspaceStoreException or IOException) { _onCheckpointError(e); }
    }
    public async Task SaveObservedUsageAsync(OpenRouterRequestUsage usage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
        await _store.WriteObservedUsageAsync(Job.Id, Job.LeaseId!.Value, usage, timeout.Token);
    }
    public async Task SaveResultAsync<T>(T result, bool checkpoint = true)
    {
        Cancellation.ThrowIfCancellationRequested(); var now = _clock.GetUtcNow();
        if (!checkpoint && now - _resultSaved < TimeSpan.FromSeconds(2)) return;
        // Final output failures are recoverable failures, never a successful job whose
        // review disappeared. Streaming handlers can checkpoint partial responses.
        await WriteAsync(AiJobArtifact.Result, result, Cancellation); _resultSaved = now;
    }
    private Task WriteAsync<T>(AiJobArtifact artifact, T value, CancellationToken ct = default) =>
        _store.WriteOwnedArtifactAsync(Job.Id, Job.LeaseId!.Value, artifact, value, ct, RecoveringCancelledOutputs);
    public async Task SaveOperationAsync<T>(string operation, AiOperationArtifact artifact, T value, CancellationToken ct = default)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (Recovering && artifact == AiOperationArtifact.Request) throw new AiGenerationException("Recovery cannot replace a submitted workflow.");
        await _store.WriteOperationAsync(Job.Id, Job.LeaseId!.Value, await OperationForAsync(operation, artifact, ct), artifact, value, ct, RecoveringCancelledOutputs);
    }
    public async Task<T?> ReadOperationAsync<T>(string operation, AiOperationArtifact artifact, CancellationToken ct = default) =>
        await _store.ReadOperationAsync<T>(Job.Id, await OperationForAsync(operation, artifact, ct), artifact, ct);
}
