using System.Net;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task VideoProgressRecordsActualBranchOrderAcrossRecoveryWithoutRenumberingCandidates()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var request = await f.Capture(2); var context = await f.Claim(request);
        await context.ReportAsync(new(new(GenerationPhase.Preparing, "Preparing references"), 1, 2));
        Assert.Empty(f.Progress[^1].CandidateExecutionOrder!);
        await context.ReportAsync(new(new(GenerationPhase.Generating, "Take 2 · Sampling H3", 1, 8, "steps"), 2, 2));
        await context.ReportAsync(new(new(GenerationPhase.Finalizing, "Take 2 · Decoding video"), 2, 2));
        Assert.Equal(new[] { 2 }, f.Progress[^1].CandidateExecutionOrder);

        // A restored worker must continue the saved order, even before any take is downloaded.
        var recovered = f.Context(context.Job, true);
        await recovered.ReportAsync(new(new(GenerationPhase.Generating, "Take 1 · Sampling H3", 1, 8, "steps"), 1, 2));
        Assert.Equal(1, f.Progress[^1].Candidate);
        Assert.Equal(new[] { 2, 1 }, f.Progress[^1].CandidateExecutionOrder);
        var checkpoint = (await recovered.ReadAsync<AiJobProgress>(AiJobArtifact.Progress, _ct))!;
        Assert.Equal(new[] { 2, 1 }, checkpoint.CandidateExecutionOrder);
        Assert.Equal(request.Batch!.Candidates, (await recovered.CurrentAsync(_ct)).Batch!.Candidates);
        Assert.Empty(f.Graphs);
    }

    [Fact]
    public void EarlierRunReviewKeepsSavedMediaAndReceiptsWithoutPretendingTheWorkerIsActive()
    {
        var original = new VideoRun { Snapshot = Snapshot(Guid.NewGuid(), Ready()), Status = "Generating…", Candidates = [
            new() { Number = 1, State = VideoCandidateState.Complete },
            new() { Number = 2, State = VideoCandidateState.Downloading, PromptId = "known-job", Output = JsonSerializer.SerializeToElement(new { saved = true }) },
            new() { Number = 3, State = VideoCandidateState.Submitting },
            new() { Number = 4, State = VideoCandidateState.Waiting }] };
        var review = AiVideoBatchReview.Legacy(original);
        Assert.True(review.Paused); Assert.Contains("Earlier batch", review.Status);
        Assert.Equal(new[] { VideoCandidateState.Complete, VideoCandidateState.Uncertain, VideoCandidateState.Uncertain, VideoCandidateState.Cancelled }, review.Candidates.Select(c => c.State));
        Assert.Equal(original.Candidates.Select(c => c.TakeId), review.Candidates.Select(c => c.TakeId));
        Assert.Equal("known-job", review.Candidates[1].PromptId); Assert.NotNull(review.Candidates[1].Output);
        Assert.Equal(VideoCandidateState.Downloading, original.Candidates[1].State); Assert.False(original.Paused);
    }

    [Fact]
    public void TakesWhoseBatchRecordIsMissingAreReviewedAsAClosedBatch()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Ready()); var runId = Guid.NewGuid(); var start = DateTimeOffset.UtcNow;
        var takes = new[] { 2, 1 }.Select(n => new ShotTake { RunId = runId, Candidate = n, Seed = n * 10, CreatedUtc = start.AddMinutes(n), Snapshot = snapshot }).ToArray();
        var review = AiVideoBatchReview.Archived(takes);
        Assert.Equal(runId, review.Id); Assert.True(review.Paused); Assert.Contains("Earlier batch", review.Status);
        Assert.Equal(start.AddMinutes(1), review.CreatedUtc);
        Assert.Equal(new[] { takes[1].Id, takes[0].Id }, review.Candidates.Select(c => c.TakeId));
        Assert.Equal(new[] { 1, 2 }, review.Candidates.Select(c => c.Number));
        Assert.All(review.Candidates, c => Assert.Equal(VideoCandidateState.Complete, c.State));
    }

    [Theory]
    [InlineData(20)] [InlineData(4)] [InlineData(8)]
    public async Task QueuedVideoKeepsCapturedShotAndSamplingAcrossCandidatesAndExtensions(int steps)
    {
        using var f = await QueuedVideoFixture.Create(this, steps);
        f.Settings.Value.H3.Performance = new() { Attention = H3AttentionBackend.Sage, SolAttention = true };
        var request = await f.Capture(2);
        var context = await f.Claim(request); var snapshot = request.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        f.Shot.Description = "Later author edit"; f.Settings.Value = new() { ComfyUrl = "http://changed.test" };
        var document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], document.Revision, ct: _ct);
        f.Adapter.BeforeDownload = async () =>
        {
            f.Adapter.BeforeDownload = null;
            var command = Guid.NewGuid();
            var append = await f.Jobs.ExtendBatchAsync(request.Id, command, request.OriginTabId, _ct);
            Assert.Equal(append.Candidate, (await f.Jobs.ExtendBatchAsync(request.Id, command, request.OriginTabId, _ct)).Candidate);
        };
        Assert.Equal(3, (await f.Worker.ExecuteAsync(context, request.Snapshot, _ct)).CompletedCandidates);
        var saved = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal("Later author edit", saved.Shots[0].Description); Assert.Equal(3, saved.Takes.Count);
        Assert.Equal(2, f.Graphs.Count); Assert.All(saved.Takes, take =>
        {
            Assert.Equal(request.Id, take.AiJobId); Assert.Equal(request.Id, take.RunId);
            Assert.Equal(snapshot.Snapshot.Prompt, take.Snapshot.Prompt); Assert.Equal(steps, take.Snapshot.Sampling!.Steps);
            Assert.Equal(snapshot.Snapshot.Performance, take.Snapshot.Performance);
            Assert.Equal(H3AttentionBackend.Sage, take.Snapshot.Performance!.Attention);
            Assert.NotNull(take.Timings?.TransferSaveSeconds);
            Assert.Null(take.Timings?.SamplingSeconds);
        });
        Assert.Equal(new long[] { 52, 53 }, saved.Takes.Take(2).Select(t => t.Seed));
        Assert.Equal(3, saved.Takes.Select(t => t.Seed).Distinct().Count());
        Assert.All(f.Graphs, graph =>
        {
            var nodes = graph.GetProperty("prompt");
            Assert.Equal(steps, nodes.GetProperty("9").GetProperty("inputs").GetProperty("steps").GetInt32());
            Assert.False(nodes.TryGetProperty("17", out _)); Assert.False(nodes.TryGetProperty("15", out _));
            Assert.Equal(H3Performance.SageNode, nodes.GetProperty("30").GetProperty("class_type").GetString());
            Assert.False(nodes.TryGetProperty("31", out _));

        });
        Assert.Contains(f.Progress, p => p.CandidateCount == 3 && p.Progress.Phase == GenerationPhase.Downloading);
        Assert.Equal(3, (await context.ReadAsync<AiVideoJobResult>(AiJobArtifact.Result, _ct))!.Candidates.Count);
    }

    [Fact]
    public async Task VideoTransferAndPublicationRetryNeverRepeatInferenceOrResurrectPurgedTakes()
    {
        using var f = await QueuedVideoFixture.Create(this); var request = await f.Capture(); var context = await f.Claim(request);
        f.Adapter.FailTransfer = true;
        var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, _ct));
        Assert.Equal(AiJobRecovery.RetryOutput, failure.Recovery); Assert.Single(f.Graphs);
        Assert.Empty((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        f.Adapter.FailTransfer = false;
        FileStream? locked = null;
        f.Adapter.BeforeDownload = () =>
        {
            locked = new(Path.Combine(f.ProjectDirectory, "shots.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.CompletedTask;
        };
        try
        {
            failure = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.RecoverAsync(f.Context(context.Job, true), request.Snapshot, _ct));
            Assert.Equal(AiJobRecovery.RetryOutput, failure.Recovery);
        }
        finally { locked?.Dispose(); }
        Assert.Single(f.Graphs); Assert.Equal(2, f.Adapter.Downloads); f.NoNetwork = true;
        Assert.Equal(1, (await f.Worker.RecoverAsync(f.Context(context.Job, true), request.Snapshot, _ct)).CompletedCandidates);
        var saved = await f.Shots.LoadAsync(f.Project.Id, _ct); var take = Assert.Single(saved.Takes);
        var discarded = await f.Shots.DiscardAsync(f.Project.Id, take.Id, ShotTrashKind.Take, saved.Revision, _ct);
        await f.Shots.PurgeAsync(f.Project.Id, discarded.Trash.Select(t => t.Id).ToArray(), discarded.Revision, _ct);
        Assert.Equal(1, (await f.Worker.RecoverAsync(f.Context(context.Job, true), request.Snapshot, _ct)).CompletedCandidates);
        Assert.Empty((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes); Assert.Equal(2, f.Adapter.Downloads);
        Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).TakePublications);
    }

    [Fact]
    public async Task ACompletedVideoContinuationUsesOriginalSnapshotAndWaitsForItsOwnDispatch()
    {
        using var f = await QueuedVideoFixture.Create(this); var request = await f.Capture(); var context = await f.Claim(request);
        await f.Worker.ExecuteAsync(context, request.Snapshot, _ct);
        await f.Jobs.UpdateAsync(request.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var append = await f.Jobs.ExtendBatchAsync(request.Id, Guid.NewGuid(), request.OriginTabId, _ct);
        Assert.True(append.Continuation); Assert.Equal(2, append.Candidate.Number);
        var snapshot = await f.Jobs.ReadSnapshotAsync(append.Job.Id, _ct); Assert.True(JsonElement.DeepEquals(request.Snapshot, snapshot));
        var claimed = (await f.Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        Assert.Equal(0, (await f.Worker.RecoverAsync(f.Context(claimed, true), snapshot, _ct)).CompletedCandidates);
        Assert.Single(f.Graphs); // Recovery of an untouched candidate cannot submit.
        await f.Worker.ExecuteAsync(f.Context(claimed, false), snapshot, _ct);
        var takes = (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes;
        Assert.Equal(new[] { 1, 2 }, takes.Select(t => t.Candidate)); Assert.All(takes, t => Assert.Equal(request.Id, t.RunId));
        Assert.Equal(append.Job.Id, takes[1].AiJobId);
    }

    [Fact]
    public async Task MissingShotOrCapabilitiesBlockNewSubmissionButNotRetrieval()
    {
        using var f = await QueuedVideoFixture.Create(this); var request = await f.Capture(); var context = await f.Claim(request);
        f.Adapter.Missing = true;
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, _ct)); Assert.Empty(f.Graphs);
        f.Adapter.Missing = false;
        var d = await f.Shots.LoadAsync(f.Project.Id, _ct); await f.Shots.SaveAsync(f.Project.Id, [], d.Revision, ct: _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Worker.ValidateExtensionAsync(context.Job, request.Snapshot, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, _ct)); Assert.Empty(f.Graphs);
    }

    [Fact]
    public async Task UncertainVideoSubmissionKeepsItsReceiptAndCannotBeAutomaticallyRepeated()
    {
        using var f = await QueuedVideoFixture.Create(this); var request = await f.Capture(); var context = await f.Claim(request);
        f.LoseReceipt = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, _ct));
        Assert.True((await context.ExecutionAsync(_ct)).MayBeRunning);
        await Assert.ThrowsAsync<AiGenerationException>(() => f.Worker.RecoverAsync(f.Context(context.Job, true), request.Snapshot, _ct));
        Assert.Single(f.Graphs); Assert.Empty((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
    }

    [Fact]
    public async Task TakePublicationReceiptsPreserveAuthoredEditsAndOwnerRemovalAcrossPurge()
    {
        var f = Fixture(); var shot = Ready(); var d = await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        d = await AddTake(f.Project.Id, f.Shots, shot); var take = d.Takes[0];
        d = await f.Shots.SaveAsync(f.Project.Id, [], d.Revision, ct: _ct);
        d = await f.Shots.PurgeAsync(f.Project.Id, d.Trash.Select(t => t.Id).ToArray(), d.Revision, _ct);
        Assert.Empty(d.Trash); Assert.Single(d.TakePublications);
        var retry = await f.Shots.PublishTakeAsync(f.Project.Id, take, "unused-staging", _ct);
        Assert.Equal(d.Revision, retry.Revision); Assert.Empty(retry.Takes); Assert.Empty(retry.Trash);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Shots.PublishTakeAsync(f.Project.Id, take with { Seed = take.Seed + 1 }, "unused", _ct));
    }

    [Fact]
    public async Task QueuedVideoCapturesOrderedCropsAndBlocksModifiedOrTrashedInputs()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Two views" };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        foreach (var size in new[] { (16, 8), (8, 16) })
        {
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(size.Item1, size.Item2);
            using var bytes = new MemoryStream(); await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(image, bytes, _ct); bytes.Position = 0;
            library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, bytes, new("view.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        }
        f.Shot.Images = library.Assets[0].Images.Select((image, i) => new ShotImageBinding { AssetId = asset.Id, MediaId = image.Id,
            Name = "View " + (i + 1), Crop = i == 0 ? new() { Width = .5 } : new() { Height = .5 } }).ToList();
        var document = await f.Shots.LoadAsync(f.Project.Id, _ct); await f.Shots.SaveAsync(f.Project.Id, [f.Shot], document.Revision, ct: _ct);
        var submission = await f.Capture(); var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(f.Shot.Images.Select(b => b.MediaId), request.Snapshot.Shot.Images.Select(b => b.MediaId));
        Assert.Equal(2, request.Inputs.Count);
        var directory = await f.Shots.RunDirectoryAsync(f.Project.Id, request.BatchId, _ct);
        foreach (var input in request.Inputs)
        {
            var pixels = ImageInspector.Inspect(await File.ReadAllBytesAsync(CapturedInputStore.Resolve(directory, input.FileName, input.Sha256), _ct));
            Assert.Equal((8, 8), (pixels.Width, pixels.Height));
        }
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, directory, _ct);
        var context = await f.Claim(submission);
        // Captured inputs are kept once, in the project's input store.
        var path = CapturedInputStore.Resolve(directory, request.Inputs[0].FileName, request.Inputs[0].Sha256); var original = await File.ReadAllBytesAsync(path, _ct);
        Assert.False(File.Exists(Path.Combine(directory, "inputs", request.Inputs[0].FileName)));
        await File.WriteAllBytesAsync(path, original.Select(b => (byte)(b ^ 1)).ToArray(), _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Worker.ValidateExtensionAsync(context.Job, submission.Snapshot, _ct));
        Assert.Empty(f.Graphs);
        await File.WriteAllBytesAsync(path, original, _ct);
        await f.Assets.DeleteImageAsync(f.Project.Id, asset.Id, f.Shot.Images[1].MediaId, library.Revision, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, _ct));
        Assert.Empty(f.Graphs);
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(request with { Inputs = request.Inputs.Select(i => i with { FileName = "../" + i.FileName }).ToArray() }));
        Assert.Throws<WorkspaceStoreException>(() => AiVideoJobPolicy.Validate(request with { Inputs = [request.Inputs[0], request.Inputs[1] with { Audio = true }] }));
    }

    [Fact]
    public async Task VideoReviewKeepsCompletedAccountingAfterDiscardAndShowsNewCandidatesOnlyOnce()
    {
        using var f = await QueuedVideoFixture.Create(this); var submission = await f.Capture(); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, _ct);
        var d = await f.Shots.LoadAsync(f.Project.Id, _ct); var take = d.Takes.Single();
        d = await f.Shots.DiscardAsync(f.Project.Id, take.Id, ShotTrashKind.Take, d.Revision, _ct);
        var command = Guid.NewGuid(); await f.Jobs.ExtendBatchAsync(submission.Id, command, submission.OriginTabId, _ct);
        await f.Jobs.ExtendBatchAsync(submission.Id, command, submission.OriginTabId, _ct);
        var jobs = (await f.Jobs.ReadAsync(_ct)).Jobs;
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var review = AiVideoBatchReview.Project(request, jobs, d, _ => new(new(GenerationPhase.Generating, "Sampling"), 2, 2));
        Assert.Equal(2, review.Candidates.Count); Assert.Equal(VideoCandidateState.Complete, review.Candidates[0].State);
        Assert.Equal(VideoCandidateState.Running, review.Candidates[1].State);
        d = await f.Shots.RestoreAsync(f.Project.Id, d.Trash.Select(t => t.Id).ToArray(), d.Revision, _ct);
        Assert.Equal(review.Candidates.Select(c => (c.TakeId, c.Number, c.State)), AiVideoBatchReview.Project(request, jobs, d, _ => new(new(GenerationPhase.Generating, "Sampling"), 2, 2)).Candidates.Select(c => (c.TakeId, c.Number, c.State)));
    }

    [Fact]
    public async Task SharedCoordinatorCancelsVideoTransferAndRecoversAllRenderedTakes()
    {
        using var f = await QueuedVideoFixture.Create(this); var submission = await f.Capture(3);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Adapter.BeforeDownload = async () => { if (f.Adapter.Downloads == 2) { entered.TrySetResult(); await release.Task; } };
        using var coordinator = new AiJobCoordinator(f.Jobs, f.Settings, [f.Worker], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        await coordinator.StartAsync(_ct);
        try
        {
            await coordinator.EnqueueAsync(submission, _ct); await entered.Task.WaitAsync(TimeSpan.FromSeconds(8), _ct);
            Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
            await coordinator.CancelAsync(submission.Id, _ct); release.TrySetResult();
            await Until(() => coordinator.View.Jobs.Any(j => j.Id == submission.Id && j.State == AiJobState.Cancelled));
            var job = coordinator.View.Jobs.Single(); Assert.False(job.LocksTarget); Assert.False(job.RemoteUnconfirmed);
            // The remote branching workflow already finished. Recover its remaining files without inference.
            Assert.Equal(3, (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Count); Assert.Single(f.Graphs);
            Assert.Equal(3, job.Batch!.Candidates.Count);
        }
        finally { release.TrySetResult(); await coordinator.StopAsync(_ct); }
    }

    [Fact]
    public async Task SharedCoordinatorCancellationKeepsTheCompletedCandidateInTheSavedResult()
    {
        using var f = await QueuedVideoFixture.Create(this); var submission = await f.Capture(3);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Adapter.BeforeDownload = async () => { if (f.Adapter.Downloads == 2) { entered.TrySetResult(); await release.Task; } };
        using var coordinator = new AiJobCoordinator(f.Jobs, f.Settings, [f.Worker], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        await coordinator.StartAsync(_ct);
        try
        {
            await coordinator.EnqueueAsync(submission, _ct); await entered.Task.WaitAsync(TimeSpan.FromSeconds(8), _ct);
            await coordinator.CancelAsync(submission.Id, _ct); release.TrySetResult();
            await Until(() => coordinator.View.Jobs.Any(j => j.Id == submission.Id && j.State == AiJobState.Cancelled));
            var job = coordinator.View.Jobs.Single();
            Assert.False(job.LocksTarget); Assert.False(job.RemoteUnconfirmed);
            var saved = (await f.Jobs.ReadArtifactAsync<AiVideoJobResult>(submission.Id, AiJobArtifact.Result, _ct))!;
            Assert.Equal(submission.Batch!.Candidates.Select(c => c.Id), saved.Candidates.Select(c => c.TakeId));
            Assert.Equal(3, (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Count);
        }
        finally { release.TrySetResult(); await coordinator.StopAsync(_ct); }
    }

    [Fact]
    public async Task SharedCoordinatorRecoversSavedVideoOutputOnRestartWithoutResubmitting()
    {
        using var f = await QueuedVideoFixture.Create(this); var submission = await f.Capture(); var context = await f.Claim(submission);
        f.Adapter.FailTransfer = true;
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, _ct));
        f.Adapter.FailTransfer = false;
        using var coordinator = new AiJobCoordinator(f.Jobs, f.Settings, [f.Worker], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        await coordinator.StartAsync(_ct);
        try
        {
            await Until(() => coordinator.View.Jobs.Any(j => j.Id == submission.Id && j.State == AiJobState.Completed));
            Assert.Single(f.Graphs); Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        }
        finally { await coordinator.StopAsync(_ct); }
    }

    [Fact]
    public async Task AShotRemovedDuringVideoGenerationReceivesItsCompletedTakeInTrash()
    {
        using var f = await QueuedVideoFixture.Create(this); var submission = await f.Capture(); var context = await f.Claim(submission);
        f.Adapter.BeforeDownload = async () =>
        {
            var d = await f.Shots.LoadAsync(f.Project.Id, _ct); await f.Shots.SaveAsync(f.Project.Id, [], d.Revision, ct: _ct);
        };
        await f.Worker.ExecuteAsync(context, submission.Snapshot, _ct);
        var saved = await f.Shots.LoadAsync(f.Project.Id, _ct); Assert.Empty(saved.Shots); Assert.Empty(saved.Takes);
        var trash = Assert.Single(saved.Trash); Assert.Equal(submission.Batch!.Candidates[0].Id, trash.Take!.Id);
        Assert.Equal(f.Shot.Id, trash.Owner!.Id); Assert.Single(saved.TakePublications);
        var restored = await f.Shots.RestoreAsync(f.Project.Id, [trash.Id], saved.Revision, _ct);
        Assert.Equal(f.Shot.Id, Assert.Single(restored.Shots).Id); Assert.Equal(trash.Take.Id, Assert.Single(restored.Takes).Id);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReelQueuePublishesWithoutShotsAndRecoversSaveFailureWithoutRegeneration(bool environment)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var submission = await f.CaptureReel(environment: environment); var context = await f.Claim(submission);
        var captured = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var before = await f.Shots.LoadAsync(f.Project.Id, _ct);
        var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
        var draft = library.ReelDrafts.Single().Copy(); draft.Name = "Later draft edit";
        await f.Assets.SaveDraftAsync(f.Project.Id, draft, draft.Revision, _ct);
        FileStream? locked = null;
        f.Adapter.BeforeDownload = () => {
            locked = new(Path.Combine(f.ProjectDirectory, "assets.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.CompletedTask;
        };
        try {
            var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, _ct));
            Assert.Equal(AiJobRecovery.RetryOutput, failure.Recovery);
        } finally { locked?.Dispose(); }
        Assert.Single(f.Graphs); Assert.Equal(1, f.Adapter.Downloads); Assert.Equal(1, f.ReelMedia.Imports);
        f.NoNetwork = true;
        Assert.Equal(1, (await f.Worker.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, _ct)).CompletedCandidates);
        library = await f.Assets.LoadAsync(f.Project.Id, _ct); var reel = Assert.Single(library.Reels);
        Assert.Equal(captured.Snapshot.Prompt, reel.Generation!.Recipe.Prompt);
        Assert.Equal(captured.Snapshot.Reel!.Recipe.Name, reel.Name);
        Assert.Equal("Later draft edit", library.ReelDrafts.Single().Name);
        Assert.Equal(before.Revision, (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision);
        Assert.Empty((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        await f.Assets.TrashReelAsync(f.Project.Id, reel.Id, library.Revision, _ct);
        Assert.Equal(1, (await f.Worker.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, _ct)).CompletedCandidates);
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        Assert.Equal(1, f.ReelMedia.Imports);
        var root = await f.Assets.RunDirectoryAsync(f.Project.Id, captured.BatchId, _ct);
        await File.WriteAllTextAsync(Path.Combine(root, "inputs", captured.Inputs[0].FileName), "damaged", _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => AiVideoJobPolicy.ValidatePreparedFilesAsync(captured, root, _ct));
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ReelKeyframeArchivePolicyIsCapturedAndPublicationFailureDoesNotRegenerate(bool keepFrames)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var submission = await f.CaptureReel(environment: true, keepFrames: keepFrames); var context = await f.Claim(submission);
        var captured = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(keepFrames, captured.Snapshot.OutputPolicy!.SaveLosslessFrames);
        Assert.Equal(keepFrames, captured.Snapshot.Reel!.Recipe.SaveLosslessFrames);
        f.ReelMedia.FailArchive = true;
        if (keepFrames) {
            await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, _ct));
            f.ReelMedia.FailArchive = false; f.NoNetwork = true;
            await f.Worker.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, _ct);
        } else await f.Worker.ExecuteAsync(context, submission.Snapshot, _ct);
        Assert.Single(f.Graphs); Assert.Equal(1, f.Adapter.Downloads); Assert.Equal(1, f.ReelMedia.Imports);
        Assert.Single((await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels);
        Assert.Equal(keepFrames ? 2 : 0, f.ReelMedia.ArchiveAttempts);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReelCompletedAfterCharacterDeletionRemainsRecoverable(bool environment)
    {
        using var f = await QueuedVideoFixture.Create(this);
        var submission = await f.CaptureReel(environment: environment); var context = await f.Claim(submission);
        f.Adapter.BeforeDownload = async () => {
            var library = await f.Assets.LoadAsync(f.Project.Id, _ct);
            await f.Assets.DeleteAssetAsync(f.Project.Id, library.Assets.Single().Id, library.Revision, _ct);
        };
        Assert.Equal(1, (await f.Worker.ExecuteAsync(context, submission.Snapshot, _ct)).CompletedCandidates);
        var saved = await f.Assets.LoadAsync(f.Project.Id, _ct);
        Assert.Empty(saved.Assets); Assert.Empty(saved.Reels); var deleted = Assert.Single(saved.ReelTrash);
        saved = await f.Assets.RestoreReelAsync(f.Project.Id, deleted.Reel.Id, saved.Revision, _ct);
        Assert.Equal(deleted.Reel.Media.Id, Assert.Single(saved.Reels).Media.Id);
        Assert.Equal(deleted.Owner.Id, Assert.Single(saved.Assets).Id);
        f.NoNetwork = true;
        Assert.Equal(1, (await f.Worker.RecoverAsync(f.Context(context.Job, true), submission.Snapshot, _ct)).CompletedCandidates);
        Assert.Single(f.Graphs);
    }

    private sealed class QueuedVideoFixture : IDisposable
    {
        private readonly ShotTests _owner; private readonly ScriptedHttpHandler _http;
        public ProjectInfo Project = null!; public IShotStore Shots = null!; public Shot Shot = null!;
        public FileAssetStore Assets = null!;
        public AiVideoJobCapture CaptureService = null!; public string ProjectDirectory = null!;
        public FakeProjectAiPreferencesStore Preferences { get; } = new();
        // Take refinement is hidden in the app; these fixtures turn it on to keep covering it.
        public FakeAiSettingsStore Settings { get; } = new() { Value = new() { ComfyUrl = "http://video.test:8188", H3 = new() { LatentUpscaler = "mock-h3-3d.safetensors", TakeRefinement = true } } };
        public FileAiJobStore Jobs { get; }
        public AiVideoJobHandler Worker { get; private set; } = null!;
        public MockVideoGenerator Generator { get; private set; } = null!;
        public VideoAdapter Adapter { get; } = new();
        public List<JsonElement> Graphs { get; } = [];
        public List<AiJobProgress> Progress { get; } = [];
        public bool NoNetwork, LoseReceipt, FailWorkflow, HoldExecution, RemoteCancelled;
        public int CancelRequests;
        public TaskCompletionSource Submitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<string, bool>? IncludeOutput;
        private readonly Dictionary<string, string> _history = [];
        private QueuedVideoFixture(ShotTests owner)
        {
            _owner = owner; Jobs = new(Path.Combine(owner._root, "queue"), TimeProvider.System);
            _http = new(async (request, ct) =>
            {
                if (NoNetwork) throw new InvalidOperationException("Publication recovery must not contact ComfyUI");
                Assert.Equal("video.test", request.RequestUri!.Host);
                if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/prompt")
                {
                    Graphs.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone());
                    if (LoseReceipt) throw new HttpRequestException("Lost submission receipt");
                    var id = Guid.NewGuid().ToString("D");
                    _history[id] = JsonSerializer.Serialize(new Dictionary<string, object>
                    { [id] = ComfyBatchHistory.Job(Graphs[^1], IncludeOutput, FailWorkflow) });
                    Submitted.TrySetResult();
                    return Json(JsonSerializer.Serialize(new { prompt_id = id }));
                }
                if (request.RequestUri.AbsolutePath.StartsWith("/history/")) return Json(HoldExecution && !RemoteCancelled ? "{}" : _history[request.RequestUri.Segments[^1]]);
                if (request.RequestUri.AbsolutePath == "/queue") return Json(HoldExecution && !RemoteCancelled
                    ? JsonSerializer.Serialize(new { queue_running = _history.Keys.Select(id => new object[] { 0, id }).ToArray(), queue_pending = Array.Empty<object>() })
                    : "{\"queue_running\":[],\"queue_pending\":[]}");
                if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.StartsWith("/api/jobs/", StringComparison.Ordinal) && request.RequestUri.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))
                { CancelRequests++; RemoteCancelled = true; return Json("{}"); }
                if (request.RequestUri.AbsolutePath == "/history") return Json("{}");
                throw new InvalidOperationException(request.RequestUri.ToString());
            });
        }
        public static async Task<QueuedVideoFixture> Create(ShotTests owner, int steps = 20)
        {
            var result = new QueuedVideoFixture(owner); var f = owner.Fixture(); var a = ApprovedShot(f.Project.Id);
            result.Project = f.Project; result.Shots = f.Shots; result.Shot = a.Shot; result.Assets = f.Assets;
            result.Shot.Turbo = steps != 20; result.Shot.TurboSteps = steps == 8 ? 8 : 4;
            result.ProjectDirectory = await f.Files.DirectoryAsync(f.Project.Id, owner._ct);
            await f.Shots.SaveAsync(f.Project.Id, [result.Shot], 0, ct: owner._ct);
            var generator = result.Generator = new MockVideoGenerator(f.Assets, f.Shots);
            result.CaptureService = new(f.Shots, a.Scripts, f.Assets, result.Settings, generator,
                new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, result.Preferences, jobs: result.Jobs);
            result.Worker = new(f.Shots, result.Generator, result.Adapter, new TestHttpFactory(result._http), new(TestComfy.Monitor()), TimeProvider.System);
            return result;
        }
        public ReelTestMedia ReelMedia { get; } = new();
        public async Task<AiJobSubmission> CaptureReel(IReadOnlyList<LoraSelection>? loras = null, bool environment = false, bool? keepFrames = null, bool existingRecording = false,
            VideoResolution? resolution = null)
        {
            var ct = _owner._ct;
            var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = environment ? "Study" : "Riley", Category = environment ? AssetCategory.Environment : AssetCategory.Character };
            var library = await Assets.SaveAsync(new() { ProjectId = Project.Id, Assets = [asset] }, 0, ct);
            using var png = new MemoryStream();
            using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(32, 32))
                image.Save(png, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            png.Position = 0;
            library = await Assets.AddImageAsync(Project.Id, asset.Id, png, new("face.png", [], AssetImageOrigin.Imported), library.Revision, ct);
            var draft = environment ? ReferenceReels.NewDraft(asset) : ReferenceReelTests.Recipe(ReelFraming.ThreeAngles);
            if (keepFrames is { } keep) draft.SaveLosslessFrames = keep;
            if (resolution is { } selectedResolution) VideoResolutions.Select(draft, selectedResolution);
            draft.Loras = loras;
            draft.AssetId = asset.Id; draft.Images = [new() { AssetId = asset.Id, MediaId = library.Assets.Single().Images.Single().Id, InferUsage = true }];
            if (existingRecording) { draft.VoiceMode = ReelVoiceMode.ExistingRecording; draft.Voice = new() { AssetId = asset.Id, VoiceId = Guid.NewGuid(), Speaker = draft.Speaker, Start = 1, Duration = 4 }; }
            var pair = ReferenceReels.Preset(draft); draft.Prompt = pair.Prompt; draft.UseGuidance = pair.UseGuidance;
            draft = await Assets.SaveDraftAsync(Project.Id, draft, 0, ct);
            Worker = new(Shots, Generator, Adapter, new TestHttpFactory(_http), new(TestComfy.Monitor()), TimeProvider.System,
                new ReelVideoPublication(Assets, Assets, ReelMedia));
            var capture = new AiReelCapture(Assets, Assets, Settings, Generator, new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(Project) }, Preferences);
            return await capture.CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), Project.Id, draft.Id, draft.Revision, 1, ct);
        }
        public async Task<AiJobSubmission> Capture(int count = 1, IReadOnlyList<ShotReferenceGuidance>? guidance = null, IReadOnlyList<ShotAppearanceContext>? appearances = null) => await CaptureService.CaptureAsync(Guid.NewGuid(), Guid.NewGuid(), Project.Id, Shot,
            (await Shots.LoadAsync(Project.Id, _owner._ct)).Revision, count, 52, guidance, appearances, _owner._ct);
        public async Task<AiJobContext> Claim(AiJobSubmission request)
        { await Jobs.EnqueueAsync(request, _owner._ct); return Context((await Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, _owner._ct))!, false); }
        public AiJobContext Context(AiJobHeader job, bool recovering) => new(job, recovering, Jobs, TimeProvider.System, (_, p) => Progress.Add(p), _ => { }, _owner._ct);
        private static HttpResponseMessage Json(string data) => new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
        public void Dispose() => _http.Dispose();
    }
    private sealed class ReelTestMedia : lumibelle.Services.Production.IReferenceVideoStore
    {
        public int Imports;
        public int ArchiveAttempts; public bool FailArchive;
        public Task PublishArchiveAsync(Guid project, ReferenceVideoMedia media, ShotTake take, string sourceDirectory, CancellationToken ct = default)
        { ArchiveAttempts++; return FailArchive ? Task.FromException(new IOException("Interrupted archive publication")) : Task.CompletedTask; }
        public Task<ReferenceVideoMedia> ImportAsync(Guid project, Stream content, string name, H3Settings settings, CancellationToken ct = default)
        { Imports++; return Task.FromResult(new ReferenceVideoMedia(Guid.NewGuid(), new('A', 64), 3, 640, 640, 124, 24, 124 / 24d, true)); }
        public Task<ReferenceVideoMedia> CopyTakeAsync(Guid p, Guid t, H3Settings s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AssetMedia?> OpenAsync(Guid p, Guid m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateAsync(Guid p, IEnumerable<ShotVideoBinding> b, CancellationToken ct = default) => Task.CompletedTask;
        public Task PrepareAsync(Guid p, Shot s, string d, List<PreparedVideoInput> i, H3Settings h, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class VideoAdapter : IComfyVideoJobAdapter
    {
        public int Downloads; public bool FailTransfer, Missing;
        public List<VideoCandidate> Downloaded { get; } = [];
        public Func<Task>? BeforeDownload;
        public ComfyExecutionOptions Options => ComfyH3Video.MonitorOptions;
        public Task ValidateAsync(AiVideoJobRequest request, CancellationToken ct) => Missing ? Task.FromException(new WorkspaceStoreException("Missing captured H3 weights")) : Task.CompletedTask;
        public Task<Func<string, object>> PrepareWorkflowAsync(AiVideoJobRequest request, AiBatchCandidate candidate, string directory, CancellationToken ct) =>
            Task.FromResult<Func<string, object>>(id => request.Refinement is { } refinement ? ComfyH3Video.BuildRefinementWorkflow(request.Snapshot, refinement, candidate.Seed, id, Guid.NewGuid().ToString("N")) : ComfyH3Video.BuildWorkflow(request.Snapshot, candidate.Seed, id, request.Inputs.Select(i => new PreparedVideoInput(i.FileName, i.Audio)).ToArray()));
        public async Task<ShotTake> DownloadAsync(AiVideoJobRequest request, VideoCandidate c, string directory, Func<string, Task> progress, CancellationToken ct)
        {
            Downloaded.Add(c);
            Downloads++; if (FailTransfer) throw new IOException("Interrupted video transfer");
            if (BeforeDownload is not null) await BeforeDownload();
            await progress("Archiving lossless WebP · 39/39 frames");
            Directory.CreateDirectory(directory); await File.WriteAllBytesAsync(Path.Combine(directory, "video.mp4"), [1, 2, 3], ct);
            var s = request.Snapshot; var frames = new List<ShotFrame>();
            for (var i = 0; i < (request.Refinement is not null || s.OutputPolicy?.SaveLosslessFrames != false ? s.FrameCount : 0); i++) frames.Add(new(i, LosslessFrameArchive.FileName(i / LosslessFrameArchive.SegmentFrames), 1));
            foreach (var file in frames.Select(f => f.FileName).Distinct()) await File.WriteAllBytesAsync(Path.Combine(directory, file), [1], ct);
            var package = s.CaptureRefinementData || request.Refinement is not null ? await MockRefinementPackage.WriteAsync(directory, s, request.Refinement, ct) : null;
            return new() { Id = c.TakeId, RunId = request.BatchId, ShotId = s.Shot.Id, Candidate = c.Number, Seed = c.Seed, Snapshot = ShotCopy.Of(s), Refinement = request.Refinement, RefinementPackage = package,
                CreatedUtc = DateTimeOffset.UtcNow, Width = H3PreviewUpscaling.OutputSize(s, request.Refinement).Width, Height = H3PreviewUpscaling.OutputSize(s, request.Refinement).Height, Directory = c.TakeId.ToString("D"), Frames = frames, Bytes = 4 + (package?.Bytes ?? 0) };
        }
    }
}
