using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed partial class AiJobCoordinatorTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.QueueTests", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly FakeAiSettingsStore _settings = new();
    private readonly List<AiJobCoordinator> _coordinators = [];
    private FileAiJobStore Store => new(new StorageTestEnvironment(_root), TimeProvider.System);
    private string Index => Path.Combine(_root, "App_Data", "ai-jobs", "queue.json");
    private static AiJobSubmission Request(AiBackend provider = AiBackend.ComfyUI, AiJobKind kind = AiJobKind.ScriptAssistant, AiJobTarget? target = null) =>
        AiJobSubmission.Create(Guid.NewGuid(), kind, provider, target ?? new(Guid.NewGuid()), "QA", "Captured target", Guid.NewGuid(), new { prompt = "Original instruction" });
    private async Task<AiJobCoordinator> Start(FakeHandler handler, TimeProvider? clock = null)
    {
        var coordinator = new AiJobCoordinator(Store, _settings, [handler], clock ?? TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        _coordinators.Add(coordinator); await coordinator.StartAsync(_ct); return coordinator;
    }
    private async Task<AiJobHeader> State(Guid id, Func<AiJobHeader, bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            var job = (await Store.ReadAsync(timeout.Token)).Jobs.Single(j => j.Id == id);
            if (condition(job)) return job;
            await Task.Delay(20, timeout.Token);
        }
    }
    [Fact]
    public async Task ProvidersHaveIndependentFifoQueuesAndUnsubscribingDoesNotCancel()
    {
        var a = await Store.EnqueueAsync(Request(), _ct);
        var b = await Store.EnqueueAsync(Request(kind: AiJobKind.AssetExtraction), _ct);
        var c = await Store.EnqueueAsync(Request(kind: AiJobKind.ImageCreate, target: new(Guid.NewGuid(), Guid.NewGuid())), _ct);
        var router = await Store.EnqueueAsync(Request(AiBackend.OpenRouter), _ct);
        await Store.SetPausedAsync(AiBackend.ComfyUI, true, _ct);
        var handler = new FakeHandler(); var queue = await Start(handler);
        Action brokenView = () => throw new InvalidOperationException("A disposed view"); queue.Changed += brokenView;
        var remote = await handler.Next(_ct); Assert.Equal(router.Id, remote.Context.Job.Id);
        Assert.Equal("Original instruction", remote.Snapshot.GetProperty("prompt").GetString());
        await queue.MoveAsync(c.Id, 0, first: true, ct: _ct); await queue.SetPausedAsync(AiBackend.ComfyUI, false, _ct);
        var first = await handler.Next(_ct); Assert.Equal(c.Id, first.Context.Job.Id);
        queue.Changed -= brokenView;
        await first.Context.ReportAsync(new(new(GenerationPhase.Generating, "Sampling", 2, 20, "steps")));
        Assert.Equal(2, queue.Progress(c.Id)!.Progress.Current);
        Assert.Equal(AiJobState.Running, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == router.Id).State);
        await first.Complete(); Assert.Equal(a.Id, (await handler.Next(_ct)).Context.Job.Id);
        await remote.Complete(); var completed = await State(router.Id, j => j.State == AiJobState.Completed);
        // MarkRead uses the version observed by the UI, which is refreshed after persistence.
        await Eventually(() => Task.FromResult(queue.View.Jobs.Single(j => j.Id == router.Id).Version == completed.Version));
        await queue.MarkReadAsync(router.Id, _ct); Assert.False((await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == router.Id).Unread);
        Assert.Equal(AiJobState.Waiting, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == b.Id).State);
    }
    [Fact]
    public async Task LoweringOpenRouterConcurrencyLetsExistingRequestsFinish()
    {
        _settings.Value = _settings.Value with { OpenRouterConcurrency = 3 };
        for (var i = 0; i < 4; i++) await Store.EnqueueAsync(Request(AiBackend.OpenRouter), _ct);
        var handler = new FakeHandler(); var queue = await Start(handler);
        var running = new[] { await handler.Next(_ct), await handler.Next(_ct), await handler.Next(_ct) };
        _settings.Value = _settings.Value with { OpenRouterConcurrency = 1 };
        await running[0].Complete(); await State(running[0].Context.Job.Id, j => j.State == AiJobState.Completed);
        await queue.SetPausedAsync(AiBackend.OpenRouter, true, _ct); await queue.SetPausedAsync(AiBackend.OpenRouter, false, _ct);
        Assert.Equal(3, handler.Calls.Count); Assert.All(running.Skip(1), r => Assert.False(r.Context.Cancellation.IsCancellationRequested));
        await running[1].Complete(); await State(running[1].Context.Job.Id, j => j.State == AiJobState.Completed);
        Assert.Equal(3, handler.Calls.Count);
        await running[2].Complete(); var fourth = await handler.Next(_ct); Assert.DoesNotContain(running, r => r.Context.Job.Id == fourth.Context.Job.Id);
        await fourth.Complete(); await State(fourth.Context.Job.Id, j => j.State == AiJobState.Completed);
    }
    [Theory]
    [InlineData(AiJobKind.ImageEdit)]
    [InlineData(AiJobKind.Video)]
    public async Task OfflineCancellationUnlocksComposerButReservesComfyUntilReconciled(AiJobKind kind)
    {
        var target = kind == AiJobKind.Video ? new AiJobTarget(Guid.NewGuid(), ShotId: Guid.NewGuid()) : new AiJobTarget(Guid.NewGuid(), Guid.NewGuid());
        var a = await Store.EnqueueAsync(Request(kind: kind, target: target), _ct);
        var waiting = await Store.EnqueueAsync(Request(), _ct);
        var handler = new FakeHandler { Cancel = async context =>
        {
            Assert.True((await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == context.Job.Id).CancelRequested);
            throw new HttpRequestException("ComfyUI is offline");
        } };
        var queue = await Start(handler); var active = await handler.Next(_ct);
        await active.Context.BeginRemoteAsync("candidate-1", "http://comfy.test:8188");
        await active.Context.AcceptRemoteAsync("candidate-1", "owned-prompt");
        await queue.CancelAsync(a.Id, _ct);
        var cancelled = await State(a.Id, j => j.State == AiJobState.Cancelled);
        Assert.False(cancelled.LocksTarget); Assert.True(cancelled.RemoteUnconfirmed); Assert.Equal(AiJobRecovery.CheckStatus, cancelled.Recovery);
        var newComposer = await queue.EnqueueAsync(Request(kind: kind, target: target), _ct);
        Assert.Equal(AiJobState.Waiting, newComposer.State);
        var router = await queue.EnqueueAsync(Request(AiBackend.OpenRouter), _ct); var remote = await handler.Next(_ct);
        Assert.Equal(router.Id, remote.Context.Job.Id);
        Assert.Equal(AiJobState.Waiting, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == waiting.Id).State);
        handler.Cancel = _ => Task.FromResult(true);
        await queue.ResumeAsync(a.Id, _ct); await State(a.Id, j => !j.RemoteUnconfirmed);
        Assert.Equal(waiting.Id, (await handler.Next(_ct)).Context.Job.Id);
        Assert.All((await Store.ReadArtifactAsync<AiJobExecution>(a.Id, AiJobArtifact.Execution, _ct))!.Submissions, s => Assert.Equal(AiRemoteState.Cancelled, s.State));
        Assert.All(handler.Cancelled, id => Assert.Equal(a.Id, id));
    }
    [Fact]
    public async Task DelayedCancellationAutomaticallyReleasesNextVideoWithoutAnotherGeneration()
    {
        var clock = new CancellationClock(); var stopped = false;
        var handler = new FakeHandler { Cancel = _ => Task.FromResult(Volatile.Read(ref stopped)) };
        var a = await Store.EnqueueAsync(Request(kind: AiJobKind.Video, target: new(Guid.NewGuid(), ShotId: Guid.NewGuid())), _ct);
        var waiting = await Store.EnqueueAsync(Request(kind: AiJobKind.Video, target: new(Guid.NewGuid(), ShotId: Guid.NewGuid())), _ct);
        var queue = await Start(handler, clock); var active = await handler.Next(_ct);
        await active.Context.BeginRemoteAsync("candidate-1", "http://comfy.test:8188");
        await active.Context.AcceptRemoteAsync("candidate-1", "owned-prompt");
        await queue.CancelAsync(a.Id, _ct);
        var cancelled = await State(a.Id, j => j.State == AiJobState.Cancelled && j.RemoteUnconfirmed);
        Assert.False(cancelled.LocksTarget); Assert.Single(handler.Calls);
        Volatile.Write(ref stopped, true);
        clock.Set(cancelled.FinishedUtc!.Value.AddSeconds(4));
        await queue.SetPausedAsync(AiBackend.OpenRouter, true, _ct); // Wake the scheduler.
        Assert.Equal(AiJobState.Waiting, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == waiting.Id).State);
        clock.Set(cancelled.FinishedUtc.Value.AddSeconds(6));
        await queue.SetPausedAsync(AiBackend.OpenRouter, false, _ct);
        var next = await handler.Next(_ct); Assert.Equal(waiting.Id, next.Context.Job.Id);
        var confirmed = (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == a.Id);
        Assert.Equal(AiJobState.Cancelled, confirmed.State); Assert.False(confirmed.RemoteUnconfirmed);
        Assert.Null(confirmed.Error); Assert.Equal(AiJobRecovery.None, confirmed.Recovery);
        Assert.Equal(2, handler.Calls.Count); Assert.All(handler.Cancelled, id => Assert.Equal(a.Id, id));
        Assert.All((await Store.ReadArtifactAsync<AiJobExecution>(a.Id, AiJobArtifact.Execution, _ct))!.Submissions,
            s => Assert.Equal(AiRemoteState.Cancelled, s.State));
        await next.Complete();
    }
    private sealed class CancellationClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
        public void Set(DateTimeOffset time) => Interlocked.Exchange(ref _ticks, time.UtcTicks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    }
    [Fact]
    public async Task WaitingCancellationMakesNoProviderCallAndAcknowledgedEnqueueCannotRepeatIt()
    {
        await Store.SetPausedAsync(AiBackend.ComfyUI, true, _ct);
        var handler = new FakeHandler(); var queue = await Start(handler); var request = Request();
        var a = await queue.EnqueueAsync(request, _ct); await queue.CancelAsync(a.Id, _ct);
        Assert.Equal(AiJobState.Cancelled, (await queue.EnqueueAsync(request, _ct)).State);
        await queue.SetPausedAsync(AiBackend.ComfyUI, false, _ct);
        Assert.Empty(handler.Calls); Assert.Empty(handler.Cancelled); Assert.False(queue.View.Jobs.Single().LocksTarget);
    }
    [Fact]
    public async Task RestartRetainsPaidPartialResponseAndOnlyObservesAcceptedComfyWork()
    {
        var comfy = await Store.EnqueueAsync(Request(), _ct); var router = await Store.EnqueueAsync(Request(AiBackend.OpenRouter), _ct);
        var firstHandler = new FakeHandler(); var firstQueue = await Start(firstHandler);
        var starts = new[] { await firstHandler.Next(_ct), await firstHandler.Next(_ct) };
        var local = starts.Single(s => s.Context.Job.Id == comfy.Id); var paid = starts.Single(s => s.Context.Job.Id == router.Id);
        await local.Context.BeginRemoteAsync("text", "http://comfy.test:8188"); await local.Context.AcceptRemoteAsync("text", "saved-id");
        await paid.Context.SaveResultAsync(new { raw = "Partial response", complete = false });
        await firstQueue.StopAsync(_ct); Assert.Empty(firstHandler.Cancelled);
        var nextHandler = new FakeHandler(); await Start(nextHandler);
        var recovery = await nextHandler.Next(_ct); Assert.Equal(comfy.Id, recovery.Context.Job.Id); Assert.True(recovery.Context.Recovering);
        Assert.Equal("saved-id", (await recovery.Context.ExecutionAsync(_ct)).Submissions.Single().PromptId);
        await Assert.ThrowsAsync<AiGenerationException>(() => recovery.Context.BeginRemoteAsync("another", "http://comfy.test:8188"));
        var interrupted = await State(router.Id, j => j.State == AiJobState.NeedsAttention);
        Assert.Equal(AiJobRecovery.GenerateAgain, interrupted.Recovery); Assert.True(interrupted.Unread); Assert.False(interrupted.HoldsProvider);
        Assert.Contains("Partial response", (await Store.ReadArtifactAsync<JsonElement>(router.Id, AiJobArtifact.Result, _ct)).ToString());
        await recovery.Context.FinishRemoteAsync("text"); await recovery.Complete();
        await State(comfy.Id, j => j.State == AiJobState.Completed); Assert.Single(nextHandler.Calls);
    }
    [Fact]
    public async Task UncertainSubmissionNeverAutomaticallyGeneratesAgain()
    {
        var a = await Store.EnqueueAsync(Request(), _ct); var handler = new FakeHandler(); var queue = await Start(handler);
        var active = await handler.Next(_ct); await active.Context.BeginRemoteAsync("text", "http://comfy.test:8188");
        active.Done.SetException(new HttpRequestException("Connection dropped during acceptance"));
        var failed = await State(a.Id, j => j.State == AiJobState.NeedsAttention);
        Assert.True(failed.RemoteUnconfirmed); Assert.True(failed.LocksTarget);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => queue.EnqueueAsync(Request(target: a.Target), _ct));
        await queue.StopAsync(_ct);
        var afterRestart = new FakeHandler(); var restarted = await Start(afterRestart);
        var observed = await afterRestart.Next(_ct); Assert.True(observed.Context.Recovering);
        Assert.Null((await observed.Context.ExecutionAsync(_ct)).Submissions.Single().PromptId);
        observed.Done.SetResult(AiJobOutcome.Attention("Submission outcome unknown", AiJobRecovery.CheckStatus));
        await State(a.Id, j => j.State == AiJobState.NeedsAttention); Assert.Single(afterRestart.Calls);
        await restarted.CancelAsync(a.Id, _ct);
    }
    [Fact]
    public async Task LateHandlerCompletionCannotReplaceCancelledResponseOrSubmitAnotherCandidate()
    {
        var a = await Store.EnqueueAsync(Request(AiBackend.OpenRouter), _ct);
        var handler = new FakeHandler { IgnoreCancellation = true }; var queue = await Start(handler);
        var running = await handler.Next(_ct); await running.Context.SaveResultAsync(new { raw = "Saved partial" });
        await queue.CancelAsync(a.Id, _ct); await State(a.Id, j => j.State == AiJobState.Cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.Context.SaveResultAsync(new { raw = "late overwrite" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.Context.ReportAsync(new(new(GenerationPhase.Completed, "Late"))));
        running.Done.SetResult(AiJobOutcome.Complete());
        var b = await queue.EnqueueAsync(Request(AiBackend.OpenRouter), _ct); Assert.Equal(b.Id, (await handler.Next(_ct)).Context.Job.Id);
        Assert.Contains("Saved partial", (await Store.ReadArtifactAsync<JsonElement>(a.Id, AiJobArtifact.Result, _ct)).ToString());
        Assert.Equal(AiJobState.Cancelled, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == a.Id).State);
    }
    [Fact]
    public async Task FailedFinalPublicationRetriesWithoutAnotherInference()
    {
        var a = await Store.EnqueueAsync(Request(), _ct); var handler = new FakeHandler(); var queue = await Start(handler);
        var active = await handler.Next(_ct); await active.Context.SaveResultAsync(new { text = "Durable completed result" });
        using (var locked = new FileStream(Index, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            active.Done.SetResult(AiJobOutcome.Complete());
            await Eventually(() => Task.FromResult(queue.Error?.Contains("status to be saved", StringComparison.Ordinal) == true));
            Assert.Equal(AiJobState.Running, (await Store.ReadAsync(_ct)).Jobs.Single().State);
        }
        await State(a.Id, j => j.State == AiJobState.Completed);
        Assert.Single(handler.Calls); Assert.Contains("Durable completed result", (await Store.ReadArtifactAsync<JsonElement>(a.Id, AiJobArtifact.Result, _ct)).ToString());
    }
    [Fact]
    public async Task FailedCancellationPublicationKeepsReceiptAndRetriesBeforeDispatchingTheNextVideo()
    {
        var target = new AiJobTarget(Guid.NewGuid(), ShotId: Guid.NewGuid());
        var a = await Store.EnqueueAsync(Request(kind: AiJobKind.Video, target: target), _ct);
        var remote = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler { Cancel = _ => { cancelling.TrySetResult(); return remote.Task; } };
        var queue = await Start(handler); var active = await handler.Next(_ct);
        await active.Context.BeginRemoteAsync("video", "http://comfy.test:8188"); await active.Context.AcceptRemoteAsync("video", "owned-video");
        await queue.CancelAsync(a.Id, _ct); await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        using (var locked = new FileStream(Index, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            remote.SetResult(true);
            await Eventually(() => Task.FromResult(queue.Error?.Contains("status to be saved", StringComparison.Ordinal) == true));
            var saved = (await Store.ReadAsync(_ct)).Jobs.Single(); Assert.True(saved.CancelRequested); Assert.False(saved.LocksTarget);
            Assert.Equal("owned-video", (await Store.ReadArtifactAsync<AiJobExecution>(a.Id, AiJobArtifact.Execution, _ct))!.Submissions.Single().PromptId);
        }
        await State(a.Id, j => j.State == AiJobState.Cancelled);
        var next = await queue.EnqueueAsync(Request(kind: AiJobKind.Video, target: target), _ct);
        var invocation = await handler.Next(_ct); Assert.Equal(next.Id, invocation.Context.Job.Id); Assert.False(invocation.Context.Recovering);
        await invocation.Complete(); await State(next.Id, j => j.State == AiJobState.Completed);
        Assert.Equal(2, handler.Calls.Count); Assert.False(queue.ExecuteTask!.IsCompleted);
    }
    [Fact]
    public async Task RetryOutputQueuesRecoveryBehindWaitingWorkWithoutSubmittingAgain()
    {
        var a = await Store.EnqueueAsync(Request(), _ct); var handler = new FakeHandler(); var queue = await Start(handler);
        var first = await handler.Next(_ct); await first.Context.BeginRemoteAsync("image", "http://comfy.test:8188");
        await first.Context.AcceptRemoteAsync("image", "downloadable-result"); await first.Context.FinishRemoteAsync("image");
        var b = await queue.EnqueueAsync(Request(), _ct);
        first.Done.SetResult(AiJobOutcome.Attention("Download failed", AiJobRecovery.RetryOutput));
        await State(a.Id, j => j.State == AiJobState.NeedsAttention); var second = await handler.Next(_ct);
        await queue.ResumeAsync(a.Id, _ct); Assert.Equal(AiJobState.Waiting, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == a.Id).State);
        Assert.Equal(b.Id, second.Context.Job.Id); await second.Complete();
        var retry = await handler.Next(_ct); Assert.Equal(a.Id, retry.Context.Job.Id); Assert.True(retry.Context.Recovering);
        await Assert.ThrowsAsync<AiGenerationException>(() => retry.Context.BeginRemoteAsync("retry", "http://comfy.test:8188"));
        await retry.Complete(); await State(a.Id, j => j.State == AiJobState.Completed);
    }
    [Fact]
    public async Task ResumeOfferedWhileTheFinishedWorkerUnwindsIsNotDropped()
    {
        var a = await Store.EnqueueAsync(Request(), _ct);
        var proxy = System.Reflection.DispatchProxy.Create<IAiJobStore, UnwindingWorkerStore>();
        var store = (UnwindingWorkerStore)proxy; store.Inner = Store; store.JobId = a.Id;
        var handler = new FakeHandler(); var queue = new AiJobCoordinator(proxy, _settings, [handler], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        _coordinators.Add(queue); await queue.StartAsync(_ct);
        var first = await handler.Next(_ct); first.Done.SetResult(AiJobOutcome.Attention("Result could not be saved", AiJobRecovery.RetryOutput));
        // The outcome is published, so the UI already offers Retry, but the worker is still registered.
        await store.Held.Task.WaitAsync(TimeSpan.FromSeconds(8), _ct);
        Assert.Equal(AiJobRecovery.RetryOutput, (await Store.ReadAsync(_ct)).Jobs.Single().Recovery);
        var resume = queue.ResumeAsync(a.Id, _ct); store.Release.SetResult(); await resume;
        var retry = await handler.Next(_ct); Assert.Equal(a.Id, retry.Context.Job.Id); Assert.True(retry.Context.Recovering);
        await retry.Complete(); await State(a.Id, j => j.State == AiJobState.Completed);
    }
    // Holds the worker's own refresh after it published NeedsAttention.
    public class UnwindingWorkerStore : System.Reflection.DispatchProxy
    {
        public IAiJobStore Inner { get; set; } = null!;
        public Guid JobId { get; set; }
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(IAiJobStore.ReadAsync) && FakeHandler.Worker.Value == JobId ? Hold((CancellationToken)args![0]!) : method.Invoke(Inner, args);
        private async Task<AiQueueDocument> Hold(CancellationToken ct)
        {
            var value = await Inner.ReadAsync(ct);
            if (value.Jobs.Single(j => j.Id == JobId).State == AiJobState.NeedsAttention && Interlocked.Exchange(ref _held, 1) == 0)
            { Held.SetResult(); await Release.Task.WaitAsync(ct); }
            return value;
        }
    }
    [Fact]
    public async Task CancelledBatchOutcomeCheckpointsCompletedCandidatesWithoutRecovery()
    {
        var request = Request(kind: AiJobKind.ImageCreate, target: new(Guid.NewGuid(), Guid.NewGuid()));
        request = request with { Batch = AiBatchDefinition.Create(request.Id, 1) };
        var root = await Store.EnqueueAsync(request, _ct);
        var handler = new FakeHandler(); var queue = await Start(handler);
        var active = await handler.Next(_ct);
        active.Done.SetResult(AiJobOutcome.CancelledCheckpoint(1));
        var cancelled = await State(root.Id, j => j.State == AiJobState.Cancelled);
        Assert.Equal(AiJobRecovery.None, cancelled.Recovery);
        Assert.False(cancelled.LocksTarget);
        Assert.True(cancelled.Unread);
    }
    [Fact]
    public async Task PreflightFailureAndSettingsReadFailureDoNotStopHealthyComfyJobs()
    {
        _settings.LoadError = new WorkspaceStoreException("Protected settings unavailable");
        var a = await Store.EnqueueAsync(Request(), _ct); var b = await Store.EnqueueAsync(Request(), _ct);
        var router = await Store.EnqueueAsync(Request(AiBackend.OpenRouter), _ct);
        var handler = new FakeHandler(); await Start(handler); var first = await handler.Next(_ct);
        first.Done.SetException(new AiGenerationException("Selected model is missing"));
        await State(a.Id, j => j.State == AiJobState.NeedsAttention);
        Assert.Equal(b.Id, (await handler.Next(_ct)).Context.Job.Id);
        Assert.Equal(AiJobState.Waiting, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == router.Id).State);
        _settings.LoadError = null; Assert.Equal(router.Id, (await handler.Next(_ct)).Context.Job.Id);
    }
    private async Task Eventually(Func<Task<bool>> test)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!await test()) await Task.Delay(20, timeout.Token);
    }
    [Fact]
    public async Task ADelayedQueueReadCannotRestoreAnOlderExecutionLock()
    {
        var job = await Store.EnqueueAsync(Request(), _ct);
        var proxy = System.Reflection.DispatchProxy.Create<IAiJobStore, DelayedReadStore>();
        var delay = (DelayedReadStore)proxy; delay.Inner = Store;
        using var coordinator = new AiJobCoordinator(proxy, _settings, [], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        var oldRead = coordinator.RefreshAsync(_ct); await delay.Captured.Task.WaitAsync(_ct);
        await Store.UpdateAsync(job.Id, j => j with { State = AiJobState.Cancelled, CancelRequested = true }, _ct);
        await coordinator.RefreshAsync(_ct);
        var revision = coordinator.View.Revision; Assert.False(coordinator.View.Jobs[0].LocksTarget);
        delay.Release.SetResult(); await oldRead;
        Assert.Equal(revision, coordinator.View.Revision); Assert.False(coordinator.View.Jobs[0].LocksTarget);
    }
    public class DelayedReadStore : System.Reflection.DispatchProxy
    {
        public IAiJobStore Inner { get; set; } = null!;
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IAiJobStore.ReadAsync) && Interlocked.Increment(ref _reads) == 1) return Slow((CancellationToken)args![0]!);
            return method.Invoke(Inner, args);
        }
        private async Task<AiQueueDocument> Slow(CancellationToken ct)
        { var value = await Inner.ReadAsync(ct); Captured.SetResult(); await Release.Task.WaitAsync(ct); return value; }
    }
    private sealed record Invocation(AiJobContext Context, JsonElement Snapshot, TaskCompletionSource<AiJobOutcome> Done)
    {
        public async Task Complete() { await Context.SaveResultAsync(new { text = "Complete suggestion", valid = true }); Done.TrySetResult(AiJobOutcome.Complete()); }
    }
    private sealed class FakeHandler : IAiBatchJobHandler
    {
        public IReadOnlyCollection<AiJobKind> Kinds => Enum.GetValues<AiJobKind>();
        public readonly ConcurrentQueue<Invocation> Calls = new();
        public readonly ConcurrentQueue<Guid> Cancelled = new();
        private readonly Channel<Invocation> _started = Channel.CreateUnbounded<Invocation>();
        public bool IgnoreCancellation;
        public Func<AiJobHeader, Task> ValidateExtension = _ => Task.CompletedTask;
        public Task ValidateExtensionAsync(AiJobHeader root, JsonElement snapshot, CancellationToken ct) => ValidateExtension(root);
        public Func<AiJobContext, Task<bool>> Cancel = _ => Task.FromResult(false);
        public async Task<Invocation> Next(CancellationToken ct) => await _started.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(8), ct);
        public Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Invoke(context, snapshot, ct);
        public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => context.Job.Backend == AiBackend.OpenRouter
            ? Task.FromResult(AiJobOutcome.Attention("Interrupted paid response. Retry explicitly.", AiJobRecovery.GenerateAgain)) : Invoke(context, snapshot, ct);
        // Set synchronously, so it flows through the rest of the calling worker.
        public static readonly AsyncLocal<Guid?> Worker = new();
        private Task<AiJobOutcome> Invoke(AiJobContext context, JsonElement snapshot, CancellationToken ct)
        {
            Worker.Value = context.Job.Id;
            var invocation = new Invocation(context, snapshot, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Calls.Enqueue(invocation); _started.Writer.TryWrite(invocation);
            return IgnoreCancellation ? invocation.Done.Task : invocation.Done.Task.WaitAsync(ct);
        }
        public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
        { Cancelled.Enqueue(context.Job.Id); return Cancel(context); }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var coordinator in _coordinators) { await coordinator.StopAsync(CancellationToken.None); coordinator.Dispose(); }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
