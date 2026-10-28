using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    private AssetBatchWorkflow BulkWorkflow(ImageJobFixture f, out AiJobCoordinator queue)
    {
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == f.Project.Id ? f.Project : null) };
        var files = new ProjectFiles(Options.Create(new ProjectStorageOptions { RootDirectory = "projects" }), new AssetEnvironment { ContentRootPath = _directory }, projects);
        var providers = new FakeProviders();
        queue = new(f.Jobs, f.Settings, [f.Worker, new BulkPromptCheckpointHandler()], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        var text = new AiTextJobCapture(f.Settings, projects, f.Assets, new PromptEnhancer(providers, f.Settings, f.Assets), new GuidanceAssistant(providers, f.Settings, f.Assets), providers: providers);
        return new(new(files), f.Assets, f.Settings, new(f.Settings, projects, f.Assets), text, f.Jobs, queue,
            new FakeReferenceImageGenerator(), new FakeReferenceImageEditor(), providers);
    }

    [Fact]
    public async Task BulkPlanUsesApprovedActiveReferencesAndCapturesLookDirection()
    {
        var asset = Asset("Juniper", "A small green robot") with { Looks = [new() { Name = "Raincoat", Description = "Yellow waterproof coat" }, new() { Name = "Old", Archived = true }] };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [asset] };
        var rows = AssetBatchPlan.Build(library, ImageWorkflow.Krea2);
        Assert.Equal(2, rows.Count); Assert.All(rows, r => Assert.True(r.MissingReference));
        Assert.Contains("Approve an identity", rows[1].Issue);
        var anchor = new AssetImage { Id = Guid.NewGuid(), FileName = "identity.png", ContentType = "image/png", Width = 64, Height = 64, IsReference = true };
        asset = asset with { Images = [anchor, anchor with { Id = Guid.NewGuid(), LookId = asset.Looks[1].Id }] };
        rows = AssetBatchPlan.Build(library with { Assets = [asset] }, ImageWorkflow.Krea2);
        Assert.False(rows[0].MissingReference); Assert.True(rows[1].MissingReference); Assert.Null(rows[1].Issue);
        Assert.Equal(anchor.Id, Assert.Single(rows[1].Item.References).Reference.ImageId);
        Assert.Contains("Yellow waterproof coat", rows[1].Item.Prompt);
    }

    [Fact]
    public async Task BulkCheckpointSurvivesRestartAndLostEnqueueAcknowledgementWithoutDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var row = AssetBatchPlan.Build(await f.Assets.LoadAsync(f.Project.Id, ct), ImageWorkflow.Krea2)[0];
        var doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, [row.Item], 2, "1:1", 1024, null, ct);
        var pending = doc.Runs[0].Pending!; Assert.Empty((await f.Jobs.ReadAsync(ct)).Jobs);
        // Simulate enqueue succeeding before the application receives its acknowledgement.
        await f.Jobs.EnqueueAsync(pending, ct);
        var restarted = BulkWorkflow(f, out var queue2); using var owned2 = queue2;
        await restarted.AdvanceAsync(f.Project.Id, ct); await restarted.AdvanceAsync(f.Project.Id, ct);
        Assert.Equal(pending.Id, Assert.Single((await f.Jobs.ReadAsync(ct)).Jobs).Id);
        f.Settings.Value = f.Settings.Value with { ComfyUrl = "http://changed.test" };
        var captured = pending.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal("http://comfy.test:8188", captured.Settings.ComfyUrl);
        await workflow.PauseAsync(f.Project.Id, doc.Runs[0].Id, ct);
        var context = await f.Claim(pending); await f.Worker.ExecuteAsync(context, pending.Snapshot, ct);
        // Candidates can be approved while the remaining request is still finishing.
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        var candidate = library.ImagePublications[0].ImageId;
        await restarted.ApproveAsync(f.Project.Id, doc.Runs[0].Id, row.Item.Id, candidate, ct);
        await f.Jobs.UpdateAsync(pending.Id, j => j with { State = AiJobState.Completed, LeaseId = null, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        doc = await restarted.AdvanceAsync(f.Project.Id, ct);
        Assert.Equal(AssetBatchState.Paused, doc.Runs[0].State); Assert.Null(doc.Runs[0].Pending);
        await restarted.ResumeAsync(f.Project.Id, doc.Runs[0].Id, ct);
        await restarted.AdvanceAsync(f.Project.Id, ct); doc = await restarted.AdvanceAsync(f.Project.Id, ct);
        Assert.Equal(AssetBatchState.Completed, doc.Runs[0].State);
        Assert.Equal(2, doc.Runs[0].Items[0].Candidates.Count); Assert.Equal(candidate, doc.Runs[0].Items[0].ApprovedImageId);
        Assert.Equal(1, f.Posts); Assert.True((await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Single(i => i.Id == candidate).IsReference);
        await restarted.GenerateAgainAsync(f.Project.Id, doc.Runs[0].Id, row.Item.Id, ct);
        doc = await restarted.AdvanceAsync(f.Project.Id, ct); var additional = doc.Runs[0].Pending!;
        var another = await f.Claim(additional); await f.Worker.ExecuteAsync(another, additional.Snapshot, ct);
        await f.Jobs.UpdateAsync(additional.Id, j => j with { State = AiJobState.Completed, LeaseId = null, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        await restarted.AdvanceAsync(f.Project.Id, ct); doc = await restarted.AdvanceAsync(f.Project.Id, ct);
        Assert.Equal(4, doc.Runs[0].Items[0].Candidates.Count); Assert.Equal(candidate, doc.Runs[0].Items[0].ApprovedImageId); Assert.Equal(2, f.Posts);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => restarted.GenerateAgainAsync(f.Project.Id, doc.Runs[0].Id, row.Item.Id, ct));
    }

    [Fact]
    public async Task BulkFailureContinuesToNextItemAndRetriesAreBounded()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var library = await f.Assets.LoadAsync(f.Project.Id, ct); var second = Asset("Studio lamp", "A blue desk lamp") with { Category = AssetCategory.Prop };
        library = await f.Assets.SaveAsync(library with { Assets = [.. library.Assets, second] }, library.Revision, ct);
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var rows = AssetBatchPlan.Build(library, ImageWorkflow.Krea2);
        var doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, rows.Select(r => r.Item).ToArray(), 1, "1:1", 1024, null, ct);
        async Task FailPending()
        {
            var run = (await workflow.LoadAsync(f.Project.Id, ct)).Runs[0];
            await workflow.AdvanceAsync(f.Project.Id, ct);
            await f.Jobs.UpdateAsync(run.Pending!.Id, j => j with { State = AiJobState.NeedsAttention, Error = "Synthetic generation failure", FinishedUtc = DateTimeOffset.UtcNow }, ct);
            await workflow.AdvanceAsync(f.Project.Id, ct);
        }
        await FailPending();
        doc = await workflow.LoadAsync(f.Project.Id, ct);
        Assert.Equal(AssetBatchItemState.NeedsAttention, doc.Runs[0].Items[0].State);
        Assert.Equal(second.Id, doc.Runs[0].Pending!.Target.AssetId);
        await FailPending();
        await workflow.RetryAsync(f.Project.Id, doc.Runs[0].Id, rows[0].Item.Id, ct); await workflow.AdvanceAsync(f.Project.Id, ct);
        await FailPending();
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.RetryAsync(f.Project.Id, doc.Runs[0].Id, rows[0].Item.Id, ct));
        Assert.Equal(3, (await f.Jobs.ReadAsync(ct)).Jobs.Count);
    }

    [Fact]
    public async Task BulkRejectsStaleDirectionAndForeignCandidateApproval()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        var item = AssetBatchPlan.Build(library, ImageWorkflow.Krea2)[0].Item;
        var doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, [item], 1, "1:1", 1024, null, ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.ApproveAsync(f.Project.Id, doc.Runs[0].Id, item.Id, f.References[0].ImageId, ct));
        await workflow.StopAsync(f.Project.Id, doc.Runs[0].Id, ct);
        await f.Assets.SaveAsync(library with { Assets = [library.Assets[0] with { Description = "New author direction" }] }, library.Revision, ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, [item], 1, "1:1", 1024, null, ct));
        Assert.Empty((await f.Jobs.ReadAsync(ct)).Jobs);
    }

    [Fact]
    public async Task BulkReviewFailureKeepsCandidatesAndContinuesWithoutApprovingThem()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        library = await f.Assets.SaveAsync(library with { Assets = [.. library.Assets, Asset("Desk lamp", "A small blue lamp") with { Category = AssetCategory.Prop }] }, library.Revision, ct);
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == f.Project.Id ? f.Project : null) };
        var providers = new FakeProviders { Models = [new("test/vision", "Vision", SupportsImages: true)] };
        var files = new ProjectFiles(Options.Create(new ProjectStorageOptions { RootDirectory = "projects" }), new AssetEnvironment { ContentRootPath = _directory }, projects);
        f.Settings.Value = f.Settings.Value with { HasOpenRouterKey = true, OpenRouterModel = "test/vision" };
        using var queue = new AiJobCoordinator(f.Jobs, f.Settings, [f.Worker], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        var capture = new AiTextJobCapture(f.Settings, projects, f.Assets, new PromptEnhancer(providers, f.Settings, f.Assets), new GuidanceAssistant(providers, f.Settings, f.Assets), providers: providers);
        var workflow = new AssetBatchWorkflow(new(files), f.Assets, f.Settings, new(f.Settings, projects, f.Assets), capture, f.Jobs, queue, new FakeReferenceImageGenerator(), new FakeReferenceImageEditor(), providers);
        var doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, AssetBatchPlan.Build(library, ImageWorkflow.Krea2).Select(r => r.Item).ToArray(),
            1, "1:1", 1024, new(AiBackend.OpenRouter, "test/vision", "Vision"), ct);
        var pending = doc.Runs[0].Pending!;
        var context = await f.Claim(pending); await f.Worker.ExecuteAsync(context, pending.Snapshot, ct);
        await f.Jobs.UpdateAsync(pending.Id, j => j with { State = AiJobState.Completed, LeaseId = null, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        doc = await workflow.AdvanceAsync(f.Project.Id, ct);
        var review = doc.Runs[0].Pending!; Assert.Equal(AiJobKind.AssetImageEvaluation, review.Kind);
        await f.Jobs.EnqueueAsync(review, ct);
        await f.Jobs.WriteArtifactAsync(review.Id, AiJobArtifact.Result, new AiTextJobResult("{incomplete", true, "Stop") { Error = "Synthetic invalid review JSON" }, ct);
        await f.Jobs.UpdateAsync(review.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        doc = await workflow.AdvanceAsync(f.Project.Id, ct);
        var first = doc.Runs[0].Items[0];
        Assert.Equal(AssetBatchItemState.NeedsAttention, first.State); Assert.Single(first.Candidates);
        Assert.Contains("Synthetic invalid", first.Error); Assert.Equal(library.Assets[1].Id, doc.Runs[0].Pending!.Target.AssetId);
        Assert.All((await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images, i => Assert.False(i.IsReference));
        Assert.Equal("{incomplete", (await f.Jobs.ReadArtifactAsync<AiTextJobResult>(review.Id, AiJobArtifact.Result, ct))!.Raw);
    }

    [Fact]
    public async Task BulkOutputRecoveryRetrievesTheOriginalInferenceInsteadOfGeneratingAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var item = AssetBatchPlan.Build(await f.Assets.LoadAsync(f.Project.Id, ct), ImageWorkflow.Krea2)[0].Item;
        var doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, [item], 2, "1:1", 1024, null, ct);
        var pending = doc.Runs[0].Pending!; var context = await f.Claim(pending); f.FailDownloads = 1;
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, pending.Snapshot, ct));
        await f.Jobs.UpdateAsync(pending.Id, j => j with { State = AiJobState.NeedsAttention, LeaseId = null, Recovery = error.Recovery, Error = error.Message }, ct);
        doc = await workflow.AdvanceAsync(f.Project.Id, ct); Assert.Equal(AssetBatchState.Completed, doc.Runs[0].State);
        doc = await workflow.RetryAsync(f.Project.Id, doc.Runs[0].Id, item.Id, ct);
        Assert.Equal(pending.Id, doc.Runs[0].Pending!.Id); Assert.Equal(1, doc.Runs[0].Items[0].Attempts);
        var retry = (await f.Jobs.ClaimNextAsync(AiBackend.ComfyUI, 1, ct))!;
        await f.Worker.RecoverAsync(f.Context(retry, true), pending.Snapshot, ct);
        await f.Jobs.UpdateAsync(pending.Id, j => j with { State = AiJobState.Completed, LeaseId = null, Recovery = AiJobRecovery.None, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        await workflow.AdvanceAsync(f.Project.Id, ct); doc = await workflow.AdvanceAsync(f.Project.Id, ct);
        Assert.Equal(2, doc.Runs[0].Items[0].Candidates.Count); Assert.Equal(1, f.Posts);
    }
}
