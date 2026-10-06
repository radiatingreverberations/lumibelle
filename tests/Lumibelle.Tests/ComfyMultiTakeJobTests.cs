using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Theory]
    [InlineData(ImageWorkflow.Krea2, false, 2)] [InlineData(ImageWorkflow.Krea2, false, 4)]
    [InlineData(ImageWorkflow.Krea2, true, 2)] [InlineData(ImageWorkflow.Krea2, true, 4)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, false, 2)] [InlineData(ImageWorkflow.Flux2Klein9bKv, false, 4)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, true, 2)] [InlineData(ImageWorkflow.Flux2Klein9bKv, true, 4)]
    public async Task ComfyMultiTakeImagesSubmitOnceAndRouteEveryCandidate(ImageWorkflow workflow, bool editing, int count)
    {
        using var f = await ImageJobFixture.Create(this, workflow, editing);
        var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(count); var context = await f.Claim(request);
        var outcome = await f.Worker.ExecuteAsync(context, request.Snapshot, ct);
        Assert.Equal(count, outcome.CompletedCandidates); Assert.Equal(1, f.Posts); Assert.Equal(count, f.Views);
        var nodes = Assert.Single(f.Graphs).GetProperty("prompt");
        Assert.Single(nodes.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "CLIPLoader");
        Assert.Equal(count, nodes.EnumerateObject().Count(n => n.Value.GetProperty("class_type").GetString() is "KSampler" or "SamplerCustomAdvanced"));
        if (workflow == ImageWorkflow.Krea2 && editing)
        {
            Assert.Equal("Krea2EditGroundedEncode", nodes.GetProperty("9").GetProperty("class_type").GetString());
            Assert.Equal("Krea2EditGroundedEncode", nodes.GetProperty("10").GetProperty("class_type").GetString());
            Assert.Equal(2, nodes.EnumerateObject().Count(n => n.Value.GetProperty("class_type").GetString() == "VAEEncode"));
        }
        else Assert.True(nodes.TryGetProperty("4", out _));
        var outputNode = f.Adapter.Output;
        var images = (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Where(i => i.Generation?.AiJobId == request.Id).ToArray();
        foreach (var c in request.Batch!.Candidates)
        {
            var image = Assert.Single(images, i => i.Id == c.Id);
            Assert.Equal(c.Seed, image.Generation!.Seed);
            Assert.Contains(f.DownloadQueries, q => q.Contains("filename=" + ComfyMultiTakeWorkflow.Node(c, outputNode) + ".png", StringComparison.Ordinal));
        }
        var remote = Assert.Single((await context.ExecutionAsync(ct)).Submissions);
        Assert.Equal(ComfyMultiTakeWorkflow.Operation, remote.Operation); Assert.Equal(AiRemoteState.Finished, remote.State);
    }

    [Fact]
    public async Task ComfyMultiTakeImageAppendRemainsASeparateSingleOutputPrompt()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(2); var context = await f.Claim(request);
        f.OnDownload = async () =>
        {
            f.OnDownload = null;
            await f.Jobs.ExtendBatchAsync(request.Id, Guid.NewGuid(), request.OriginTabId, ct);
        };
        Assert.Equal(3, (await f.Worker.ExecuteAsync(context, request.Snapshot, ct)).CompletedCandidates);
        Assert.Equal(2, f.Posts);
        Assert.True(f.Graphs[0].GetProperty("prompt").TryGetProperty("take_1_9", out _));
        Assert.True(f.Graphs[0].GetProperty("prompt").TryGetProperty("take_2_9", out _));
        Assert.True(f.Graphs[1].GetProperty("prompt").TryGetProperty("9", out _));
        Assert.DoesNotContain(f.Graphs[1].GetProperty("prompt").EnumerateObject(), n => n.Name.StartsWith("take_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ComfyMultiTakeImageFailureRetainsCompletedOutputsEvenWhenTheyAreNotAPrefix()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(3); var context = await f.Claim(request);
        f.FailWorkflow = true; f.IncludeOutput = id => id.StartsWith("take_2_", StringComparison.Ordinal);
        await f.Jobs.ExtendBatchAsync(request.Id, Guid.NewGuid(), request.OriginTabId, ct);
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, ct));
        Assert.Equal(AiJobRecovery.GenerateAgain, error.Recovery);
        var result = (await context.ReadAsync<AiImageJobResult>(AiJobArtifact.Result, ct))!;
        Assert.Equal(request.Batch!.Candidates[1].Id, Assert.Single(result.Candidates).ImageId);
        Assert.Equal(1, f.Posts); Assert.Equal(1, f.Views);
        // The appended request is not submitted after a failed initial workflow.
        Assert.Single((await context.ExecutionAsync(ct)).Submissions);
        f.NoNetwork = true;
        var recovered = await f.Recover(context);
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.RecoverAsync(recovered, request.Snapshot, ct));
        Assert.Equal(1, f.Views); Assert.Equal(1, f.Posts);
    }

    [Fact]
    public async Task ComfyMultiTakeLegacyAcceptedCandidateIsRecoveredWithoutRegrouping()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(2); var context = await f.Claim(request);
        var captured = request.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        var first = request.Batch!.Candidates[0]; var operation = ComfyMultiTakeWorkflow.CandidateOperation(first);
        var clientId = Guid.NewGuid().ToString("D");
        var graph = JsonSerializer.SerializeToElement(f.Adapter.Build(captured, first, clientId), AtomicJsonFile.Options);
        await context.SaveOperationAsync(operation, AiOperationArtifact.Request,
            new ComfySavedOperation(captured.Settings.ComfyUrl, clientId, graph, f.Adapter.Options(captured)), ct);
        await context.BeginRemoteAsync(operation, captured.Settings.ComfyUrl, clientId);
        await context.AcceptRemoteAsync(operation, Guid.NewGuid().ToString("D"));
        await context.FinishRemoteAsync(operation);
        await context.SaveOperationAsync(operation, AiOperationArtifact.Output,
            JsonSerializer.SerializeToElement(ComfyBatchHistory.Job(graph)), ct);
        var recovered = await f.Recover(context);
        Assert.Equal(1, (await f.Worker.RecoverAsync(recovered, request.Snapshot, ct)).CompletedCandidates);
        Assert.Equal(0, f.Posts);
        Assert.Equal(2, (await f.Worker.ExecuteAsync(f.Context(recovered.Job, false), request.Snapshot, ct)).CompletedCandidates);
        Assert.Equal(1, f.Posts);
        Assert.True(Assert.Single(f.Graphs).GetProperty("prompt").TryGetProperty("9", out _));
        Assert.DoesNotContain((await recovered.ExecutionAsync(ct)).Submissions, r => r.Operation == ComfyMultiTakeWorkflow.Operation);
    }

    [Fact]
    public async Task ComfyMultiTakeImageRecoveryOfUntouchedBatchCannotSubmit()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(2); var context = await f.Claim(request);
        Assert.Equal(0, (await f.Worker.RecoverAsync(f.Context(context.Job, true), request.Snapshot, ct)).CompletedCandidates);
        Assert.Empty(f.Graphs);
        Assert.Equal(2, (await f.Worker.ExecuteAsync(context, request.Snapshot, ct)).CompletedCandidates);
        Assert.Single(f.Graphs);
    }

    [Fact]
    public async Task ComfyMultiTakeImageDownloadFailurePublishesLaterCompletedCandidates()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var ct = TestContext.Current.CancellationToken;
        var request = await f.Capture(3); var context = await f.Claim(request);
        f.FailDownloads = 1;
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, ct));
        Assert.Equal(AiJobRecovery.RetryOutput, error.Recovery);
        Assert.Equal(3, f.Views); Assert.Single(f.Graphs); Assert.Equal(1, f.Posts);
        var result = (await context.ReadAsync<AiImageJobResult>(AiJobArtifact.Result, ct))!;
        Assert.Equal(2, result.Candidates.Count);
        Assert.DoesNotContain(result.Candidates, c => c.CandidateId == request.Batch!.Candidates[0].Id);
        Assert.Contains(request.Batch!.Candidates[1].Id, result.Candidates.Select(c => c.CandidateId));
        Assert.Contains(request.Batch!.Candidates[2].Id, result.Candidates.Select(c => c.CandidateId));
    }
}

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellingMultiTakeRecoversAnOutOfOrderCompletedBranchWithoutResubmitting(bool failDownload)
    {
        using var f = await QueuedVideoFixture.Create(this);
        f.HoldExecution = true; f.FailWorkflow = true;
        f.IncludeOutput = id => id.StartsWith("take_2_", StringComparison.Ordinal);
        f.Adapter.FailTransfer = failDownload;
        var submission = await f.Capture(3);
        using var coordinator = new AiJobCoordinator(f.Jobs, f.Settings, [f.Worker], TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<AiJobCoordinator>.Instance);
        await coordinator.StartAsync(_ct);
        try
        {
            await coordinator.EnqueueAsync(submission, _ct);
            await f.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(8), _ct);
            await coordinator.CancelAsync(submission.Id, _ct);
            await Until(() => coordinator.View.Jobs.Any(j => j.Id == submission.Id && j.State == AiJobState.Cancelled && !j.RemoteUnconfirmed));
            Assert.Equal(1, f.CancelRequests);
            if (failDownload)
            {
                Assert.Equal(AiJobRecovery.RetryOutput, coordinator.View.Jobs.Single().Recovery);
                Assert.Contains("Recover completed takes", coordinator.View.Jobs.Single().Error);
                f.Adapter.FailTransfer = false;
                await coordinator.ResumeAsync(submission.Id, _ct);
                await UntilOutputRecovered(async () => (await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes.Count == 1);
                await Until(() => coordinator.View.Jobs.Single().State == AiJobState.Cancelled);
            }
            var document = await f.Shots.LoadAsync(f.Project.Id, _ct);
            Assert.Equal(submission.Batch!.Candidates[1].Id, Assert.Single(document.Takes).Id);
            Assert.Single(f.Graphs);
            Assert.True(coordinator.View.Jobs.Single().CancelRequested);
            Assert.Equal(AiJobRecovery.None, coordinator.View.Jobs.Single().Recovery);
            var result = (await f.Jobs.ReadArtifactAsync<AiVideoJobResult>(submission.Id, AiJobArtifact.Result, _ct))!;
            Assert.Equal(submission.Batch.Candidates[1].Id, Assert.Single(result.Candidates).TakeId);
            // Repeating retrieval is idempotent, even with ComfyUI offline once receipts are saved.
            f.NoNetwork = true;
            var priorLease = coordinator.View.Jobs.Single().LeaseId;
            await coordinator.ResumeAsync(submission.Id, _ct);
            Assert.NotEqual(priorLease, coordinator.View.Jobs.Single().LeaseId);
            await Until(() => coordinator.View.Jobs.Single().State == AiJobState.Cancelled);
            Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
            Assert.Single(f.Graphs);
        }
        finally { await coordinator.StopAsync(_ct); }
    }

    private async Task UntilOutputRecovered(Func<Task<bool>> ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await ready()) await Task.Delay(25, timeout.Token);
    }

    [Theory]
    [InlineData(20, 2)] [InlineData(20, 4)] [InlineData(4, 3)] [InlineData(8, 4)]
    public async Task ComfyMultiTakeVideoSharesConditioningAndRoutesOutputsByCandidate(int steps, int count)
    {
        using var f = await QueuedVideoFixture.Create(this, steps);
        var request = await f.Capture(count); var context = await f.Claim(request);
        Assert.Equal(count, (await f.Worker.ExecuteAsync(context, request.Snapshot, _ct)).CompletedCandidates);
        var workflow = Assert.Single(f.Graphs); var nodes = workflow.GetProperty("prompt");
        Assert.Single(nodes.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "MiniMaxH3ReferenceToVideo");
        var receipt = Assert.Single((await context.ExecutionAsync(_ct)).Submissions);
        Assert.Equal(ComfyMultiTakeWorkflow.Operation, receipt.Operation);
        Assert.Equal(count, f.Adapter.Downloaded.Count);
        foreach (var c in request.Batch!.Candidates)
        {
            var noise = nodes.GetProperty(ComfyMultiTakeWorkflow.Node(c, "7")).GetProperty("inputs");
            var sampler = nodes.GetProperty(ComfyMultiTakeWorkflow.Node(c, "10")).GetProperty("inputs");
            Assert.Equal(c.Seed, noise.GetProperty("noise_seed").GetInt64());
            Assert.Equal("5", sampler.GetProperty("latent_image")[0].GetString());
            var output = Assert.Single(f.Adapter.Downloaded, d => d.TakeId == c.Id);
            Assert.Equal(receipt.PromptId, output.PromptId); Assert.Equal(c.Id.ToString("D"), output.ClientId);
            var video = output.Output!.Value.GetProperty("outputs").GetProperty("14").GetProperty("videos")[0];
            Assert.Equal(ComfyMultiTakeWorkflow.Node(c, "14") + ".mp4", video.GetProperty("filename").GetString());
            Assert.DoesNotContain(output.Output.Value.GetProperty("outputs").EnumerateObject(), n => n.Name.StartsWith("take_", StringComparison.Ordinal));
        }
        var saved = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(request.Batch.Candidates.Select(c => c.Id), saved.Takes.Select(t => t.Id));
        Assert.Equal(request.Batch.Candidates.Select(c => c.Seed), saved.Takes.Select(t => t.Seed));
    }

    [Fact]
    public async Task ComfyMultiTakeVideoTransferRetryUsesTheSingleSavedPromptForAllTakes()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var request = await f.Capture(3); var context = await f.Claim(request);
        f.Adapter.FailTransfer = true;
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, _ct));
        Assert.Equal(AiJobRecovery.RetryOutput, error.Recovery); Assert.Single(f.Graphs);
        f.Adapter.FailTransfer = false;
        var recovered = f.Context(context.Job, true);
        Assert.Equal(3, (await f.Worker.RecoverAsync(recovered, request.Snapshot, _ct)).CompletedCandidates);
        Assert.Single(f.Graphs); Assert.Equal(6, f.Adapter.Downloads);
        Assert.Equal(3, (await f.Shots.LoadAsync(f.Project.Id, _ct)).TakePublications.Count);
        f.NoNetwork = true;
        Assert.Equal(3, (await f.Worker.RecoverAsync(recovered, request.Snapshot, _ct)).CompletedCandidates);
        Assert.Equal(6, f.Adapter.Downloads);
    }

    [Fact]
    public async Task ComfyMultiTakeVideoPartialFailurePublishesOnlyCompleteCandidateOutputSets()
    {
        using var f = await QueuedVideoFixture.Create(this);
        f.Shot.SaveLosslessFrames = true;
        var document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], document.Revision, ct: _ct);
        var request = await f.Capture(3); var context = await f.Claim(request);
        Assert.True(request.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Snapshot.OutputPolicy?.SaveLosslessFrames);
        f.FailWorkflow = true;
        // Candidate 1 has an MP4 but lacks its required archive; candidate 2 is complete.
        f.IncludeOutput = id => id == "take_1_14" || id.StartsWith("take_2_", StringComparison.Ordinal);
        var failure = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, _ct));
        Assert.Equal(AiJobRecovery.GenerateAgain, failure.Recovery);
        var saved = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(request.Batch!.Candidates[1].Id, Assert.Single(saved.Takes).Id);
        Assert.Equal(1, f.Adapter.Downloads); Assert.Single(f.Graphs);
        f.NoNetwork = true;
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.RecoverAsync(f.Context(context.Job, true), request.Snapshot, _ct));
        Assert.Equal(1, f.Adapter.Downloads); Assert.Single(f.Graphs);
    }

    [Fact]
    public async Task ComfyMultiTakeVideoLostReceiptNeverSubmitsASecondWorkflowDuringRecovery()
    {
        using var f = await QueuedVideoFixture.Create(this);
        var request = await f.Capture(3); var context = await f.Claim(request);
        f.LoseReceipt = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Worker.ExecuteAsync(context, request.Snapshot, _ct));
        Assert.Equal(ComfyMultiTakeWorkflow.Operation, Assert.Single((await context.ExecutionAsync(_ct)).Submissions).Operation);
        await Assert.ThrowsAsync<AiGenerationException>(() => f.Worker.RecoverAsync(f.Context(context.Job, true), request.Snapshot, _ct));
        Assert.Single(f.Graphs); Assert.Equal(0, f.Adapter.Downloads);
    }
}
