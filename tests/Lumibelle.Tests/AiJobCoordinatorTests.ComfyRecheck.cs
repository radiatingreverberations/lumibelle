using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

// The coordinator and ComfyUI transport are real. Only ComfyUI's HTTP API and its
// progress socket are fake, so each test follows the path an overnight queue takes.
public sealed partial class AiJobCoordinatorTests
{
    private async Task<AiJobCoordinator> StartComfy(ComfyJobStub handler, CancellationClock clock)
    {
        var coordinator = new AiJobCoordinator(Store, _settings, [handler], clock, NullLogger<AiJobCoordinator>.Instance);
        _coordinators.Add(coordinator); await coordinator.StartAsync(_ct); return coordinator;
    }
    private async Task<AiJobHeader> Job(Guid id) => (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == id);
    private async Task<AiJobHeader> Dropped(Guid id) => await State(id, j => j.State == AiJobState.NeedsAttention && j.RemoteUnconfirmed);
    private static async Task Wake(AiJobCoordinator queue, CancellationToken ct)
    {
        // Pausing an unused provider only wakes the scheduler.
        await queue.SetPausedAsync(AiBackend.OpenRouter, true, ct);
        await queue.SetPausedAsync(AiBackend.OpenRouter, false, ct);
    }
    // Moves past this job's backoff and waits until that one check has published.
    private async Task<AiJobHeader> Recheck(AiJobCoordinator queue, CancellationClock clock, Guid id, int afterSeconds = 60)
    {
        var due = (await Job(id)).FinishedUtc!.Value.AddSeconds(afterSeconds);
        clock.Set(due); await Wake(queue, _ct);
        return await State(id, j => j.State != AiJobState.Running && j.FinishedUtc >= due);
    }

    [Fact]
    public async Task ADroppedConnectionRecoversFinishedWorkFromHistoryAndResumesTheQueue()
    {
        var comfy = new FakeComfy { DropNextObservation = true }; var clock = new CancellationClock(); var handler = new ComfyJobStub(comfy);
        var a = await Store.EnqueueAsync(Request(), _ct); var waiting = await Store.EnqueueAsync(Request(), _ct);
        var queue = await StartComfy(handler, clock);

        var dropped = await Dropped(a.Id);
        Assert.Equal("ComfyUI connection failed.", dropped.Error);
        Assert.Null(dropped.ComfyControl); Assert.Equal(AiJobRecovery.CheckStatus, dropped.Recovery);
        Assert.Equal(AiJobState.Waiting, (await Job(waiting.Id)).State);

        // ComfyUI carries on and finishes while nobody is connected to it.
        comfy.FinishRunning();
        var recovered = await Recheck(queue, clock, a.Id, 5);

        Assert.Equal(AiJobState.Completed, recovered.State); Assert.False(recovered.RemoteUnconfirmed);
        Assert.Null(recovered.Error); Assert.Equal(1, handler.Recoveries);
        await State(waiting.Id, j => j.State == AiJobState.Completed);
        Assert.Equal(2, comfy.Posts); // One per job: nothing was rendered twice.
        Assert.Equal(AiRemoteState.Finished, (await Store.ReadArtifactAsync<AiJobExecution>(a.Id, AiJobArtifact.Execution, _ct))!.Submissions.Single().State);
    }

    [Fact]
    public async Task WorkStillRenderingIsObservedAgainRatherThanSubmittedAgain()
    {
        var comfy = new FakeComfy { DropNextObservation = true }; var clock = new CancellationClock(); var handler = new ComfyJobStub(comfy);
        var a = await Store.EnqueueAsync(Request(), _ct);
        var queue = await StartComfy(handler, clock);
        await Dropped(a.Id);

        var recovered = await Recheck(queue, clock, a.Id, 5);

        Assert.Equal(AiJobState.Completed, recovered.State);
        Assert.Equal(1, comfy.Posts); Assert.Equal(2, comfy.Observations);
    }

    [Fact]
    public async Task RechecksBackOffAndAnUnreachableServerKeepsTheReservationPastTheGrace()
    {
        var comfy = new FakeComfy { DropNextObservation = true, GoOfflineOnDrop = true }; var clock = new CancellationClock(); var handler = new ComfyJobStub(comfy);
        var a = await Store.EnqueueAsync(Request(), _ct); var waiting = await Store.EnqueueAsync(Request(), _ct);
        var queue = await StartComfy(handler, clock);
        await Dropped(a.Id);

        var checks = 0;
        foreach (var delay in new[] { 5, 10, 20, 40, 60, 60 })
        {
            var finished = (await Job(a.Id)).FinishedUtc!.Value;
            clock.Set(finished.AddSeconds(delay - 1)); await Wake(queue, _ct); await Task.Delay(150, _ct);
            Assert.Equal(checks, handler.Recoveries);
            var job = await Recheck(queue, clock, a.Id, delay);
            Assert.Equal(++checks, handler.Recoveries);
            // Nothing can be learned while it is down, so nothing is written off or started.
            Assert.True(job.RemoteUnconfirmed); Assert.Equal(AiJobRecovery.CheckStatus, job.Recovery);
            Assert.Equal(AiJobState.Waiting, (await Job(waiting.Id)).State);
        }
        Assert.Equal(1, comfy.Posts);

        comfy.Offline = false; comfy.FinishRunning();
        var recovered = await Recheck(queue, clock, a.Id);
        Assert.Equal(AiJobState.Completed, recovered.State);
        await State(waiting.Id, j => j.State == AiJobState.Completed);
        Assert.Equal(2, comfy.Posts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATrulyLostJobEndsClearlyAndReleasesTheQueueWithoutRenderingAgain(bool acknowledged)
    {
        var comfy = new FakeComfy { DropNextObservation = acknowledged, LoseNextAcceptance = !acknowledged };
        var clock = new CancellationClock(); var handler = new ComfyJobStub(comfy);
        var a = await Store.EnqueueAsync(Request(), _ct); var waiting = await Store.EnqueueAsync(Request(), _ct);
        var queue = await StartComfy(handler, clock);
        await Dropped(a.Id);
        comfy.Restart();

        // One empty reading is not proof. The reservation holds through the grace period.
        for (var check = 0; check < 2; check++)
        {
            var held = await Recheck(queue, clock, a.Id);
            Assert.True(held.RemoteUnconfirmed); Assert.Equal(AiJobState.Waiting, (await Job(waiting.Id)).State);
        }
        Assert.Equal(1, comfy.Posts);

        var lost = await Recheck(queue, clock, a.Id);

        Assert.Equal(AiJobState.NeedsAttention, lost.State); Assert.False(lost.RemoteUnconfirmed); Assert.False(lost.HoldsProvider);
        Assert.Equal(ComfyQueuePolicy.LostRemoteMessage, lost.Error); Assert.True(lost.Unread); Assert.True(AiActivityPolicy.NeedsAttention(lost));
        Assert.True(ComfyQueuePolicy.CanRetry(lost));
        Assert.Equal(AiRemoteState.Cancelled, (await Store.ReadArtifactAsync<AiJobExecution>(a.Id, AiJobArtifact.Execution, _ct))!.Submissions.Single().State);
        await State(waiting.Id, j => j.State == AiJobState.Completed);
        Assert.Equal(2, comfy.Posts); // Only the next job; the lost one was not repeated.

        // It stays settled: no more automatic checks, and it no longer blocks anything.
        var checks = handler.Recoveries;
        clock.Set(lost.FinishedUtc!.Value.AddMinutes(10)); await Wake(queue, _ct); await Task.Delay(150, _ct);
        Assert.Equal(checks, handler.Recoveries); Assert.Equal(AiJobState.NeedsAttention, (await Job(a.Id)).State);
    }

    [Fact]
    public async Task ALostJobCanBeRetriedAndRunsItsCapturedWorkflowOnce()
    {
        var comfy = new FakeComfy { DropNextObservation = true }; var clock = new CancellationClock();
        var a = await Store.EnqueueAsync(Request(), _ct);
        var queue = await StartComfy(new(comfy), clock);
        await Dropped(a.Id);
        comfy.Restart();
        await Recheck(queue, clock, a.Id); await Recheck(queue, clock, a.Id);
        var lost = await Recheck(queue, clock, a.Id);
        Assert.False(lost.RemoteUnconfirmed);

        await queue.RetryComfyAsync(a.Id, lost.Version, _ct);

        Assert.Equal(AiJobState.Completed, (await State(a.Id, j => j.State == AiJobState.Completed)).State);
        Assert.Equal(2, comfy.Posts);
        Assert.Equal(comfy.PostedGraphs[0].GetRawText(), comfy.PostedGraphs[1].GetRawText());
    }

    [Fact]
    public async Task AnUnreachableCheckRestartsTheGraceBecauseItProvesNothing()
    {
        var comfy = new FakeComfy { DropNextObservation = true }; var clock = new CancellationClock();
        var a = await Store.EnqueueAsync(Request(), _ct);
        var queue = await StartComfy(new(comfy), clock);
        await Dropped(a.Id);
        comfy.Restart();

        await Recheck(queue, clock, a.Id);
        comfy.Offline = true; await Recheck(queue, clock, a.Id);
        comfy.Offline = false;
        // Two minutes after the first empty reading, but the outage interrupted the evidence.
        Assert.True((await Recheck(queue, clock, a.Id)).RemoteUnconfirmed);
        Assert.True((await Recheck(queue, clock, a.Id)).RemoteUnconfirmed);
        Assert.False((await Recheck(queue, clock, a.Id)).RemoteUnconfirmed);
    }

    [Fact]
    public async Task HistoryThatReappearsDuringTheGraceIsRecoveredInsteadOfRetired()
    {
        var comfy = new FakeComfy { DropNextObservation = true }; var clock = new CancellationClock();
        var a = await Store.EnqueueAsync(Request(), _ct);
        var queue = await StartComfy(new(comfy), clock);
        await Dropped(a.Id);
        comfy.Forgotten = true;
        Assert.True((await Recheck(queue, clock, a.Id)).RemoteUnconfirmed);

        comfy.Forgotten = false; comfy.FinishRunning();

        Assert.Equal(AiJobState.Completed, (await Recheck(queue, clock, a.Id)).State);
        Assert.Equal(1, comfy.Posts);
    }

    [Fact]
    public void OnlyAnUnconfirmedRequestWithNoOtherScheduleIsRecheckedOnTheBackoff()
    {
        Assert.Equal([5, 10, 20, 40, 60, 60], Enumerable.Range(0, 6).Select(n => (int)ComfyQueuePolicy.RecheckDelay(n).TotalSeconds));
        var now = DateTimeOffset.UtcNow;
        var job = new AiJobHeader { Id = Guid.NewGuid(), Kind = AiJobKind.Video, Backend = AiBackend.ComfyUI, Target = new(Guid.NewGuid()), ProjectName = "QA", TargetName = "Shot",
            OriginTabId = Guid.NewGuid(), RequestFingerprint = "fingerprint", State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.CheckStatus, RemoteUnconfirmed = true, FinishedUtc = now.AddSeconds(-6) };
        Assert.True(ComfyQueuePolicy.ShouldRecheck(job, 0, now));
        Assert.False(ComfyQueuePolicy.ShouldRecheck(job, 1, now));
        Assert.False(ComfyQueuePolicy.ShouldRecheck(job with { RemoteUnconfirmed = false }, 0, now));
        Assert.False(ComfyQueuePolicy.ShouldRecheck(job with { CancelRequested = true }, 0, now));
        Assert.False(ComfyQueuePolicy.ShouldRecheck(job with { ComfyControl = new(Guid.NewGuid()) }, 0, now));
        Assert.False(ComfyQueuePolicy.ShouldRecheck(job with { State = AiJobState.Running }, 0, now));
        Assert.False(ComfyQueuePolicy.ShouldRecheck(job with { Backend = AiBackend.OpenRouter }, 0, now));
    }

    private sealed class ComfyJobStub(FakeComfy comfy) : IAiJobHandler
    {
        private const string Operation = "work";
        private static readonly ComfyExecutionOptions Options = new(new Dictionary<string, ComfyNodeStage>(), "Rejected", "Failed", "Timed out", "Timed out", "Unreadable", "ComfyUI connection failed.");
        private readonly ComfyJobExecution _transport = new(comfy);
        private int _recoveries;
        public int Recoveries => Volatile.Read(ref _recoveries);
        public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.ScriptAssistant];
        public Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => RunAsync(http =>
            _transport.ExecuteAsync(context, Operation, http, client => new
            {
                prompt = new Dictionary<string, object> { ["1"] = new { class_type = "TestNode", inputs = new { job = context.Job.Id } } },
                client_id = client
            }, Options, ct), ct);
        public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
        {
            Interlocked.Increment(ref _recoveries);
            return RunAsync(http => _transport.ObserveAsync(context, Operation, http, ct: ct), ct);
        }
        public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) =>
            _transport.CancelAsync(context, _ => comfy.Client(), ct);
        private async Task<AiJobOutcome> RunAsync(Func<HttpClient, IAsyncEnumerable<ComfyExecutionUpdate>> updates, CancellationToken ct)
        {
            using var http = comfy.Client();
            await foreach (var update in updates(http).WithCancellation(ct))
                if (update.Complete) return AiJobOutcome.Complete();
            return AiJobOutcome.Attention("No output yet.", AiJobRecovery.CheckStatus);
        }
    }

    private sealed class FakeComfy : HttpMessageHandler, IComfyExecutionMonitor
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, object[]> _running = [];
        private readonly Dictionary<string, object> _history = [];
        private readonly List<JsonElement> _posted = [];
        private int _observations;
        public volatile bool Offline, Forgotten, DropNextObservation, GoOfflineOnDrop, LoseNextAcceptance;
        public int Observations => Volatile.Read(ref _observations);
        public int Posts { get { lock (_gate) return _posted.Count; } }
        public IReadOnlyList<JsonElement> PostedGraphs { get { lock (_gate) return _posted.ToArray(); } }
        public HttpClient Client() => new(this, disposeHandler: false) { BaseAddress = new("http://comfy.test/"), Timeout = Timeout.InfiniteTimeSpan };
        // A restart loses both: ComfyUI keeps its queue and history in memory.
        public void Restart() { lock (_gate) { _running.Clear(); _history.Clear(); } }
        public void FinishRunning() { lock (_gate) foreach (var id in _running.Keys.ToArray()) Finish(id); }
        private JsonElement Finish(string id)
        {
            var result = new { prompt = _running.Remove(id, out var row) ? row : [], outputs = new Dictionary<string, object> { ["1"] = new { text = new[] { "Finished" } } },
                status = new { completed = true, status_str = "success" } };
            _history[id] = result;
            return JsonSerializer.SerializeToElement(result);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("No connection could be made.");
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (request.Method == HttpMethod.Post && path == "prompt")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                lock (_gate)
                {
                    _posted.Add(body.GetProperty("prompt"));
                    if (LoseNextAcceptance) { LoseNextAcceptance = false; throw new HttpRequestException("The connection closed before a response."); }
                    var id = Guid.NewGuid().ToString("D");
                    _running[id] = [1, id, body.GetProperty("prompt"), new { client_id = body.GetProperty("client_id").GetString() }, Array.Empty<string>()];
                    return Json(new { prompt_id = id });
                }
            }
            lock (_gate)
            {
                if (path == "queue") return Json(new { queue_running = Forgotten ? [] : _running.Values.ToArray(), queue_pending = Array.Empty<object>() });
                if (path == "history") return Json(Forgotten ? [] : _history);
                if (path.StartsWith("history/", StringComparison.Ordinal))
                {
                    var id = path["history/".Length..];
                    return Json(!Forgotten && _history.TryGetValue(id, out var result) ? new Dictionary<string, object> { [id] = result } : []);
                }
            }
            throw new InvalidOperationException("Unexpected request: " + request.Method + " " + path);
        }
        // Serialized under the lock: a lazily read body would race with later changes.
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        public async IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(HttpClient http, string promptId, string clientId, ComfyExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref _observations);
            if (DropNextObservation)
            {
                DropNextObservation = false;
                if (GoOfflineOnDrop) Offline = true;
                throw new AiGenerationException("ComfyUI connection failed.");
            }
            JsonElement job; lock (_gate) job = Finish(promptId);
            yield return new(new(GenerationPhase.Completed, "Done"), promptId, job, true);
        }
        public IAsyncEnumerable<ComfyExecutionUpdate> ExecuteAsync(HttpClient http, Func<string, object> workflowFactory, ComfyExecutionOptions options, CancellationToken operationToken, CancellationToken callerToken) => throw new NotSupportedException();
    }
}
