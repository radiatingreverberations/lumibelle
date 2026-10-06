using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiJobCoordinator(IAiJobStore store, IAiSettingsStore settings, IEnumerable<IAiJobHandler> handlers,
    TimeProvider clock, ILogger<AiJobCoordinator> logger, ICodexClient? codex = null, IClaudeCodeClient? claude = null) : BackgroundService
{
    private readonly IReadOnlyDictionary<AiJobKind, IAiJobHandler> _handlers = IndexHandlers(handlers);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _commands = new(1);
    private readonly ConcurrentDictionary<Guid, Worker> _workers = new();
    private readonly ConcurrentDictionary<Guid, AiJobProgress> _progress = new();
    private readonly HashSet<Guid> _reconciled = [];
    private AiQueueDocument _view = new();
    private CancellationToken _stopping;
    private int _ownsExecution;
    private string? _errorSource;
    public AiQueueDocument View => Volatile.Read(ref _view);
    public string? Error { get; private set; }
    public event Action? Changed;
    public AiJobProgress? Progress(Guid id) => _progress.GetValueOrDefault(id);
    public Task<AiJobProgress?> ReadProgressAsync(Guid id, CancellationToken ct = default) => _progress.TryGetValue(id, out var progress)
        ? Task.FromResult<AiJobProgress?>(progress) : store.ReadArtifactAsync<AiJobProgress>(id, AiJobArtifact.Progress, ct);

    private static Dictionary<AiJobKind, IAiJobHandler> IndexHandlers(IEnumerable<IAiJobHandler> handlers)
    {
        var result = new Dictionary<AiJobKind, IAiJobHandler>();
        foreach (var handler in handlers)
            foreach (var kind in handler.Kinds)
                if (!Enum.IsDefined(kind) || !result.TryAdd(kind, handler)) throw new InvalidOperationException($"Duplicate or invalid AI handler: {kind}.");
        return result;
    }
    public async Task<AiJobHeader> EnqueueAsync(AiJobSubmission request, CancellationToken ct = default)
    {
        if (!_handlers.ContainsKey(request.Kind)) throw new WorkspaceStoreException("This AI operation has no background handler installed.");
        var job = await store.EnqueueAsync(request, ct); await RefreshAsync(ct); Wake(); return job;
    }
    public async Task<AiBatchAppend> ExtendBatchAsync(Guid rootId, Guid commandId, Guid originTabId, CancellationToken ct = default)
    {
        var document = await store.ReadAsync(ct);
        var acknowledged = document.Jobs.FirstOrDefault(j => j.Batch?.Candidates.Any(c => c.AppendCommandId == commandId) == true);
        if (acknowledged is null)
        {
            var root = document.Jobs.SingleOrDefault(j => j.Id == rootId && j.Batch?.RootId == rootId) ?? throw new WorkspaceStoreException("Batch not found.");
            if (!_handlers.TryGetValue(root.Kind, out var handler) || handler is not IAiBatchJobHandler batchHandler)
                throw new WorkspaceStoreException("This batch cannot be extended by the installed handler.");
            try { await batchHandler.ValidateExtensionAsync(root, await store.ReadSnapshotAsync(root.Id, ct), ct); }
            catch when (!ct.IsCancellationRequested)
            {
                // A simultaneous retry may have published this command while validation
                // was waiting. Return that acknowledgement even if inputs since changed.
                if (!(await store.ReadAsync(ct)).Jobs.Any(j => j.Batch?.RootId == rootId &&
                        j.Batch.Candidates.Any(c => c.AppendCommandId == commandId))) throw;
            }
        }
        var added = await store.ExtendBatchAsync(rootId, commandId, originTabId, ct);
        await RefreshAsync(ct); Wake(); return added;
    }
    public async Task SetPausedAsync(AiBackend provider, bool paused, CancellationToken ct = default)
    {
        if (provider == AiBackend.Codex && !paused && codex is not null)
        {
            // Resuming only confirms Codex is still connected; the next request shows
            // whether a usage limit has reset.
            var check = await codex.CheckAsync((await settings.LoadAsync(ct)).Codex, ct);
            if (!check.Success) throw new AiGenerationException(check.Message);
        }
        if (provider == AiBackend.ClaudeCode && !paused && claude is not null)
        {
            // Claude Code does not report its allowance. Resuming only confirms the CLI
            // is still signed in; the next request shows whether the limit has reset.
            var check = await claude.CheckAsync((await settings.LoadAsync(ct)).ClaudeCode, ct);
            if (!check.Success) throw new AiGenerationException(check.Message);
        }
        await store.SetPausedAsync(provider, paused, ct);
        if (provider == AiBackend.ComfyUI && paused) await StopPausedObserversAsync(ct);
        await RefreshAsync(ct); Wake();
    }
    public async Task MoveAsync(Guid id, int offset, bool first = false, CancellationToken ct = default)
    { await store.MoveAsync(id, offset, first, ct); await RefreshAsync(ct); Wake(); }
    public async Task MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        if (View.Jobs.FirstOrDefault(j => j.Id == id) is { } observed)
            await ChangeActivityAsync([observed], AiActivityChange.Read, ct);
    }
    public async Task<IReadOnlyList<AiJobHeader>> ChangeActivityAsync(IEnumerable<AiJobHeader> observed, AiActivityChange change, CancellationToken ct = default)
    {
        var changed = await store.ChangeActivityAsync(observed.Select(j => new AiActivityObservation(j.Id, j.Version)).DistinctBy(j => j.Id).ToArray(), change, ct);
        await RefreshAsync(ct);
        return changed;
    }

    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct);
        try
        {
            // Persist intent before cancelling an observer or contacting ComfyUI. The
            // composer unlocks immediately even when remote cancellation is unavailable.
            var job = await store.UpdateAsync(id, j => j.State is AiJobState.Completed or AiJobState.Cancelled && !j.RemoteUnconfirmed ? j : j with
            {
                CancelRequested = true, ComfyControl = null,
                State = j.State == AiJobState.Running ? AiJobState.Running : AiJobState.Cancelled,
                FinishedUtc = j.State == AiJobState.Running ? null : clock.GetUtcNow(),
                Recovery = j.RemoteUnconfirmed ? AiJobRecovery.CheckStatus : AiJobRecovery.None,
                Error = j.State == AiJobState.Waiting ? null : j.Error
            }, ct);
            if (await HasActiveWorkerAsync(job, ct)) _workers[id].Cancellation.Cancel();
            else if (job.RemoteUnconfirmed && Volatile.Read(ref _ownsExecution) == 1) await StartRecoveryAsync(job, ct);
            await RefreshAsync(ct);
        }
        finally { _commands.Release(); Wake(); }
    }

    public async Task ResumeAsync(Guid id, CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct);
        try
        {
            var job = (await store.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("AI request not found.");
            if (await HasActiveWorkerAsync(job, ct)) return;
            if (!job.RemoteUnconfirmed && !job.CanRecoverCancelledOutputs && job.State is AiJobState.Waiting or AiJobState.Running or AiJobState.Completed or AiJobState.Cancelled) return;
            if (job.RemoteUnconfirmed || job.CanRecoverCancelledOutputs)
            {
                if (Volatile.Read(ref _ownsExecution) != 1) throw new WorkspaceStoreException("The AI queue worker is unavailable. Check status again when it reconnects.");
                await StartRecoveryAsync(job, ct);
            }
            else await store.RequeueAsync(id, ct);
            await RefreshAsync(ct);
        }
        finally { _commands.Release(); Wake(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var owner = store.AcquireExecutionOwner();
                Volatile.Write(ref _ownsExecution, 1); ClearError("owner");
                while (!stoppingToken.IsCancellationRequested)
                {
                    try { await TickAsync(stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception e) { Failed("The AI queue could not publish its state. It will retry without resubmitting requests.", e); }
                    await WaitForWakeAsync(stoppingToken);
                }
                // Only stop local observation during shutdown. Remote jobs remain owned
                // by their durable receipts, ready for reconciliation after restart.
                foreach (var worker in _workers.Values) worker.Cancellation.Cancel();
                try { await Task.WhenAll(_workers.Values.Select(w => w.Task)).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception e) when (e is OperationCanceledException or TimeoutException) { }
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                Failed("The AI queue worker is unavailable. Saved requests are waiting safely.", e, "owner");
                try { await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
            finally { Volatile.Write(ref _ownsExecution, 0); }
        }
    }
    private async Task WaitForWakeAsync(CancellationToken ct)
    {
        using var pulse = new CancellationTokenSource(TimeSpan.FromSeconds(1), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, pulse.Token);
        try { await _wake.Reader.ReadAsync(linked.Token); }
        catch (OperationCanceledException) { }
    }
    private async Task TickAsync(CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            foreach (var entry in _workers.Where(w => w.Value.Task.IsCompleted).ToArray())
                if (_workers.TryRemove(entry.Key, out var finished)) finished.Cancellation.Dispose();
            var document = await store.ReadAsync(ct);
            foreach (var job in document.Jobs.Where(j => j.CancelRequested || j.ComfyControl?.PauseRequested == true))
                if (_workers.TryGetValue(job.Id, out var active)) active.Cancellation.Cancel();
            foreach (var job in document.Jobs.Where(j => j.State == AiJobState.Running || j.RemoteUnconfirmed || ComfyQueuePolicy.ShouldReconcile(j, document.Paused, clock.GetUtcNow())))
            {
                // Cancelling a sampler is asynchronous. Recheck cancelled jobs until
                // their owned workload has stopped, without resubmitting inference.
                var cancellationDue = job.CancelRequested && job.RemoteUnconfirmed && job.FinishedUtc is { } finished &&
                    clock.GetUtcNow() - finished >= TimeSpan.FromSeconds(5);
                var reconnectDue = ComfyQueuePolicy.ShouldReconcile(job, document.Paused, clock.GetUtcNow());
                if (_workers.ContainsKey(job.Id) || _reconciled.Contains(job.Id) && !cancellationDue && !reconnectDue) continue;
                if (job.ComfyControl is { PauseRequested: false } && document.Paused.Contains(AiBackend.ComfyUI)) continue;
                if (job.Backend == AiBackend.ComfyUI && _workers.Values.Any(w => w.Backend == AiBackend.ComfyUI)) continue;
                // Recovery handlers may read completed paid output from disk. An
                // interrupted stream yields an explicit-retry result without inference.
                await StartRecoveryAsync(job, ct);
                _reconciled.Add(job.Id);
            }
            await DispatchAsync(AiBackend.ComfyUI, 1, ct);
            try
            {
                // Concurrency is scheduling policy, not generation context. Reading the
                // saved value each tick also makes reductions take effect after active jobs.
                var concurrency = (await settings.LoadAsync(ct)).OpenRouterConcurrency;
                await DispatchAsync(AiBackend.OpenRouter, concurrency, ct);
                ClearError("openrouter");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { Failed("OpenRouter scheduling needs readable settings and writable job storage. It will retry without repeating active requests.", e, "openrouter"); }
            if (codex is not null)
            {
                var configured = (await settings.LoadAsync(ct)).Codex;
                if (!configured.Enabled) { if (!document.Jobs.Any(j => j.Backend == AiBackend.Codex && j.State == AiJobState.Running)) await codex.StopAsync(); }
                else await DispatchAsync(AiBackend.Codex, configured.Concurrency, ct);
            }
            if (claude is not null)
            {
                var configured = (await settings.LoadAsync(ct)).ClaudeCode;
                if (!configured.Enabled) { if (!document.Jobs.Any(j => j.Backend == AiBackend.ClaudeCode && j.State == AiJobState.Running)) await claude.StopAsync(); }
                else await DispatchAsync(AiBackend.ClaudeCode, configured.Concurrency, ct);
            }
            await RefreshAsync(ct);
            ClearError("queue");
        }
        finally { _commands.Release(); }
    }
    private async Task DispatchAsync(AiBackend backend, int concurrency, CancellationToken ct)
    {
        // A paused worker may have published Waiting just before its task exits.
        // Never claim it under a new lease while that observer is still registered.
        if (backend == AiBackend.ComfyUI && _workers.Values.Any(w => w.Backend == backend)) return;
        while (await store.ClaimNextAsync(backend, concurrency, ct) is { } job)
        {
            _reconciled.Add(job.Id);
            StartWorker(job, job.Recovery is AiJobRecovery.CheckStatus or AiJobRecovery.RetryOutput);
        }
    }
    // Callers hold _commands, so no worker can be added meanwhile. A worker whose job
    // is no longer Running has published its outcome and is only unwinding; it must
    // not swallow a recovery command the UI already offers for that outcome.
    private async Task<bool> HasActiveWorkerAsync(AiJobHeader job, CancellationToken ct)
    {
        if (!_workers.TryGetValue(job.Id, out var worker)) return false;
        if (job.State == AiJobState.Running && !worker.Task.IsCompleted) return true;
        await worker.Task.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        ct.ThrowIfCancellationRequested();
        if (_workers.TryRemove(KeyValuePair.Create(job.Id, worker))) worker.Cancellation.Dispose();
        return false;
    }
    private async Task StartRecoveryAsync(AiJobHeader job, CancellationToken ct)
    {
        if (await HasActiveWorkerAsync(job, ct)) return;
        var document = await store.ReadAsync(ct);
        if (job.Backend == AiBackend.ComfyUI && document.Jobs.Any(j => j.Id != job.Id && j.Backend == job.Backend && j.State == AiJobState.Running))
            throw new WorkspaceStoreException("Another job is still being reconciled for this provider.");
        job = await store.UpdateAsync(job.Id, j => j with
        {
            State = AiJobState.Running, Recovery = AiJobRecovery.CheckStatus, LeaseId = Guid.NewGuid(),
            StartedUtc = j.StartedUtc ?? clock.GetUtcNow(), FinishedUtc = null
        }, ct);
        var execution = await store.ReadArtifactAsync<AiJobExecution>(job.Id, AiJobArtifact.Execution, ct);
        var neverSubmitted = job.ComfyControl is { PauseRequested: false } && (execution?.Submissions.Count ?? 0) == 0;
        if (neverSubmitted) job = await store.UpdateAsync(job.Id, j => j with { ComfyControl = null }, ct);
        StartWorker(job, !neverSubmitted);
    }
    private void StartWorker(AiJobHeader job, bool recovering)
    {
        var worker = new Worker(CancellationTokenSource.CreateLinkedTokenSource(_stopping), job.Backend);
        if (!_workers.TryAdd(job.Id, worker)) { worker.Cancellation.Dispose(); return; }
        worker.Task = RunBatchWorkerAsync(job, recovering, worker.Cancellation.Token);
    }
    private async Task RunBatchWorkerAsync(AiJobHeader job, bool recovering, CancellationToken ct)
    {
        // Recovery reconciles receipts first. Only after it proves pending candidates
        // were never submitted may a fresh execution context handle those candidates.
        while (await RunWorkerAsync(job, recovering, ct))
        {
            recovering = false;
            while (!_stopping.IsCancellationRequested)
            {
                try { job = (await store.ReadAsync(_stopping)).Jobs.Single(j => j.Id == job.Id); ClearError($"publication/{job.Id}"); break; }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    Failed("The batch is waiting for readable job storage before continuing.", e, $"publication/{job.Id}");
                    try { await Task.Delay(TimeSpan.FromSeconds(1), clock, _stopping); }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
                }
            }
            if (_stopping.IsCancellationRequested) return;
        }
    }
    private async Task<bool> RunWorkerAsync(AiJobHeader job, bool recovering, CancellationToken ct)
    {
        var context = new AiJobContext(job, recovering, store, clock, OnProgress,
            e => { if (e is null) ClearError($"progress/{job.Id}"); else Failed("Live AI progress could not be checkpointed. The provider request is still being observed.", e, $"progress/{job.Id}"); }, ct);
        AiJobOutcome outcome;
        var codexAllowanceExceeded = false; var claudeLimitReached = false;
        JsonElement snapshot = default;
        _handlers.TryGetValue(job.Kind, out var handler);
        try
        {
            snapshot = await store.ReadSnapshotAsync(job.Id, ct);
            if (handler is null) throw new AiGenerationException("This saved operation's background handler is unavailable.");
            if (job.CancelRequested || job.ComfyControl?.PauseRequested == true) throw new OperationCanceledException(ct);
            var task = recovering ? handler.RecoverAsync(context, snapshot, ct) : handler.ExecuteAsync(context, snapshot, ct);
            ObserveLateFailure(task);
            // Owned ComfyUI observers and CLI processes must finish unwinding before
            // a paused request can be claimed again under a new lease.
            outcome = job.Backend is AiBackend.Codex or AiBackend.ClaudeCode or AiBackend.ComfyUI ? await task : await task.WaitAsync(ct);
            if (outcome.State is not (AiJobState.Completed or AiJobState.Cancelled or AiJobState.NeedsAttention)) throw new AiGenerationException("The AI handler returned an invalid completion state.");
            if (job.Batch is not null && outcome.State == AiJobState.Completed && outcome.CompletedCandidates is null or < 0)
                throw new AiGenerationException("The batch handler did not account for its candidates.");
            if (job.Batch is not null && outcome.State == AiJobState.Completed &&
                (outcome.CompletedCandidates > (await context.CurrentAsync(ct)).Batch!.Candidates.Count || !recovering && outcome.CompletedCandidates == 0))
                throw new AiGenerationException("The batch handler returned invalid candidate accounting.");
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || job.CancelRequested || job.ComfyControl?.PauseRequested == true)
        { outcome = new(AiJobState.Cancelled); }
        catch (AiJobRecoveryException e) { outcome = AiJobOutcome.Attention(e.Message, e.Recovery); }
        catch (CodexAllowanceException e) { codexAllowanceExceeded = true; outcome = AiJobOutcome.Attention(e.Message, AiJobRecovery.GenerateAgain); }
        catch (ClaudeCodeLimitException e) { claudeLimitReached = true; outcome = AiJobOutcome.Attention(e.Message, AiJobRecovery.GenerateAgain); }
        catch (Exception e)
        { outcome = AiJobOutcome.Attention(e.Message, recovering ? AiJobRecovery.CheckStatus : AiJobRecovery.GenerateAgain); }
        if (_stopping.IsCancellationRequested) return false;

        bool remoteUnknown; var hasSubmissions = false;
        try
        {
            remoteUnknown = false;
            if (job.Backend == AiBackend.ComfyUI)
            {
                var execution = await context.ExecutionAsync();
                hasSubmissions = execution.Submissions.Count > 0;
                remoteUnknown = execution.MayBeRunning;
            }
        }
        catch (Exception e)
        {
            remoteUnknown = job.Backend == AiBackend.ComfyUI;
            outcome = AiJobOutcome.Attention(e.Message, AiJobRecovery.CheckStatus);
        }
        // A transport cannot report success while its owned remote request is uncertain.
        if (remoteUnknown) outcome = AiJobOutcome.Attention(outcome.Error ?? "The ComfyUI submission could not be confirmed. Check its status before continuing.", AiJobRecovery.CheckStatus);
        var cancelled = outcome.State == AiJobState.Cancelled || ct.IsCancellationRequested || job.CancelRequested || job.ComfyControl?.PauseRequested == true;
        if (cancelled && remoteUnknown && handler is not null && snapshot.ValueKind == JsonValueKind.Object)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _stopping);
            try
            {
                var cancel = handler.CancelRemoteAsync(context, snapshot, cancellation.Token); ObserveLateFailure(cancel);
                remoteUnknown = !await cancel.WaitAsync(cancellation.Token);
                if (!remoteUnknown)
                    foreach (var remote in (await context.ExecutionAsync()).Submissions.Where(s => s.MayBeRunning))
                        await context.FinishRemoteAsync(remote.Operation, cancelled: true);
            }
            catch (Exception e) { remoteUnknown = true; logger.LogWarning(e, "Remote cancellation of AI job {Job} remains unconfirmed", job.Id); }
        }
        if (_stopping.IsCancellationRequested) return false;
        if (cancelled && !remoteUnknown)
        {
            outcome = job.Batch is not null ? AiJobOutcome.CancelledCheckpoint(outcome.CompletedCandidates ?? 0) : new(AiJobState.Cancelled);
            if (handler is IAiCancelledOutputHandler outputHandler)
            {
                // The inference worker has fully unwound. Retrieval has a separate,
                // bounded lifetime and the same lease; cancellation stays durable.
                using var retrievalTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2), clock);
                using var retrieval = CancellationTokenSource.CreateLinkedTokenSource(retrievalTimeout.Token, _stopping);
                try
                {
                    var stoppedJob = (await store.ReadAsync(retrieval.Token)).Jobs.Single(j => j.Id == job.Id);
                    if (stoppedJob.CancelRequested)
                    {
                        var recovery = new AiJobContext(stoppedJob, true, store, clock, OnProgress, _ => { }, retrieval.Token, recoveringCancelledOutputs: true);
                        var captured = await store.ReadSnapshotAsync(job.Id, retrieval.Token);
                        await recovery.ReportAsync(new(new(GenerationPhase.Downloading, "Recovering completed takes…")), true);
                        outcome = await outputHandler.RecoverCancelledOutputsAsync(recovery, captured, retrieval.Token);
                    }
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return false; }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Completed output retrieval for cancelled AI job {Job} needs retry", job.Id);
                    outcome = new(AiJobState.Cancelled, true, "Generation stopped. Some completed takes could not be retrieved. Use Recover completed takes to try again. " + e.Message, AiJobRecovery.RetryOutput);
                }
            }
        }

        // Retrying publication is safe: inference and results are already finished. Do
        // not re-enter a handler because a manifest was temporarily locked or unavailable.
        var published = false;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                var extended = false;
                if (codexAllowanceExceeded) await store.SetPausedAsync(AiBackend.Codex, true, _stopping);
                if (claudeLimitReached) await store.SetPausedAsync(AiBackend.ClaudeCode, true, _stopping);
                if (!published) await store.UpdateAsync(job.Id, j =>
                {
                    if (j.LeaseId != job.LeaseId) return j;
                    if (!j.CancelRequested && j.ComfyControl?.PauseRequested == true)
                        return ComfyQueuePolicy.Suspended(j, remoteUnknown, hasSubmissions, clock.GetUtcNow());
                    // This check and ExtendBatch share the index lock. An acknowledged
                    // append cannot be stranded between a handler's return and completion.
                    if (!j.CancelRequested && !cancelled && !remoteUnknown && outcome.State == AiJobState.Completed &&
                        j.Batch is { } batch && outcome.CompletedCandidates < batch.Candidates.Count)
                    { extended = true; return j; }
                    return j with {
                    State = j.CancelRequested || cancelled ? AiJobState.Cancelled : outcome.State,
                    Recovery = remoteUnknown ? AiJobRecovery.CheckStatus : outcome.Recovery,
                    FinishedUtc = clock.GetUtcNow(), RemoteUnconfirmed = remoteUnknown, Unread = outcome.Reviewable || remoteUnknown || outcome.Error is not null,
                    ComfyControl = !remoteUnknown && outcome.Recovery != AiJobRecovery.CheckStatus ? null : j.ComfyControl,
                    Error = remoteUnknown && (j.CancelRequested || cancelled) ? "Cancelled locally. Waiting for ComfyUI to confirm it has stopped; status is checked automatically." : outcome.Error
                    };
                }, _stopping);
                if (extended) return true;
                published = true;
                ClearError($"publication/{job.Id}");
                await RefreshAsync(_stopping); Wake(); return false;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return false; }
            catch (Exception e)
            {
                Failed("An AI result is waiting for its job status to be saved. It will retry publication without generating again.", e, $"publication/{job.Id}");
                try { await Task.Delay(TimeSpan.FromSeconds(1), clock, _stopping); }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return false; }
            }
        }
        return false;
    }
    private void ObserveLateFailure(Task task) => _ = task.ContinueWith(t => logger.LogWarning(t.Exception, "An interrupted AI observer finished with an error"),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var view = await store.ReadAsync(ct);
        while (true)
        {
            var previous = View;
            // Slow observers must not restore a queued/running lock after another
            // refresh has already observed completion or cancellation.
            if (view.Revision <= previous.Revision) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _view, view, previous), previous)) { Notify(); return; }
        }
    }
    private void OnProgress(Guid id, AiJobProgress progress) { _progress[id] = progress; Notify(); }
    private void Failed(string message, Exception e, string source = "queue") { _errorSource = source; Error = message; logger.LogError(e, "{Message}", message); Notify(); }
    private void ClearError(string source) { if (_errorSource == source) { _errorSource = null; Error = null; Notify(); } }
    private void Wake() => _wake.Writer.TryWrite(true);
    private void Notify()
    {
        if (Changed is not { } changed) return;
        foreach (Action listener in changed.GetInvocationList())
            try { listener(); } catch (Exception e) { logger.LogWarning(e, "An AI activity subscriber failed"); }
    }
    private sealed class Worker(CancellationTokenSource cancellation, AiBackend backend)
    {
        public AiBackend Backend { get; } = backend;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
