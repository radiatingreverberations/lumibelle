using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    // These tests drive saved terminal checkpoints; live text execution is tested in AiTextJobTests.
    private sealed class BulkPromptCheckpointHandler : IAiJobHandler
    {
        public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.AssetPromptEnhancement];
        public Task<AiJobOutcome> ExecuteAsync(AiJobContext c, JsonElement s, CancellationToken ct) => throw new NotSupportedException();
        public Task<AiJobOutcome> RecoverAsync(AiJobContext c, JsonElement s, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CancelRemoteAsync(AiJobContext c, JsonElement s, CancellationToken ct) => Task.FromResult(false);
    }
    private static TextModelReference BulkPromptModel(ImageJobFixture f)
    {
        f.Settings.Value = f.Settings.Value with { HasOpenRouterKey = true, OpenRouterModel = "test/prompt" };
        return new(AiBackend.OpenRouter, "test/prompt", "Prompt writer");
    }
    private static async Task SavePromptReply(ImageJobFixture f, Guid id, string? error, CancellationToken ct)
    {
        var reply = new PromptEnhancementResult(PromptEnhancementKind.Prompt, "Enhanced photographic robot. Full-body framing.");
        await f.Jobs.WriteArtifactAsync(id, AiJobArtifact.Result,
            new AiTextJobResult("Saved raw reply", true, "Stop", JsonSerializer.SerializeToElement(reply, AtomicJsonFile.Options), error), ct);
        await f.Jobs.UpdateAsync(id, j => j with { State = error is null ? AiJobState.Completed : AiJobState.NeedsAttention, Error = error }, ct);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BulkEnhancesBeforeGenerationAndResumesWithoutRepeatingTheRewrite(bool edit)
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, edit);
        var model = BulkPromptModel(f);
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        if (edit) library = await f.Assets.SaveAsync(library with { Assets = library.Assets.Select(a => a with {
            Images = a.Images.Select((i, n) => i with { IsReference = n == 0 }).ToList() }).ToList() }, library.Revision, ct);
        var item = AssetBatchPlan.Build(library, ImageWorkflow.Krea2)[0].Item with { AdditionalInstructions = "Full-body framing.", VisualStyle = new() { Preset = VisualStylePreset.Photographic } };
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, [item], 2, "9:16", 1024, null, ct, model);
        var pending = doc.Runs[0].Pending!;
        Assert.Equal(AiJobKind.AssetPromptEnhancement, pending.Kind);
        Assert.Equal(0, doc.Runs[0].Items[0].Attempts);
        var request = pending.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<PromptEnhancementRequest>();
        Assert.Contains(item.AdditionalInstructions, request.Context.Prompt);
        Assert.Equal("9:16", request.Context.AspectRatio); Assert.Equal(edit, request.Context.IsEdit);
        Assert.Equal(item.References.Count, request.Context.References.Count);
        await workflow.AdvanceAsync(f.Project.Id, ct);
        var restarted = BulkWorkflow(f, out var restartedQueue); using var owned2 = restartedQueue;
        await restarted.AdvanceAsync(f.Project.Id, ct);
        Assert.Single((await f.Jobs.ReadAsync(ct)).Jobs);
        await SavePromptReply(f, pending.Id, null, ct);
        doc = await restarted.AdvanceAsync(f.Project.Id, ct);
        var run = doc.Runs[0];
        Assert.Equal(edit ? AiJobKind.ImageEdit : AiJobKind.ImageCreate, run.Pending!.Kind);
        var image = run.Pending.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(run.Items[0].EnhancedPrompt, image.OriginalPrompt);
        Assert.Equal(item.Prompt, run.Items[0].Prompt);
        Assert.Equal(item.AdditionalInstructions, run.Items[0].AdditionalInstructions);
        Assert.Single(run.Items[0].PromptJobs); Assert.Equal(1, run.Items[0].PromptAttempts);
        Assert.Equal(library.Assets[0].Description, (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Description);
        Assert.Contains("Saved raw reply", (await f.Jobs.ReadArtifactAsync<AiTextJobResult>(pending.Id, AiJobArtifact.Result, ct))!.Raw);
    }

    [Fact]
    public async Task BulkFailedRewriteDoesNotGenerateAndRetriesAreBounded()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var model = BulkPromptModel(f);
        var item = AssetBatchPlan.Build(await f.Assets.LoadAsync(f.Project.Id, ct), ImageWorkflow.Krea2)[0].Item;
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, [item], 1, "1:1", 1024, null, ct, model);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await workflow.AdvanceAsync(f.Project.Id, ct);
            await SavePromptReply(f, doc.Runs[0].Pending!.Id, "The reply was truncated.", ct);
            doc = await workflow.AdvanceAsync(f.Project.Id, ct);
            Assert.Equal(AssetBatchItemState.NeedsAttention, doc.Runs[0].Items[0].State);
            Assert.Empty(doc.Runs[0].Items[0].GenerationJobs); Assert.Null(doc.Runs[0].Items[0].EnhancedPrompt);
            Assert.Equal(item.Prompt, doc.Runs[0].Items[0].Prompt);
            if (attempt == 0) { await workflow.RetryAsync(f.Project.Id, doc.Runs[0].Id, item.Id, ct); doc = await workflow.AdvanceAsync(f.Project.Id, ct); }
        }
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.RetryAsync(f.Project.Id, doc.Runs[0].Id, item.Id, ct));
        Assert.Equal(2, (await f.Jobs.ReadAsync(ct)).Jobs.Count);
    }

    [Fact]
    public async Task BulkFailedRewriteContinuesWithCapturedSettingsForTheNextAsset()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var model = BulkPromptModel(f);
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        library = await f.Assets.SaveAsync(library with { Assets = [.. library.Assets,
            Asset("Workshop", "A bright room") with { Category = AssetCategory.Environment }] }, library.Revision, ct);
        var items = AssetBatchPlan.Build(library, ImageWorkflow.Krea2).Select(r => r.Item).ToArray();
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, items, 1, "16:9", 1024, null, ct, model);
        await workflow.AdvanceAsync(f.Project.Id, ct);
        f.Settings.Value = f.Settings.Value with { OpenRouterModel = "different/model", Temperature = .8f };
        await SavePromptReply(f, doc.Runs[0].Pending!.Id, "The reply was truncated.", ct);
        doc = await workflow.AdvanceAsync(f.Project.Id, ct);
        Assert.Equal(items[1].Context.AssetId, doc.Runs[0].Pending!.Target.AssetId);
        Assert.Equal(AiJobKind.AssetPromptEnhancement, doc.Runs[0].Pending!.Kind);
        var next = doc.Runs[0].Pending!.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(model.Model, next.Model.Model);
        Assert.Equal("test/prompt", next.Settings.OpenRouterModel);
        Assert.NotEqual(f.Settings.Value.Temperature, next.Settings.Temperature);
        Assert.Equal(AssetBatchItemState.NeedsAttention, doc.Runs[0].Items[0].State);
        Assert.Empty(doc.Runs[0].Items[0].GenerationJobs);
    }

    [Fact]
    public async Task BulkPauseHoldsGenerationAfterEnhancementAndStopDoesNotStartANewRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var model = BulkPromptModel(f);
        var item = AssetBatchPlan.Build(await f.Assets.LoadAsync(f.Project.Id, ct), ImageWorkflow.Krea2)[0].Item;
        var workflow = BulkWorkflow(f, out var queue); using var owned = queue;
        var doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, [item], 1, "1:1", 1024, null, ct, model);
        var run = doc.Runs[0];
        await workflow.AdvanceAsync(f.Project.Id, ct);
        await workflow.PauseAsync(f.Project.Id, run.Id, ct);
        await SavePromptReply(f, run.Pending!.Id, null, ct);
        doc = await workflow.AdvanceAsync(f.Project.Id, ct);
        Assert.Equal(AssetBatchState.Paused, doc.Runs[0].State); Assert.Null(doc.Runs[0].Pending);
        Assert.NotNull(doc.Runs[0].Items[0].EnhancedPrompt); Assert.Empty(doc.Runs[0].Items[0].GenerationJobs);
        await workflow.ResumeAsync(f.Project.Id, run.Id, ct);
        doc = await workflow.AdvanceAsync(f.Project.Id, ct);
        Assert.Equal(AiJobKind.ImageCreate, doc.Runs[0].Pending!.Kind);
        await workflow.StopAsync(f.Project.Id, run.Id, ct);
        doc = await workflow.StartAsync(f.Project.Id, Guid.NewGuid(), ImageWorkflow.Krea2, [item], 1, "1:1", 1024, null, ct, model);
        run = doc.Runs.Last();
        await workflow.StopAsync(f.Project.Id, run.Id, ct);
        doc = await workflow.AdvanceAsync(f.Project.Id, ct);
        Assert.Equal(AssetBatchItemState.Skipped, doc.Runs.Last().Items[0].State);
        Assert.DoesNotContain((await f.Jobs.ReadAsync(ct)).Jobs, j => j.Id == run.Pending!.Id);
    }
}
