using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed class ComfyQueueControlTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task PauseRetainsTheExactRequestAndBlocksDispatchAtomically()
    {
        using var f = await Fixture.Create();
        var before = (await f.Store.ReadSnapshotAsync(f.Id, Ct)).GetRawText();
        await f.Store.SetPausedAsync(AiBackend.ComfyUI, true, Ct);
        var job = await f.Job();
        Assert.True(job.ComfyControl!.PauseRequested);
        Assert.False(job.CancelRequested);
        Assert.True(job.LocksTarget);
        Assert.Null(await f.Store.ClaimNextAsync(AiBackend.ComfyUI, 1, Ct));
        Assert.Equal(before, (await f.Store.ReadSnapshotAsync(f.Id, Ct)).GetRawText());
        var retryId = job.ComfyControl.RetryId;
        await f.Store.SetPausedAsync(AiBackend.ComfyUI, true, Ct);
        Assert.Equal(retryId, (await f.Job()).ComfyControl!.RetryId);
    }

    [Theory]
    [InlineData(AiJobState.Waiting)]
    [InlineData(AiJobState.Completed)]
    [InlineData(AiJobState.Cancelled)]
    [InlineData(AiJobState.NeedsAttention)]
    public async Task PauseDoesNotRewriteUntouchedOrTerminalRequests(AiJobState state)
    {
        using var f = await Fixture.Create();
        await f.Store.UpdateAsync(f.Id, j => j with { State = state }, Ct);
        await f.Store.SetPausedAsync(AiBackend.ComfyUI, true, Ct);
        Assert.Null((await f.Job()).ComfyControl);
        Assert.Equal(state, (await f.Job()).State);
    }

    [Fact]
    public async Task UnpauseDefersClaimUntilTheOwnedStopIsConfirmed()
    {
        using var f = await Fixture.Create();
        await f.Store.SetPausedAsync(AiBackend.ComfyUI, true, Ct);
        await f.Store.UpdateAsync(f.Id, j => ComfyQueuePolicy.Suspended(j, true, true, DateTimeOffset.UtcNow), Ct);
        await f.Store.SetPausedAsync(AiBackend.ComfyUI, false, Ct);
        Assert.Null(await f.Store.ClaimNextAsync(AiBackend.ComfyUI, 1, Ct));
        await f.Store.UpdateAsync(f.Id, j => ComfyQueuePolicy.Suspended(j, false, true, DateTimeOffset.UtcNow), Ct);
        var retained = await f.Job();
        Assert.True(retained.ComfyControl!.PauseRequested);
        var resumed = (await f.Store.ClaimNextAsync(AiBackend.ComfyUI, 1, Ct))!;
        Assert.False(resumed.ComfyControl!.PauseRequested);
        Assert.Equal(retained.ComfyControl.RetryId, resumed.ComfyControl.RetryId);
        Assert.NotEqual(retained.LeaseId, resumed.LeaseId);
        Assert.Equal(AiJobRecovery.CheckStatus, resumed.Recovery);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task AutomaticReconnectHonorsPauseAndItsBackoff(bool paused, bool stopping, bool expected)
    {
        using var f = await Fixture.Create();
        var now = DateTimeOffset.UtcNow;
        var job = (await f.Job()) with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.CheckStatus,
            FinishedUtc = now.AddSeconds(-6), ComfyControl = new(Guid.NewGuid(), stopping) };
        AiBackend[] providers = paused ? [AiBackend.ComfyUI] : [];
        Assert.Equal(expected, ComfyQueuePolicy.ShouldReconcile(job, providers, now));
        Assert.False(ComfyQueuePolicy.ShouldReconcile(job with { FinishedUtc = now }, providers, now));
        Assert.False(ComfyQueuePolicy.ShouldReconcile(job with { CancelRequested = true }, providers, now));
        Assert.False(ComfyQueuePolicy.ShouldReconcile(job with { Recovery = AiJobRecovery.GenerateAgain }, providers, now));
    }

    [Fact]
    public async Task RetryPermissionPreventsDuplicateActiveTargetsAndCanBeCancelled()
    {
        using var f = await Fixture.Create();
        await f.Store.UpdateAsync(f.Id, j => j with { State = AiJobState.NeedsAttention,
            Recovery = AiJobRecovery.CheckStatus, ComfyControl = new(Guid.NewGuid()) }, Ct);
        Assert.True((await f.Job()).LocksTarget);
        Assert.False((await f.Job()).CanClearActivity);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Store.EnqueueAsync(f.Submission(Guid.NewGuid()), Ct));
        using var coordinator = f.Coordinator([]);
        await coordinator.CancelAsync(f.Id, Ct);
        Assert.Null((await f.Job()).ComfyControl);
        Assert.True((await f.Job()).CancelRequested);
        await f.Store.EnqueueAsync(f.Submission(Guid.NewGuid()), Ct);
    }

    [Fact]
    public async Task RetryCommandRejectsStaleConfirmationAndNeverRevivesCancellation()
    {
        using var f = await Fixture.Create();
        var old = await f.Job();
        await f.Store.UpdateAsync(f.Id, j => j with { State = AiJobState.NeedsAttention, RemoteUnconfirmed = true, Recovery = AiJobRecovery.CheckStatus }, Ct);
        using var coordinator = f.Coordinator([]);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => coordinator.RetryComfyAsync(f.Id, old.Version, Ct));
        var cancelled = await f.Store.UpdateAsync(f.Id, j => j with { State = AiJobState.Cancelled, CancelRequested = true }, Ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => coordinator.RetryComfyAsync(f.Id, cancelled.Version, Ct));
    }

    [Fact]
    public async Task RetryCommandSurvivesRestartWithoutChangingTheCapturedSnapshot()
    {
        using var f = await Fixture.Create();
        var before = (await f.Store.ReadSnapshotAsync(f.Id, Ct)).GetRawText();
        var job = await f.Store.UpdateAsync(f.Id, j => j with { State = AiJobState.NeedsAttention, RemoteUnconfirmed = true, Recovery = AiJobRecovery.CheckStatus }, Ct);
        using var coordinator = f.Coordinator([]);
        await coordinator.RetryComfyAsync(f.Id, job.Version, Ct);
        var reopened = new FileAiJobStore(f.Root, TimeProvider.System);
        Assert.NotNull((await reopened.ReadAsync(Ct)).Jobs.Single().ComfyControl);
        Assert.Equal(before, (await reopened.ReadSnapshotAsync(f.Id, Ct)).GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassiveRecoveryNeverResubmitsMissingWork(bool acknowledged)
    {
        using var f = await Fixture.Create();
        await f.Prepare(acknowledged);
        await Assert.ThrowsAsync<AiGenerationException>(() => f.Observe());
        Assert.Empty(f.Server.PostedGraphs);
    }

    [Fact]
    public async Task MissingAcceptedWorkReplaysOnceWithAnImmutableAttemptHistory()
    {
        using var f = await Fixture.Create();
        var original = await f.Prepare(true);
        await f.Authorize();
        var context = await f.Context(true);
        var result = await Drain(f.Transport.ObserveAsync(context, "text", f.Http, ct: Ct));
        Assert.Single(result, r => r.Complete);
        Assert.Single(f.Server.PostedGraphs);
        var latest = (await context.ExecutionAsync(Ct)).Submissions.Single();
        Assert.NotNull(latest.ArtifactOperation);
        Assert.Equal((await f.Job()).ComfyControl!.RetryId, latest.RetryId);
        Assert.NotEqual(original.ClientId, latest.ClientId);
        var oldRequest = await f.Store.ReadOperationAsync<ComfySavedOperation>(f.Id, "text", AiOperationArtifact.Request, Ct);
        Assert.Equal(original.ClientId, oldRequest!.ClientId);
        Assert.Equal(original.ClientId, (await f.Store.ReadOperationAsync<AiRemoteSubmission>(f.Id, "text", AiOperationArtifact.RetryReceipt, Ct))!.ClientId);
        Assert.Equal(latest.ClientId, (await context.ReadOperationAsync<ComfySavedOperation>("text", AiOperationArtifact.Request, Ct))!.ClientId);
        Assert.NotNull(await context.ReadOperationAsync<JsonElement?>("text", AiOperationArtifact.Output, Ct));
        Assert.True(JsonElement.DeepEquals(original.Workflow.GetProperty("prompt"), f.Server.PostedGraphs.Single().GetProperty("prompt")));
        Assert.Equal("captured", (await f.Store.ReadSnapshotAsync(f.Id, Ct)).GetProperty("prompt").GetString());
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("bad-queue")]
    [InlineData("bad-history")]
    public async Task UnreadableOrOfflineServerDoesNotReleaseOrRepeatTheOldAttempt(string mode)
    {
        using var f = await Fixture.Create();
        await f.Prepare(true); await f.Authorize(); f.Server.Mode = mode;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Observe());
        Assert.Empty(f.Server.PostedGraphs);
        Assert.True((await (await f.Context(true)).ExecutionAsync(Ct)).MayBeRunning);
    }

    [Fact]
    public async Task CancellationDuringAStatusProbeNeverSubmitsAWorkflow()
    {
        using var f = await Fixture.Create();
        await f.Prepare(true); await f.Authorize(); f.Server.Mode = "wait";
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelled.CancelAfter(TimeSpan.FromMilliseconds(50));
        var context = await f.Context(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(
            f.Transport.ObserveAsync(context, "text", f.Http, ct: cancelled.Token)));
        Assert.Empty(f.Server.PostedGraphs);
        Assert.True((await context.ExecutionAsync(Ct)).MayBeRunning);
    }

    [Fact]
    public async Task FailedHistoryIsArchivedBeforeReplayingTheCapturedWorkflow()
    {
        using var f = await Fixture.Create();
        var saved = await f.Prepare(true); await f.Authorize();
        f.Server.History[f.OldPrompt] = new { prompt = f.Server.Row(f.OldPrompt, saved),
            outputs = new { }, status = new { completed = false, status_str = "error" } };
        await f.Observe();
        Assert.Single(f.Server.PostedGraphs);
        var original = await f.Store.ReadOperationAsync<JsonElement>(f.Id, "text", AiOperationArtifact.Output, Ct);
        Assert.Equal("error", original.GetProperty("status").GetProperty("status_str").GetString());
        var latest = await (await f.Context(true)).ReadOperationAsync<JsonElement>("text", AiOperationArtifact.Output, Ct);
        Assert.Equal("success", latest.GetProperty("status").GetProperty("status_str").GetString());
    }

    [Fact]
    public async Task RunningOwnedWorkIsObservedRatherThanDuplicated()
    {
        using var f = await Fixture.Create();
        var saved = await f.Prepare(true); await f.Authorize();
        f.Server.Running.Add(f.Server.Row(f.OldPrompt, saved));
        await f.Observe();
        Assert.Empty(f.Server.PostedGraphs);
        Assert.Equal(1, f.Monitor.Observations);
    }

    [Fact]
    public async Task ExistingSuccessfulHistoryIsRetrievedWithoutNewInference()
    {
        using var f = await Fixture.Create();
        var saved = await f.Prepare(true); await f.Authorize();
        f.Server.History[f.OldPrompt] = f.Server.Result(f.OldPrompt, saved);
        await f.Observe();
        Assert.Empty(f.Server.PostedGraphs);
        Assert.Equal(0, f.Monitor.Observations);
    }

    [Fact]
    public async Task LostAcknowledgementSearchesAllHistoryNotJustTheRecent256Entries()
    {
        using var f = await Fixture.Create();
        var saved = await f.Prepare(false); await f.Authorize();
        for (var i = 0; i < 300; i++) f.Server.History[Guid.NewGuid().ToString("D")] = new { outputs = new { } };
        f.Server.History[f.OldPrompt] = f.Server.Result(f.OldPrompt, saved);
        await f.Observe();
        Assert.Empty(f.Server.PostedGraphs);
        Assert.Equal(f.OldPrompt, (await (await f.Context(true)).ExecutionAsync(Ct)).Submissions.Single().PromptId);
        Assert.Contains("history", f.Server.Paths);
        Assert.DoesNotContain(f.Server.Paths, p => p.Contains("max_items"));
    }

    [Fact]
    public async Task AmbiguousReceiptMatchesFailClosed()
    {
        using var f = await Fixture.Create();
        var saved = await f.Prepare(false); await f.Authorize();
        f.Server.Pending.Add(f.Server.Row(f.OldPrompt, saved));
        var other = Guid.NewGuid().ToString("D");
        f.Server.History[other] = f.Server.Result(other, saved);
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => f.Observe());
        Assert.Contains("More than one", error.Message);
        Assert.Empty(f.Server.PostedGraphs);
    }

    [Fact]
    public async Task ARetryWithLostAcceptanceCannotReplayAgainUnderTheSameAuthorization()
    {
        using var f = await Fixture.Create();
        await f.Prepare(true); await f.Authorize(); f.Server.LoseNextAcceptance = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Observe());
        Assert.Single(f.Server.PostedGraphs);
        await Assert.ThrowsAsync<AiGenerationException>(() => f.Observe());
        Assert.Single(f.Server.PostedGraphs);
        await f.Authorize();
        await f.Observe();
        Assert.Equal(2, f.Server.PostedGraphs.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopTargetsOnlyTheOwnedJobAndNeverUsesGlobalInterrupt(bool supported)
    {
        using var f = await Fixture.Create();
        var saved = await f.Prepare(true); f.Server.CancelSupported = supported;
        var other = Guid.NewGuid().ToString("D");
        f.Server.Running.Add(f.Server.Row(f.OldPrompt, saved));
        f.Server.Running.Add(f.Server.Row(other, saved with { ClientId = Guid.NewGuid().ToString("D") }));
        var stopped = await f.Transport.CancelAsync(await f.Context(true), _ => f.Client(), Ct);
        Assert.Equal(supported, stopped);
        Assert.Contains(f.Server.Running, row => (string)row[1] == other);
        Assert.DoesNotContain(f.Server.Paths, p => p.Contains("interrupt"));
        Assert.Contains("api/jobs/" + f.OldPrompt + "/cancel", f.Server.Paths);
        Assert.Empty(f.Server.PostedGraphs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyExplicitPauseCanRetireARecentUnacknowledgedMissingLegacyRequest(bool pause)
    {
        using var f = await Fixture.Create(); await f.Prepare(false);
        if (pause) await f.Store.SetPausedAsync(AiBackend.ComfyUI, true, Ct);
        var stopped = await f.Transport.CancelAsync(await f.Context(true), _ => f.Client(), Ct);
        Assert.Equal(pause, stopped);
        Assert.Equal(!pause, (await (await f.Context(true)).ExecutionAsync(Ct)).MayBeRunning);
        Assert.Empty(f.Server.PostedGraphs);
    }

    [Fact]
    public async Task CancellingAnOldUnacknowledgedRequestThatComfyHasNoRecordOfReleasesIt()
    {
        using var f = await Fixture.Create(); await f.Prepare(false);
        var later = new AiJobContext(await f.Job(), true, f.Store, new ManualClock(DateTimeOffset.UtcNow + ComfyQueuePolicy.LostRemoteGrace + TimeSpan.FromMinutes(1)), (_, _) => { }, _ => { }, Ct);
        Assert.True(await f.Transport.CancelAsync(later, _ => f.Client(), Ct));
        Assert.False((await later.ExecutionAsync(Ct)).MayBeRunning);
        Assert.Empty(f.Server.PostedGraphs);
    }

    [Fact]
    public async Task RetriedWorkCannotChangeCandidateStagingOrUseTheGeneralRecoverySubmitPath()
    {
        using var f = await Fixture.Create(); await f.Prepare(true); await f.Authorize();
        var original = await f.Context(false);
        await original.SaveOperationAsync("text", AiOperationArtifact.Image, new { candidate = "retained" }, Ct);
        var context = await f.Context(true);
        await Assert.ThrowsAsync<AiGenerationException>(() => context.BeginRemoteAsync("another", "http://comfy.test"));
        await Drain(f.Transport.ObserveAsync(context, "text", f.Http, ct: Ct));
        Assert.Equal("retained", (await context.ReadOperationAsync<JsonElement>("text", AiOperationArtifact.Image, Ct)).GetProperty("candidate").GetString());
    }

    [Fact]
    public async Task AlreadyStagedRetryRequestIsReusedAfterAnInterruptedPointerCommit()
    {
        using var f = await Fixture.Create(); var saved = await f.Prepare(true); await f.Authorize();
        var original = await f.Context(false); await original.FinishRemoteAsync("text", cancelled: true);
        var permission = (await f.Job()).ComfyControl!.RetryId;
        var client = Guid.NewGuid().ToString("D");
        var staged = saved with { ClientId = client, Workflow = JsonSerializer.SerializeToElement(new
        { prompt = saved.Workflow.GetProperty("prompt"), client_id = client }) };
        await original.SaveOperationAsync(ComfyQueuePolicy.AttemptOperation("text", permission), AiOperationArtifact.Request, staged, Ct);
        var context = await f.Context(true);
        var retry = await context.BeginComfyRetryAsync("text", (await context.ExecutionAsync(Ct)).Submissions.Single(), Ct);
        Assert.Equal(client, retry.ClientId);
        Assert.Equal(client, (await context.ExecutionAsync(Ct)).Submissions.Single().ClientId);
    }

    [Fact]
    public async Task PauseCannotReplayUntilUnpauseEvenWithRetryPermission()
    {
        using var f = await Fixture.Create(); await f.Prepare(true); await f.Authorize();
        await f.Store.SetPausedAsync(AiBackend.ComfyUI, true, Ct);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Observe());
        Assert.Empty(f.Server.PostedGraphs);
    }

    [Fact]
    public async Task LegacyJsonOmitsAllNewOptionalControlAndAttemptFields()
    {
        using var f = await Fixture.Create();
        var json = JsonSerializer.Serialize(await f.Job(), AtomicJsonFile.Options);
        Assert.DoesNotContain("comfyControl", json);
        var receipt = JsonSerializer.Serialize(new AiRemoteSubmission("text", "http://comfy.test", Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow), AtomicJsonFile.Options);
        Assert.DoesNotContain("artifactOperation", receipt); Assert.DoesNotContain("retryId", receipt);
    }

    [Fact]
    public async Task ResumeDuringStopWaitsForTheOldObserverAndKeepsTheSameQueueIdentity()
    {
        using var f = await Fixture.Create(claim: false);
        var handler = new PausableHandler(holdUnwind: true);
        using var queue = f.Coordinator([handler]);
        await queue.StartAsync(Ct);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await queue.SetPausedAsync(AiBackend.ComfyUI, true, Ct);
            await handler.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await queue.SetPausedAsync(AiBackend.ComfyUI, false, Ct);
            await Task.Delay(100, Ct);
            Assert.Equal(1, handler.Executions);
            Assert.False((await f.Job()).CancelRequested);
            handler.Unwind.TrySetResult();
            await Until(async () => (await f.Job()).State == AiJobState.Completed);
            Assert.Equal(2, handler.Executions);
            Assert.Equal(1, handler.MaximumConcurrent);
            Assert.Equal(f.Id, (await f.Job()).Id);
            Assert.Null((await f.Job()).ComfyControl);
        }
        finally { handler.Unwind.TrySetResult(); await queue.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CancellingAPausedRequestPreventsItFromStartingOnResume()
    {
        using var f = await Fixture.Create(claim: false);
        var handler = new PausableHandler(holdUnwind: false);
        using var queue = f.Coordinator([handler]); await queue.StartAsync(Ct);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await queue.SetPausedAsync(AiBackend.ComfyUI, true, Ct);
            await Until(async () => (await f.Job()).State == AiJobState.Waiting);
            await queue.CancelAsync(f.Id, Ct);
            await queue.SetPausedAsync(AiBackend.ComfyUI, false, Ct);
            await Task.Delay(100, Ct);
            Assert.Equal(AiJobState.Cancelled, (await f.Job()).State);
            Assert.Null((await f.Job()).ComfyControl);
            Assert.Equal(1, handler.Executions);
        }
        finally { await queue.StopAsync(CancellationToken.None); }
    }

    private static async Task Until(Func<Task<bool>> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!await predicate()) await Task.Delay(20, timeout.Token);
    }
    private static async Task<List<ComfyExecutionUpdate>> Drain(IAsyncEnumerable<ComfyExecutionUpdate> source)
    { var values = new List<ComfyExecutionUpdate>(); await foreach (var value in source.WithCancellation(Ct)) values.Add(value); return values; }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Lumibelle.ComfyControl", Guid.NewGuid().ToString("N"));
        public Guid Id { get; } = Guid.NewGuid();
        public Guid Project { get; } = Guid.NewGuid();
        public string OldPrompt { get; } = Guid.NewGuid().ToString("D");
        public FileAiJobStore Store { get; }
        public Server Server { get; } = new();
        public Monitor Monitor { get; }
        public ComfyJobExecution Transport { get; }
        public HttpClient Http { get; }
        private Fixture() { Store = new(Root, TimeProvider.System); Monitor = new(Server); Transport = new(Monitor); Http = Client(); }
        public static async Task<Fixture> Create(bool claim = true)
        {
            var f = new Fixture(); await f.Store.EnqueueAsync(f.Submission(f.Id), Ct);
            if (claim) await f.Store.ClaimNextAsync(AiBackend.ComfyUI, 1, Ct);
            return f;
        }
        public AiJobSubmission Submission(Guid id) => AiJobSubmission.Create(id, AiJobKind.ScriptAssistant, AiBackend.ComfyUI,
            new(Project), "Test project", "Captured request", Guid.NewGuid(), new { prompt = "captured", seed = 123 });
        public async Task<AiJobHeader> Job() => (await Store.ReadAsync(Ct)).Jobs.Single(j => j.Id == Id);
        public async Task<AiJobContext> Context(bool recovering) => new(await Job(), recovering, Store, TimeProvider.System, (_, _) => { }, _ => { }, Ct);
        public async Task<List<ComfyExecutionUpdate>> Observe() => await Drain(Transport.ObserveAsync(await Context(true), "text", Http, ct: Ct));
        public HttpClient Client() => new(Server, disposeHandler: false) { BaseAddress = new("http://comfy.test/"), Timeout = Timeout.InfiniteTimeSpan };
        public AiJobCoordinator Coordinator(IEnumerable<IAiJobHandler> handlers) => new(Store, new Settings(), handlers, TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        public async Task Authorize() => await Store.UpdateAsync(Id, j => j with { ComfyControl = new(Guid.NewGuid()) }, Ct);
        public async Task<ComfySavedOperation> Prepare(bool acknowledged)
        {
            var context = await Context(false); var client = Guid.NewGuid().ToString("D");
            var workflow = JsonSerializer.SerializeToElement(new { prompt = new Dictionary<string, object>
            { ["1"] = new { class_type = "TestNode", inputs = new { seed = 123, prompt = "captured" } } }, client_id = client });
            var options = new ComfyExecutionOptions(new Dictionary<string, ComfyNodeStage>(), "Rejected", "Failed", "Timed out", "Timed out", "Unreadable", "Disconnected");
            var saved = new ComfySavedOperation("http://comfy.test", client, workflow, options);
            await context.SaveOperationAsync("text", AiOperationArtifact.Request, saved, Ct);
            await context.BeginRemoteAsync("text", "http://comfy.test", client);
            if (acknowledged) await context.AcceptRemoteAsync("text", OldPrompt);
            Server.Original = saved;
            return saved;
        }
        public void Dispose() { Http.Dispose(); Server.Dispose(); try { Directory.Delete(Root, true); } catch (DirectoryNotFoundException) { } }
    }

    private sealed class Settings : IAiSettingsStore
    {
        private AiSettings _value = new();
        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_value);
        public Task<AiSettings> SaveAsync(AiSettings settings, string? replacementKey = null, bool removeKey = false, CancellationToken cancellationToken = default) => Task.FromResult(_value = settings);
        public Task<string?> ReadOpenRouterKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
    private sealed class Server : HttpMessageHandler
    {
        public ComfySavedOperation? Original;
        public List<object[]> Running { get; } = [];
        public List<object[]> Pending { get; } = [];
        public Dictionary<string, object> History { get; } = [];
        public List<JsonElement> PostedGraphs { get; } = [];
        public List<string> Paths { get; } = [];
        public string? Mode;
        public bool LoseNextAcceptance, CancelSupported = true;
        public object[] Row(string id, ComfySavedOperation saved) => [1, id, saved.Workflow.GetProperty("prompt"), new { client_id = saved.ClientId }, Array.Empty<string>()];
        public object Result(string id, ComfySavedOperation saved) => new { prompt = Row(id, saved),
            outputs = new Dictionary<string, object> { ["3"] = new { text = new[] { "Finished" } } }, status = new { completed = true, status_str = "success" } };
        public JsonElement Complete(string id)
        {
            Running.RemoveAll(r => (string)r[1] == id); Pending.RemoveAll(r => (string)r[1] == id);
            var result = Result(id, Original!); History[id] = result; return JsonSerializer.SerializeToElement(result);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/'); Paths.Add(path);
            if (Mode == "offline") throw new HttpRequestException("offline");
            if (Mode == "wait") await Task.Delay(Timeout.Infinite, ct);
            if (request.Method == HttpMethod.Get && path == "queue") return Json(Mode == "bad-queue" ? new { unexpected = true } : (object)new { queue_running = Running, queue_pending = Pending });
            if (request.Method == HttpMethod.Get && path.StartsWith("history"))
            {
                if (Mode == "bad-history") return Json(new[] { "invalid" });
                if (path == "history") return Json(History);
                var id = path["history/".Length..];
                return Json(History.TryGetValue(id, out var result) ? new Dictionary<string, object> { [id] = result } : new Dictionary<string, object>());
            }
            if (request.Method == HttpMethod.Post && path == "prompt")
            {
                var graph = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone(); PostedGraphs.Add(graph);
                if (LoseNextAcceptance) { LoseNextAcceptance = false; throw new HttpRequestException("lost acceptance"); }
                var id = Guid.NewGuid().ToString("D");
                var saved = Original! with { ClientId = graph.GetProperty("client_id").GetString()!, Workflow = graph };
                History[id] = Result(id, saved);
                return Json(new { prompt_id = id });
            }
            if (request.Method == HttpMethod.Post && path.StartsWith("api/jobs/") && path.EndsWith("/cancel"))
            {
                if (!CancelSupported) return new(HttpStatusCode.NotFound);
                var id = path.Substring("api/jobs/".Length, path.Length - "api/jobs/".Length - "/cancel".Length);
                Running.RemoveAll(r => (string)r[1] == id); Pending.RemoveAll(r => (string)r[1] == id);
                return Json(new { cancelled = true });
            }
            if (request.Method == HttpMethod.Post && path == "queue")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                foreach (var id in body.RootElement.GetProperty("delete").EnumerateArray()) Pending.RemoveAll(r => (string)r[1] == id.GetString());
                return Json(new { });
            }
            throw new InvalidOperationException("Unexpected request: " + request.Method + " " + path);
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
    private sealed class Monitor(Server server) : IComfyExecutionMonitor
    {
        public int Observations;
        public async IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(HttpClient http, string promptId, string clientId, ComfyExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Observations++; await Task.Yield();
            yield return new(new(GenerationPhase.Completed, "Done"), promptId, server.Complete(promptId), true);
        }
        public IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(HttpClient http, Func<string, object> workflowFactory, ComfyExecutionOptions options, CancellationToken operationToken, CancellationToken callerToken) => throw new NotSupportedException();
    }
    private sealed class PausableHandler : IAiJobHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Unwind { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Executions, MaximumConcurrent;
        private int _concurrent;
        public PausableHandler(bool holdUnwind) { if (!holdUnwind) Unwind.TrySetResult(); }
        public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.ScriptAssistant];
        public async Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref Executions); var concurrent = Interlocked.Increment(ref _concurrent);
            MaximumConcurrent = Math.Max(MaximumConcurrent, concurrent);
            try
            {
                if (call == 1)
                {
                    Started.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { Stopping.TrySetResult(); await Unwind.Task; throw; }
                }
                return AiJobOutcome.Complete(false);
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
        public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => ExecuteAsync(context, snapshot, ct);
        public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(true);
    }
}
