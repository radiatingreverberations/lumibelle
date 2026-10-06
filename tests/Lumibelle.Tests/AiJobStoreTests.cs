using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;
public sealed partial class AiJobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.JobTests", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly JobClock _clock = new();
    private FileAiJobStore Store => new(new StorageTestEnvironment(_root), _clock);
    private string Index => Path.Combine(_root, "App_Data", "ai-jobs", "queue.json");
    [Theory]
    [InlineData(AiJobState.NeedsAttention, AiJobRecovery.CheckStatus, false, false, true)]
    [InlineData(AiJobState.NeedsAttention, AiJobRecovery.RetryOutput, false, false, true)]
    [InlineData(AiJobState.NeedsAttention, AiJobRecovery.GenerateAgain, false, false, false)]
    [InlineData(AiJobState.NeedsAttention, AiJobRecovery.None, false, false, false)]
    [InlineData(AiJobState.NeedsAttention, AiJobRecovery.CheckStatus, true, false, false)]
    [InlineData(AiJobState.NeedsAttention, AiJobRecovery.CheckStatus, false, true, false)]
    [InlineData(AiJobState.Completed, AiJobRecovery.RetryOutput, false, false, false)]
    [InlineData(AiJobState.Waiting, AiJobRecovery.CheckStatus, false, false, false)]
    public async Task CapturedRetryEligibilityMatchesStoreWithoutReplacingInputs(AiJobState state, AiJobRecovery recovery, bool cancelled, bool remote, bool allowed)
    {
        var store = Store; var request = Request(); var queued = await store.EnqueueAsync(request, _ct);
        var job = await store.UpdateAsync(queued.Id, j => j with { State = state, Recovery = recovery, CancelRequested = cancelled, RemoteUnconfirmed = remote }, _ct);
        Assert.Equal(allowed, job.CanRetryCaptured);
        if (allowed) Assert.Equal(AiJobState.Waiting, (await store.RequeueAsync(job.Id, _ct)).State);
        else await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.RequeueAsync(job.Id, _ct));
        Assert.Single((await store.ReadAsync(_ct)).Jobs);
        Assert.True(JsonElement.DeepEquals(request.Snapshot, await store.ReadSnapshotAsync(job.Id, _ct)));
    }
    private AiJobSubmission Request(AiJobKind kind = AiJobKind.ScriptAssistant, AiBackend backend = AiBackend.ComfyUI, AiJobTarget? target = null) =>
        AiJobSubmission.Create(Guid.NewGuid(), kind, backend, target ?? new(Guid.NewGuid()), "QA", "A target", Guid.NewGuid(), new { prompt = "Captured instruction", settings = new AiSettings() });
    [Fact]
    public async Task SingleShotDraftsQueueSideBySideButEachShotTakesOneAtATime()
    {
        var project = Guid.NewGuid();
        AiJobSubmission Draft(Guid? shot) => Request(AiJobKind.ShotPlanning, target: new(project, ShotId: shot));
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        await Store.EnqueueAsync(Draft(first), _ct);
        await Store.EnqueueAsync(Draft(second), _ct);
        // A scene breakdown is independent of drafts for single shots.
        await Store.EnqueueAsync(Draft(null), _ct);
        Assert.Equal(3, (await Store.ReadAsync(_ct)).Jobs.Count);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(Draft(first), _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(Draft(null), _ct));
    }
    [Fact]
    public async Task EnqueueIsIdempotentAndCapturesInputsBeforeWaitingForPublication()
    {
        var store = Store; using var json = JsonDocument.Parse("{\"prompt\":\"exact input\"}");
        var request = Request() with { Snapshot = json.RootElement };
        Task<AiJobHeader> enqueue;
        using (await ProjectFiles.LockAsync(Index, _ct)) { enqueue = store.EnqueueAsync(request, _ct); json.Dispose(); Assert.False(enqueue.IsCompleted); }
        var saved = await enqueue; var snapshot = await Store.ReadSnapshotAsync(saved.Id, _ct);
        Assert.Equal("exact input", snapshot.GetProperty("prompt").GetString());
        var retry = request with { Snapshot = snapshot };
        Assert.Equal(saved, await Store.EnqueueAsync(retry, _ct)); Assert.Single((await Store.ReadAsync(_ct)).Jobs);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(retry with { Snapshot = JsonSerializer.SerializeToElement(new { prompt = "different" }) }, _ct));
        await Store.UpdateAsync(saved.Id, j => j with { State = AiJobState.Completed, Unread = true }, _ct);
        Assert.Equal(AiJobState.Completed, (await Store.EnqueueAsync(retry, _ct)).State);
        Assert.DoesNotContain("exact input", await File.ReadAllTextAsync(Index, _ct));
    }
    [Fact]
    public async Task FailedQueuePublicationKeepsOldIndexAndCanRetryItsOrphanInput()
    {
        var first = await Store.EnqueueAsync(Request(), _ct); var request = Request();
        using (var locked = new FileStream(Index, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<Exception>(() => Store.EnqueueAsync(request, _ct));
        Assert.Equal(first.Id, (await Store.ReadAsync(_ct)).Jobs.Single().Id);
        Assert.True(File.Exists(Path.Combine(Store.DirectoryFor(request.Id), "request.json")));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.ReadSnapshotAsync(request.Id, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(request with { TargetName = "Different request" }, _ct));
        await Store.EnqueueAsync(request, _ct); Assert.Equal(2, (await Store.ReadAsync(_ct)).Jobs.Count);
    }
    [Fact]
    public async Task PromptCompositionLocksTheShotAcrossGenerationSetups()
    {
        var target = new AiJobTarget(Guid.NewGuid(), ShotId: Guid.NewGuid(), CompositionId: Guid.NewGuid());
        var first = await Store.EnqueueAsync(Request(AiJobKind.PromptComposition, AiBackend.OpenRouter, target), _ct);
        var otherSetup = target with { CompositionId = Guid.NewGuid() };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(Request(AiJobKind.PromptComposition, AiBackend.Codex, otherSetup), _ct));
        await Store.EnqueueAsync(Request(AiJobKind.PromptComposition, AiBackend.OpenRouter, otherSetup with { ShotId = Guid.NewGuid() }), _ct);
        await Store.UpdateAsync(first.Id, j => j with { State = AiJobState.Completed }, _ct);
        await Store.EnqueueAsync(Request(AiJobKind.PromptComposition, AiBackend.OpenRouter, otherSetup), _ct);
    }
    [Fact]
    public async Task TargetLocksSpanProvidersAndReleaseWhenResultsAwaitReview()
    {
        var project = Guid.NewGuid(); var asset = new AiJobTarget(project, AssetId: Guid.NewGuid());
        var image = await Store.EnqueueAsync(Request(AiJobKind.ImageEdit, target: asset), _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(Request(AiJobKind.PromptEnhancement, AiBackend.OpenRouter, asset), _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(Request(AiJobKind.ImageCreate, target: asset), _ct));
        await Store.EnqueueAsync(Request(AiJobKind.Guidance, target: asset with { GuidanceScope = GuidanceScope.CharacterIdentity }), _ct);
        await Store.EnqueueAsync(Request(AiJobKind.Guidance, target: asset with { GuidanceScope = GuidanceScope.Image, ImageId = Guid.NewGuid() }), _ct);
        await Store.EnqueueAsync(Request(target: new(project)), _ct);
        await Store.EnqueueAsync(Request(AiJobKind.AssetExtraction, target: new(project)), _ct);
        await Store.EnqueueAsync(Request(AiJobKind.ShotPlanning, target: new(project)), _ct);
        await Store.UpdateAsync(image.Id, j => j with { State = AiJobState.Completed, Unread = true }, _ct);
        await Store.EnqueueAsync(Request(AiJobKind.PromptEnhancement, AiBackend.OpenRouter, asset), _ct);
        Assert.Equal(7, (await Store.ReadAsync(_ct)).Jobs.Count);
    }
    [Fact]
    public async Task FifoReorderingPauseAndProviderLimitsSurviveReopening()
    {
        var a = await Store.EnqueueAsync(Request(), _ct); var router = await Store.EnqueueAsync(Request(backend: AiBackend.OpenRouter), _ct);
        var b = await Store.EnqueueAsync(Request(), _ct); var c = await Store.EnqueueAsync(Request(), _ct);
        await Store.MoveAsync(c.Id, 0, first: true, ct: _ct);
        await Store.SetPausedAsync(AiBackend.ComfyUI, true, _ct);
        Assert.Null(await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct));
        Assert.Equal(router.Id, (await Store.ClaimNextAsync(AiBackend.OpenRouter, 1, _ct))!.Id);
        await Store.SetPausedAsync(AiBackend.ComfyUI, false, _ct);
        _clock.Now = _clock.Now.AddMinutes(10);
        var claimed = await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct); Assert.Equal(c.Id, claimed!.Id);
        Assert.Equal(_clock.Now, claimed.StartedUtc); Assert.NotNull(claimed.LeaseId);
        Assert.Null(await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.MoveAsync(c.Id, -1, ct: _ct));
        await Store.UpdateAsync(c.Id, j => j with { State = AiJobState.Completed }, _ct);
        await Store.MoveAsync(b.Id, -1, ct: _ct);
        Assert.Equal(b.Id, (await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!.Id);
        await Store.UpdateAsync(b.Id, j => j with { State = AiJobState.Cancelled, CancelRequested = true, RemoteUnconfirmed = true }, _ct);
        Assert.Null(await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct));
        await Store.UpdateAsync(b.Id, j => j with { RemoteUnconfirmed = false }, _ct);
        Assert.Equal(a.Id, (await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!.Id);
    }
    [Theory] [InlineData(AiBackend.ComfyUI, 1)] [InlineData(AiBackend.OpenRouter, 3)]
    public async Task SimultaneousDispatchClaimsCannotExceedProviderCapacity(AiBackend provider, int capacity)
    {
        for (var i = 0; i < 8; i++) await Store.EnqueueAsync(Request(backend: provider), _ct);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store.ClaimNextAsync(provider, capacity, _ct)));
        Assert.Equal(capacity, claims.Count(c => c is not null));
        Assert.Equal(capacity, claims.Where(c => c is not null).Select(c => c!.LeaseId).Distinct().Count());
        Assert.Null(await Store.ClaimNextAsync(provider, 1, _ct)); // lowering concurrency does not cancel existing work
    }
    [Fact]
    public async Task CancelledWaitingRequestsReleaseOnlyTheirTargetAndProgressDoesNotRewriteInputs()
    {
        var request = Request(); var job = await Store.EnqueueAsync(request, _ct);
        var inputPath = Path.Combine(Store.DirectoryFor(job.Id), "request.json"); var bytes = await File.ReadAllBytesAsync(inputPath, _ct);
        var revision = (await Store.ReadAsync(_ct)).Revision;
        await Store.WriteArtifactAsync(job.Id, AiJobArtifact.Progress, new AiJobProgress(new(GenerationPhase.Generating, "Tokens", 10, 100, "tokens")), _ct);
        Assert.Equal(revision, (await Store.ReadAsync(_ct)).Revision);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(inputPath, _ct));
        Assert.Equal(10, (await Store.ReadArtifactAsync<AiJobProgress>(job.Id, AiJobArtifact.Progress, _ct))!.Progress.Current);
        await Store.UpdateAsync(job.Id, j => j with { CancelRequested = true, State = AiJobState.Cancelled }, _ct);
        Assert.Null(await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct));
        await Store.EnqueueAsync(request with { Id = Guid.NewGuid() }, _ct);
        Assert.Single((await Store.ReadAsync(_ct)).Jobs, j => j.LocksTarget);
    }
    [Fact]
    public async Task CorruptedRequestAndIdentityMutationAreRejected()
    {
        var job = await Store.EnqueueAsync(Request(), _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.UpdateAsync(job.Id, j => j with { Target = new(Guid.NewGuid()) }, _ct));
        var path = Path.Combine(Store.DirectoryFor(job.Id), "request.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, _ct))!;
        json["request"]!["prompt"] = "unexpected modification"; await File.WriteAllTextAsync(path, json.ToJsonString(), _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.ReadSnapshotAsync(job.Id, _ct));
        Assert.Equal(AiJobState.Waiting, (await Store.ReadAsync(_ct)).Jobs.Single().State);
    }
    [Fact]
    public async Task AHeldHeaderReadsTheSameSnapshotAndStillRejectsAMismatch()
    {
        var request = Request(); var job = await Store.EnqueueAsync(request, _ct);
        Assert.True(JsonElement.DeepEquals(request.Snapshot, await Store.ReadSnapshotAsync(job, _ct)));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.ReadSnapshotAsync(job with { TargetName = "Another target" }, _ct));
    }
    [Fact]
    public async Task EnqueueRacesAndMalformedTargetsCannotBypassDuplicatePrevention()
    {
        var request = Request();
        var errors = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Record.ExceptionAsync(async () => await Store.EnqueueAsync(request with { Id = Guid.NewGuid() }, _ct)).AsTask()));
        Assert.Single(errors, e => e is null); Assert.Equal(7, errors.Count(e => e is WorkspaceStoreException));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(Request(AiJobKind.Video, target: new(Guid.NewGuid())), _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(Request(AiJobKind.ImageCreate, AiBackend.OpenRouter, new(Guid.NewGuid(), AssetId: Guid.NewGuid())), _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.EnqueueAsync(Request(AiJobKind.Guidance, target: new(Guid.NewGuid(), AssetId: Guid.NewGuid(), GuidanceScope: GuidanceScope.Look)), _ct));
    }
    private sealed class JobClock : TimeProvider { public DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    [Fact]
    public async Task ArtifactReadsWaitForTheirWriterSoTheyNeverBlockItsRename()
    {
        var job = await Store.EnqueueAsync(Request(), _ct); job = (await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        await Store.WriteOwnedArtifactAsync(job.Id, job.LeaseId!.Value, AiJobArtifact.Result, new { raw = "Partial" }, _ct);
        // Windows refuses a rename over a file that is open, so a slow read during publication would fail the job's result.
        Task<JsonElement> read;
        using (await ProjectFiles.LockAsync(Path.Combine(Store.DirectoryFor(job.Id), "result.json"), _ct))
        {
            read = Store.ReadArtifactAsync<JsonElement>(job.Id, AiJobArtifact.Result, _ct);
            await Task.WhenAny(read, Task.Delay(500, _ct)); Assert.False(read.IsCompleted);
        }
        Assert.Contains("Partial", (await read).ToString());
    }
    [Fact]
    public async Task LeasesRejectSupersededWritesWhileAllowingLateAcceptanceOfCancelledRemoteJob()
    {
        var job = await Store.EnqueueAsync(Request(), _ct); job = (await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        var old = job.LeaseId!.Value; var next = Guid.NewGuid();
        await Store.UpdateAsync(job.Id, j => j with { LeaseId = next }, _ct);
        await Store.WriteOwnedArtifactAsync(job.Id, next, AiJobArtifact.Result, new { raw = "New worker" }, _ct);
        await Assert.ThrowsAsync<AiJobLeaseException>(() => Store.WriteOwnedArtifactAsync(job.Id, old, AiJobArtifact.Result, new { raw = "Late old worker" }, _ct));
        Assert.Contains("New worker", (await Store.ReadArtifactAsync<JsonElement>(job.Id, AiJobArtifact.Result, _ct)).ToString());
        await Store.UpdateAsync(job.Id, j => j with { State = AiJobState.Cancelled, CancelRequested = true, RemoteUnconfirmed = true }, _ct);
        await Assert.ThrowsAsync<AiJobLeaseException>(() => Store.WriteOwnedArtifactAsync(job.Id, next, AiJobArtifact.Result, new { raw = "Late cancelled result" }, _ct));
        var receipt = new AiJobExecution([new("text", "http://comfy.test", Guid.NewGuid().ToString(), _clock.Now, AiRemoteState.Accepted, "owned-id")]);
        await Store.WriteOwnedArtifactAsync(job.Id, next, AiJobArtifact.Execution, receipt, _ct);
        Assert.Equal("owned-id", (await Store.ReadArtifactAsync<AiJobExecution>(job.Id, AiJobArtifact.Execution, _ct))!.Submissions.Single().PromptId);
    }
    [Fact]
    public async Task CancelledVideoRecoveryRetainsLeaseChecksAndCannotWriteNewRequests()
    {
        var request = Request(AiJobKind.Video, target: new(Guid.NewGuid(), ShotId: Guid.NewGuid()));
        var queued = await Store.EnqueueAsync(request, _ct);
        var job = (await Store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        await Store.UpdateAsync(job.Id, j => j with { CancelRequested = true }, _ct);
        var lease = job.LeaseId!.Value;
        await Assert.ThrowsAsync<AiJobLeaseException>(() => Store.WriteOwnedArtifactAsync(job.Id, lease, AiJobArtifact.Result, new { result = "late" }, _ct));
        await Store.WriteOwnedArtifactAsync(job.Id, lease, AiJobArtifact.Result, new { result = "recovered" }, _ct, recoveringCancelledOutputs: true);
        await Store.WriteOperationAsync(job.Id, lease, "candidate/one", AiOperationArtifact.Video, new { video = "saved.mp4" }, _ct, recoveringCancelledOutputs: true);
        await Assert.ThrowsAsync<AiJobLeaseException>(() => Store.WriteOperationAsync(job.Id, lease, "new-request", AiOperationArtifact.Request, new { prompt = "forbidden" }, _ct, recoveringCancelledOutputs: true));
        await Assert.ThrowsAsync<AiJobLeaseException>(() => Store.WriteOperationAsync(job.Id, Guid.NewGuid(), "candidate/two", AiOperationArtifact.Video, new { video = "stale.mp4" }, _ct, recoveringCancelledOutputs: true));
        await Store.UpdateAsync(job.Id, j => j with { State = AiJobState.Cancelled }, _ct);
        await Assert.ThrowsAsync<AiJobLeaseException>(() => Store.WriteOwnedArtifactAsync(job.Id, lease, AiJobArtifact.Result, new { result = "after lease finished" }, _ct, recoveringCancelledOutputs: true));
    }

    [Fact]
    public void OnlyOneApplicationOwnsExecutionWhileReadAccessRemainsIndependent()
    {
        using (Store.AcquireExecutionOwner()) Assert.Throws<WorkspaceStoreException>(() => Store.AcquireExecutionOwner());
        using var restartedOwner = Store.AcquireExecutionOwner();
        Assert.NotNull(restartedOwner);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
