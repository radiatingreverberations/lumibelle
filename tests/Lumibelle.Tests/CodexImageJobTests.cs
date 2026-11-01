using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodexRefinementKeepsNativeImagesAndPublishesOnlyAfterSuccessfulTurn(bool failTurn)
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.CodexImages, false); var ct = TestContext.Current.CancellationToken;
        var mock = new MockCodexTransport { NativeImageCount = 2, FailTurn = failTurn };
        await using var client = new CodexClient(mock, TimeProvider.System);
        f.Settings.Value = f.Settings.Value with { Codex = new() { Enabled = true, ImageModel = "mock-codex" } };
        var capture = new AiImageJobCapture(f.Settings, new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, f.Assets, client);
        var request = await capture.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), f.AssetId, new() { ProjectId = f.Project.Id, Workflow = ImageWorkflow.CodexImages, Prompt = "Create", AspectRatio = "1:1" }, ct);
        await f.Jobs.EnqueueAsync(request, ct); var job = (await f.Jobs.ClaimNextAsync(AiBackend.Codex, 1, ct))!;
        var context = f.Context(job, false); var worker = new AiImageJobHandler(f.Assets, null!, null!, null!, TimeProvider.System, client);
        if (failTurn) await Assert.ThrowsAsync<AiGenerationException>(() => worker.ExecuteAsync(context, request.Snapshot, ct));
        else Assert.Equal(1, (await worker.ExecuteAsync(context, request.Snapshot, ct)).CompletedCandidates);
        var operation = "candidate/" + job.Batch!.Candidates[0].Id.ToString("D") + "/native-image";
        var first = await context.ReadOperationAsync<CodexImageOutput>(operation + "/1", AiOperationArtifact.Output, ct);
        var final = await context.ReadOperationAsync<CodexImageOutput>(operation + "/2", AiOperationArtifact.Output, ct);
        Assert.Equal(80, ImageInspector.Inspect(first!.Image!).Width);
        Assert.Equal(81, ImageInspector.Inspect(final!.Image!).Width);
        var images = (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Where(i => i.Generation is not null).ToArray();
        if (failTurn) Assert.Empty(images);
        else Assert.Equal(81, Assert.Single(images).Width);
        Assert.Equal(1, mock.Turns);
    }

    [Fact]
    public async Task FailedCodexImageSavesEntireResponseBeforeTheStreamingCheckpointInterval()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.CodexImages, false); var ct = TestContext.Current.CancellationToken;
        var mock = new MockCodexTransport { OmitImage = true, TextChunks = ["Unable", " to access", " the image generation tool."] };
        await using var client = new CodexClient(mock, TimeProvider.System);
        f.Settings.Value = f.Settings.Value with { Codex = new() { Enabled = true, ImageModel = "mock-codex" } };
        var capture = new AiImageJobCapture(f.Settings, new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, f.Assets, client);
        var request = await capture.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), f.AssetId, new() { ProjectId = f.Project.Id, Workflow = ImageWorkflow.CodexImages, Prompt = "Create", AspectRatio = "1:1" }, ct);
        await f.Jobs.EnqueueAsync(request, ct); var job = (await f.Jobs.ClaimNextAsync(AiBackend.Codex, 1, ct))!;
        var context = f.Context(job, false);
        var worker = new AiImageJobHandler(f.Assets, null!, null!, null!, TimeProvider.System, client);
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => worker.ExecuteAsync(context, request.Snapshot, ct));
        Assert.Contains("without an image", error.Message);
        var result = await context.ReadAsync<AiImageJobResult>(AiJobArtifact.Result, ct);
        Assert.Equal("Unable to access the image generation tool.", result!.Raw);
        Assert.Empty(result.Candidates); Assert.Equal(1, mock.Turns);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CodexCandidatesPreserveCapturedContextDimensionsCropsAndTrash(bool edit)
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.CodexImages, edit); var ct = TestContext.Current.CancellationToken;
        var mock = new MockCodexTransport(); await using var client = new CodexClient(mock, TimeProvider.System);
        f.Settings.Value = f.Settings.Value with { Codex = new() { Enabled = true, ImageModel = "mock-codex", ImageEffort = "high" } };
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var capture = new AiImageJobCapture(f.Settings, projects, f.Assets, client); var id = Guid.NewGuid(); var tab = Guid.NewGuid();
        var request = edit ? await capture.EditAsync(id, tab, f.AssetId, new() { ProjectId = f.Project.Id, Workflow = ImageWorkflow.CodexImages,
            SourceAssetId = f.AssetId, SourceImageId = f.References[0].ImageId, Prompt = "Keep ÖPPET and mouse_token", AspectRatio = "1:1", Count = 2,
            SourceCrop = new() { X = .25, Width = .5 }, ReferenceCrops = [new(f.References[1], new() { Height = .5 })] }, f.References, ct)
            : await capture.CreateAsync(id, tab, f.AssetId, new() { ProjectId = f.Project.Id, Workflow = ImageWorkflow.CodexImages, Prompt = "Keep ÖPPET and mouse_token", AspectRatio = "1:1", Count = 2 }, ct);
        var snapshot = request.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(AiBackend.Codex, request.Backend); Assert.Empty(snapshot.AppliedLoras); Assert.Equal("high", snapshot.Codex!.Effort);
        if (edit) Assert.Equal(new[] { (40, 40), (20, 30) }, snapshot.Inputs.Select(i => { var info = ImageInspector.Inspect(i.Png); return (info.Width, info.Height); }));
        await f.Jobs.EnqueueAsync(request, ct); var job = (await f.Jobs.ClaimNextAsync(AiBackend.Codex, 1, ct))!;
        var worker = new AiImageJobHandler(f.Assets, null!, null!, null!, TimeProvider.System, client);
        var context = f.Context(job, false);
        f.Settings.Value = f.Settings.Value with { Codex = f.Settings.Value.Codex with { ImageModel = "changed", ImageEffort = "low" } };
        Assert.Equal(2, (await worker.ExecuteAsync(context, request.Snapshot, ct)).CompletedCandidates);
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        var images = library.Assets[0].Images.Where(i => i.Generation?.AiJobId == id).ToArray(); Assert.Equal(2, images.Length);
        Assert.All(images, i =>
        {
            Assert.Equal((80, 40), (i.Width, i.Height)); Assert.Equal("1:1", i.Generation!.AspectRatio);
            Assert.Equal("high", i.Generation.Codex!.Effort); Assert.Equal("Returned revised prompt", i.Generation.Codex.RevisedPrompt);
            Assert.NotNull(i.Generation.Codex.Timing?.CompletedUtc);
            Assert.Single(i.Generation.Codex.Timing!.ImageCalls);
            Assert.Empty(i.Generation.DiffusionModel); Assert.Equal(0, i.Generation.Steps);
            if (edit) { Assert.Equal(snapshot.Edit!.SourceCrop, i.Generation.Edit!.SourceCrop); Assert.Equal(f.References, i.Generation.Edit.References); }
        });
        var trash = await f.Assets.DeleteImageAsync(f.Project.Id, f.AssetId, images[0].Id, library.Revision, ct);
        Assert.Equal(2, (await worker.RecoverAsync(await f.Recover(context), request.Snapshot, ct)).CompletedCandidates); Assert.Equal(2, mock.Turns);
        // Appending uses the stored snapshot, even after model settings changed.
        await f.Jobs.UpdateAsync(id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        var appended = await f.Jobs.ExtendBatchAsync(id, Guid.NewGuid(), tab, ct);
        var more = (await f.Jobs.ClaimNextAsync(AiBackend.Codex, 1, ct))!;
        Assert.Equal(1, (await worker.ExecuteAsync(f.Context(more, false), await f.Jobs.ReadSnapshotAsync(more.Id, ct), ct)).CompletedCandidates);
        Assert.Equal(3, mock.Threads); Assert.Equal(3, mock.Turns);
        Assert.All(mock.Inputs, p => Assert.Equal("high", p.GetProperty("effort").GetString()));
    }
    [Fact]
    public async Task CodexPublicationFailureRecoversWithoutAnotherRequest()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.CodexImages, false); var ct = TestContext.Current.CancellationToken;
        var mock = new MockCodexTransport(); await using var client = new CodexClient(mock, TimeProvider.System);
        f.Settings.Value = f.Settings.Value with { Codex = new() { Enabled = true, ImageModel = "mock-codex" } };
        var capture = new AiImageJobCapture(f.Settings, new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, f.Assets, client);
        var request = await capture.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), f.AssetId, new() { ProjectId = f.Project.Id, Workflow = ImageWorkflow.CodexImages, Prompt = "Create", AspectRatio = "1:1" }, ct);
        await f.Jobs.EnqueueAsync(request, ct); var job = (await f.Jobs.ClaimNextAsync(AiBackend.Codex, 1, ct))!; var context = f.Context(job, false);
        FileStream? locked = null;
        mock.Notification += e => { if (e.GetProperty("method").GetString() == "item/completed") locked = TestProjectDatabase.HoldReadOnly(Path.Combine(_directory, "projects", f.Project.Id.ToString("D"))); };
        var worker = new AiImageJobHandler(f.Assets, null!, null!, null!, TimeProvider.System, client);
        try
        {
            var failed = await Assert.ThrowsAsync<AiJobRecoveryException>(() => worker.ExecuteAsync(context, request.Snapshot, ct));
            Assert.Equal(AiJobRecovery.RetryOutput, failed.Recovery);
        }
        finally { locked?.Dispose(); }
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        await f.Assets.SaveAsync(library with { Assets = library.Assets.Select(a => a with { Description = "Edited during save" }).ToList() }, library.Revision, ct);
        Assert.Equal(1, (await worker.RecoverAsync(await f.Recover(context), request.Snapshot, ct)).CompletedCandidates);
        Assert.Equal(1, mock.Turns);
        library = await f.Assets.LoadAsync(f.Project.Id, ct);
        Assert.Equal("Edited during save", library.Assets[0].Description);
        Assert.Equal(AiBackend.Codex, library.Assets[0].Images.Single(i => i.Generation is not null).Generation!.Provider);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task CodexNativeOutputAndCompletedReceiptRecoverBeforeStaging(int nativeImageCount)
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.CodexImages, false); var ct = TestContext.Current.CancellationToken;
        var mock = new MockCodexTransport(); await using var client = new CodexClient(mock, TimeProvider.System);
        f.Settings.Value = f.Settings.Value with { Codex = new() { Enabled = true, ImageModel = "mock-codex" } };
        var capture = new AiImageJobCapture(f.Settings, new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, f.Assets, client);
        var request = await capture.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), f.AssetId, new() { ProjectId = f.Project.Id, Workflow = ImageWorkflow.CodexImages, Prompt = "Create", AspectRatio = "1:1" }, ct);
        await f.Jobs.EnqueueAsync(request, ct); var job = (await f.Jobs.ClaimNextAsync(AiBackend.Codex, 1, ct))!; var context = f.Context(job, false);
        var operation = "candidate/" + job.Batch!.Candidates[0].Id.ToString("D");
        await context.SaveOperationAsync(operation, AiOperationArtifact.Request, new CodexReceipt(), ct);
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var timing = nativeImageCount == 0 ? null : new CodexImageTiming(start, [new("one", start.AddSeconds(5), start.AddSeconds(28))], start.AddSeconds(30));
        await context.SaveOperationAsync(operation + "/complete", AiOperationArtifact.Request, new CodexReceipt("thread", "turn", true, nativeImageCount, timing), ct);
        await context.SaveOperationAsync(operation + "/native-image" + (nativeImageCount > 0 ? "/" + nativeImageCount : ""), AiOperationArtifact.Output, new CodexImageOutput(Image: Png(80, 40)), ct);
        var worker = new AiImageJobHandler(f.Assets, null!, null!, null!, TimeProvider.System, client);
        Assert.Equal(1, (await worker.RecoverAsync(await f.Recover(context), request.Snapshot, ct)).CompletedCandidates);
        Assert.Equal(0, mock.Turns);
        var saved = (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Single(i => i.Generation is not null).Generation!.Codex!.Timing;
        Assert.Equal(timing?.CompletedUtc, saved?.CompletedUtc);
        Assert.Equal(timing?.DurationsAt(start), saved?.DurationsAt(start));
    }
    [Fact]
    public async Task InterruptedCodexImageNeverRegeneratesDuringRecovery()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.CodexImages, false); var ct = TestContext.Current.CancellationToken;
        var mock = new MockCodexTransport(); await using var client = new CodexClient(mock, TimeProvider.System);
        f.Settings.Value = f.Settings.Value with { Codex = new() { Enabled = true, ImageModel = "mock-codex" } };
        var capture = new AiImageJobCapture(f.Settings, new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) }, f.Assets, client);
        var request = await capture.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), f.AssetId, new() { ProjectId = f.Project.Id, Workflow = ImageWorkflow.CodexImages, Prompt = "Create", AspectRatio = "1:1" }, ct);
        await f.Jobs.EnqueueAsync(request, ct); var job = (await f.Jobs.ClaimNextAsync(AiBackend.Codex, 1, ct))!; var context = f.Context(job, false);
        await context.SaveOperationAsync("candidate/" + job.Batch!.Candidates[0].Id.ToString("D"), AiOperationArtifact.Request, new CodexReceipt("uncertain-thread"), ct);
        var worker = new AiImageJobHandler(f.Assets, null!, null!, null!, TimeProvider.System, client);
        var outcome = await worker.RecoverAsync(await f.Recover(context), request.Snapshot, ct);
        Assert.Equal(AiJobRecovery.GenerateAgain, outcome.Recovery); Assert.Equal(0, mock.Turns);
    }
}
