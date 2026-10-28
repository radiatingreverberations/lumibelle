using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    private AssetBatchWorkflow ReelWorkflow(ImageJobFixture f, out AiJobCoordinator queue)
    {
        var projects = new FakeProjectStore { Get = id => Task.FromResult<ProjectInfo?>(id == f.Project.Id ? f.Project : null) };
        var files = new ProjectFiles(Options.Create(new ProjectStorageOptions { RootDirectory = "projects" }), new AssetEnvironment { ContentRootPath = _directory }, projects);
        var providers = new FakeProviders { Models = [new("test/vision", "Vision", SupportsImages: true)] };
        var frames = new BatchReelFrames(); var generator = new Lumibelle.Testing.MockVideoGenerator(f.Assets);
        var capture = new AiReelCapture(f.Assets, f.Assets, f.Settings, generator, projects, new FakeProjectAiPreferencesStore(), referenceVideos: frames);
        queue = new(f.Jobs, f.Settings, [f.Worker, new BatchReelHandler()], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        var text = new AiTextJobCapture(f.Settings, projects, f.Assets, new PromptEnhancer(providers, f.Settings, f.Assets), new GuidanceAssistant(providers, f.Settings, f.Assets),
            providers: providers, referenceVideos: frames);
        return new(new(files), f.Assets, f.Settings, new(f.Settings, projects, f.Assets), text, f.Jobs, queue,
            new FakeReferenceImageGenerator(), new FakeReferenceImageEditor(), providers, capture, f.Assets, frames);
    }
    private async Task<AssetLibrary> ApproveReelInputs(ImageJobFixture f, AssetCategory category = AssetCategory.Character)
    {
        var ct = TestContext.Current.CancellationToken; var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        return await f.Assets.SaveAsync(library with { Assets = library.Assets.Select(a => a with { Category = category,
            Images = a.Images.Select(i => i with { IsReference = true }).ToList() }).ToList() }, library.Revision, ct);
    }
    private async Task<AssetReferenceReel> PublishBatchReel(ImageJobFixture f, AiJobSubmission pending)
    {
        var ct = TestContext.Current.CancellationToken;
        var captured = pending.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        var recipe = captured.Snapshot.Reel!.Recipe;
        var reel = new AssetReferenceReel { Id = pending.Batch!.Candidates[0].Id, AssetId = recipe.AssetId, LookId = recipe.LookId, Name = recipe.Name,
            UseGuidance = recipe.UseGuidance, Media = new(Guid.NewGuid(), new('a', 64), 100, 640, 360, 241, 24, 241/24d, false),
            Generation = new(recipe.Copy(), pending.Id, pending.Id, 1, 42, captured.Snapshot) };
        await f.Assets.PublishReelAsync(f.Project.Id, reel, ct);
        return reel;
    }
    private static string ReelReviewJson => """{"verdict":"Pass","summary":"Two complementary sampled views","checks":[{"name":"reference","verdict":"Pass","reason":"Visible details match"},{"name":"coverage","verdict":"CannotJudge","reason":"Other angles are not established"},{"name":"consistency","verdict":"Pass","reason":"Stable landmarks in the samples"},{"name":"clarity","verdict":"Pass","reason":"Readable silhouettes"}],"views":[{"candidate":1,"name":"Front view","notes":"Visible front and clear outline"},{"candidate":2,"name":"Side view","notes":"Complementary visible side"}],"warnings":["Watch the video to assess motion"]}""";

    [Theory] [InlineData(AssetBatchKind.CharacterReels)] [InlineData(AssetBatchKind.EnvironmentReels)]
    public async Task BulkReelsCaptureOneCandidateAndResumeAnExactPendingRequest(AssetBatchKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var library = await ApproveReelInputs(f, kind == AssetBatchKind.CharacterReels ? AssetCategory.Character : AssetCategory.Environment);
        var workflow = ReelWorkflow(f, out var queue); using var owned = queue;
        var rows = AssetBatchPlan.BuildReels(library, kind, kind == AssetBatchKind.CharacterReels ? ReelFraming.CharacterNeutralTurntable : ReelFraming.EnvironmentFullSet);
        var doc = await workflow.StartReelsAsync(f.Project.Id, Guid.NewGuid(), kind, [rows[0].Item], null, ct);
        var pending = doc.Runs[0].Pending!;
        Assert.Equal(AiJobKind.ReelVideo, pending.Kind); Assert.Single(pending.Batch!.Candidates);
        var snapshot = pending.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(ReelVoiceMode.Silent, snapshot.Snapshot.Reel!.Recipe.VoiceMode);
        f.Settings.Value = f.Settings.Value with { ComfyUrl = "http://changed.test" };
        await f.Jobs.EnqueueAsync(pending, ct); // An enqueue acknowledgement lost before a restart.
        var restarted = ReelWorkflow(f, out var queue2); using var owned2 = queue2;
        await restarted.AdvanceAsync(f.Project.Id, ct); await restarted.PauseAsync(f.Project.Id, doc.Runs[0].Id, ct);
        await PublishBatchReel(f, pending);
        await f.Jobs.UpdateAsync(pending.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        doc = await restarted.AdvanceAsync(f.Project.Id, ct);
        Assert.Equal(AssetBatchState.Paused, doc.Runs[0].State); Assert.Single(doc.Runs[0].Items[0].ReelCandidates);
        await restarted.ResumeAsync(f.Project.Id, doc.Runs[0].Id, ct); await restarted.AdvanceAsync(f.Project.Id, ct);
        doc = await restarted.AdvanceAsync(f.Project.Id, ct);
        Assert.Equal(AssetBatchState.Completed, doc.Runs[0].State); Assert.Empty(doc.Runs[0].Items[0].ReviewedReels);
        Assert.Single((await f.Jobs.ReadAsync(ct)).Jobs); Assert.Contains("comfy.test", snapshot.Snapshot.ComfyUrl);
    }

    [Fact]
    public async Task BulkReelReviewRequiresExplicitApprovalAndRejectsConcurrentKeyframeEdits()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        f.Settings.Value = f.Settings.Value with { HasOpenRouterKey = true, OpenRouterModel = "test/vision" };
        var library = await ApproveReelInputs(f); var workflow = ReelWorkflow(f, out var queue); using var owned = queue;
        var item = AssetBatchPlan.BuildReels(library, AssetBatchKind.CharacterReels, ReelFraming.CharacterNeutralTurntable)[0].Item;
        var doc = await workflow.StartReelsAsync(f.Project.Id, Guid.NewGuid(), AssetBatchKind.CharacterReels, [item], new(AiBackend.OpenRouter, "test/vision", "Vision"), ct);
        var pending = doc.Runs[0].Pending!; await f.Jobs.EnqueueAsync(pending, ct);
        var reel = await PublishBatchReel(f, pending);
        await f.Jobs.UpdateAsync(pending.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        doc = await workflow.AdvanceAsync(f.Project.Id, ct); var review = doc.Runs[0].Pending!;
        Assert.Equal(AiJobKind.AssetReelEvaluation, review.Kind);
        var request = review.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<AssetReelEvaluationRequest>();
        var parsed = AssetReelEvaluator.Parse(ReelReviewJson, request);
        await f.Jobs.EnqueueAsync(review, ct);
        await f.Jobs.WriteArtifactAsync(review.Id, AiJobArtifact.Result, new AiTextJobResult(ReelReviewJson, true, "Stop") { Value = JsonSerializer.SerializeToElement(parsed, AtomicJsonFile.Options) }, ct);
        await f.Jobs.UpdateAsync(review.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        doc = await workflow.AdvanceAsync(f.Project.Id, ct);
        Assert.Null((await f.Assets.LoadAsync(f.Project.Id, ct)).Reels[0].Keyframes);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.ApproveReelViewsAsync(f.Project.Id, doc.Runs[0].Id, item.Id, reel.Id, [999], ct));
        await workflow.ApproveReelViewsAsync(f.Project.Id, doc.Runs[0].Id, item.Id, reel.Id, [1], ct);
        await workflow.ApproveReelViewsAsync(f.Project.Id, doc.Runs[0].Id, item.Id, reel.Id, [1], ct); // Lost save acknowledgement is idempotent.
        library = await f.Assets.LoadAsync(f.Project.Id, ct); Assert.Single(library.Reels[0].Keyframes!.Frames);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.ApproveReelViewsAsync(f.Project.Id, doc.Runs[0].Id, item.Id, reel.Id, [2], ct));
        Assert.Single((await f.Assets.LoadAsync(f.Project.Id, ct)).Reels[0].Keyframes!.Frames);
    }

    [Fact]
    public async Task BulkMissingCoverageReusesAReelAndCountsOnlyTheRequestedLookAndPurpose()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var library = await ApproveReelInputs(f); var workflow = ReelWorkflow(f, out var queue); using var owned = queue;
        var first = AssetBatchPlan.BuildReels(library, AssetBatchKind.CharacterReels, ReelFraming.CharacterNeutralTurntable)[0].Item;
        var doc = await workflow.StartReelsAsync(f.Project.Id, Guid.NewGuid(), AssetBatchKind.CharacterReels, [first], null, ct);
        var pending = doc.Runs[0].Pending!; await f.Jobs.EnqueueAsync(pending, ct); var reel = await PublishBatchReel(f, pending);
        await f.Jobs.UpdateAsync(pending.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        await workflow.AdvanceAsync(f.Project.Id, ct); await workflow.AdvanceAsync(f.Project.Id, ct);
        library = await f.Assets.LoadAsync(f.Project.Id, ct);
        var row = AssetBatchPlan.BuildReels(library, AssetBatchKind.CharacterReels, ReelFraming.CharacterNeutralTurntable)[0];
        Assert.True(row.MissingReference); Assert.Equal(reel.Id, row.Item.ExistingReelId);
        doc = await workflow.StartReelsAsync(f.Project.Id, Guid.NewGuid(), AssetBatchKind.CharacterReels, [row.Item], null, ct);
        Assert.Null(doc.Runs[^1].Pending); Assert.Equal(0, doc.Runs[^1].Items[0].Attempts);
        var frames = await new BatchReelFrames().SuggestFramesAsync(f.Project.Id, reel.Media, 2, new(), ct);
        await workflow.SaveManualReelViewsAsync(f.Project.Id, doc.Runs[^1].Id, row.Item.Id, reel, frames, ct);
        library = await f.Assets.LoadAsync(f.Project.Id, ct);
        Assert.False(AssetBatchPlan.BuildReels(library, AssetBatchKind.CharacterReels, ReelFraming.CharacterNeutralTurntable)[0].MissingReference);
        Assert.True(AssetBatchPlan.BuildReels(library, AssetBatchKind.CharacterReels, ReelFraming.CharacterFacePriority)[0].MissingReference);
        Assert.Single((await f.Jobs.ReadAsync(ct)).Jobs);
    }
    [Fact]
    public async Task BulkReelReviewFailurePreservesVideoContinuesAndBoundsRetries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        f.Settings.Value = f.Settings.Value with { HasOpenRouterKey = true, OpenRouterModel = "test/vision" };
        var library = await ApproveReelInputs(f);
        library = await f.Assets.SaveAsync(library with { Assets = [library.Assets[0] with { Looks = [new() { Name = "Raincoat", Description = "Yellow coat" }] }] }, library.Revision, ct);
        var workflow = ReelWorkflow(f, out var queue); using var owned = queue;
        var rows = AssetBatchPlan.BuildReels(library, AssetBatchKind.CharacterReels, ReelFraming.CharacterNeutralTurntable);
        var doc = await workflow.StartReelsAsync(f.Project.Id, Guid.NewGuid(), AssetBatchKind.CharacterReels, rows.Select(r => r.Item).ToArray(), new(AiBackend.OpenRouter, "test/vision", "Vision"), ct);
        var pending = doc.Runs[0].Pending!; await f.Jobs.EnqueueAsync(pending, ct);
        var reel = await PublishBatchReel(f, pending);
        await f.Jobs.UpdateAsync(pending.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, ct);
        doc = await workflow.AdvanceAsync(f.Project.Id, ct);
        async Task FailReview()
        {
            var review = (await workflow.LoadAsync(f.Project.Id, ct)).Runs[0].Pending!;
            Assert.Equal(AiJobKind.AssetReelEvaluation, review.Kind);
            await f.Jobs.EnqueueAsync(review, ct);
            await f.Jobs.WriteArtifactAsync(review.Id, AiJobArtifact.Result, new AiTextJobResult("{incomplete", true, "Stop") { Error = "Synthetic invalid review JSON" }, ct);
            await f.Jobs.UpdateAsync(review.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, ct);
            doc = await workflow.AdvanceAsync(f.Project.Id, ct);
            Assert.Equal("{incomplete", (await f.Jobs.ReadArtifactAsync<AiTextJobResult>(review.Id, AiJobArtifact.Result, ct))!.Raw);
        }
        await FailReview();
        Assert.Equal(AssetBatchItemState.NeedsAttention, doc.Runs[0].Items[0].State);
        Assert.Equal(rows[1].Item.Id, doc.Runs[0].PendingItemId);
        Assert.Equal(AiJobKind.ReelVideo, doc.Runs[0].Pending!.Kind);
        Assert.Null((await f.Assets.LoadAsync(f.Project.Id, ct)).Reels[0].Keyframes);
        await workflow.StopAsync(f.Project.Id, doc.Runs[0].Id, ct); // The next request has not yet been submitted.
        await workflow.RetryAsync(f.Project.Id, doc.Runs[0].Id, rows[0].Item.Id, ct);
        await workflow.AdvanceAsync(f.Project.Id, ct); await FailReview();
        var first = doc.Runs[0].Items[0];
        Assert.Single(first.GenerationJobs); Assert.Single(first.ReelCandidates); Assert.Empty(first.ReelReviews);
        Assert.Equal(2, first.ReviewAttempts);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => workflow.RetryAsync(f.Project.Id, doc.Runs[0].Id, first.Id, ct));
        var frames = await new BatchReelFrames().SuggestFramesAsync(f.Project.Id, reel.Media, 2, new(), ct);
        await workflow.SaveManualReelViewsAsync(f.Project.Id, doc.Runs[0].Id, first.Id, reel, frames, ct);
        Assert.Equal(2, (await f.Assets.LoadAsync(f.Project.Id, ct)).Reels[0].Keyframes!.Frames.Count);
    }

    [Theory] [InlineData(ReelFraming.EnvironmentFeatureStudy)] [InlineData(ReelFraming.EnvironmentWalkthrough)] [InlineData(ReelFraming.EnvironmentPath)]
    public async Task BulkEnvironmentRecipesRequireTheAuthorRouteBeforeBuildingPrompts(ReelFraming framing)
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Krea2, false);
        var library = await ApproveReelInputs(f, AssetCategory.Environment);
        var row = AssetBatchPlan.BuildReels(library, AssetBatchKind.EnvironmentReels, framing)[0];
        Assert.Empty(row.Item.Prompt); Assert.NotNull(row.Issue);
        var recipe = row.Item.Recipe!.Copy(); recipe.Instructions = "Begin at the red door and hold a view of the central table, then return to the doorway.";
        var built = AssetBatchPlan.BuildReelPrompt(row.Item with { Recipe = recipe }, library);
        Assert.Contains("red door", built.Prompt); ReferenceReels.Validate(built.Recipe!, true);
    }

    private sealed class BatchReelHandler : IAiJobHandler
    {
        public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.ReelVideo, AiJobKind.AssetReelEvaluation];
        public Task<AiJobOutcome> ExecuteAsync(AiJobContext c, JsonElement s, CancellationToken t) => throw new NotSupportedException();
        public Task<AiJobOutcome> RecoverAsync(AiJobContext c, JsonElement s, CancellationToken t) => throw new NotSupportedException();
        public Task<bool> CancelRemoteAsync(AiJobContext c, JsonElement s, CancellationToken t) => Task.FromResult(true);
    }
    private sealed class BatchReelFrames : IReferenceVideoStore
    {
        public Task<ReelKeyframeSet> SuggestFramesAsync(Guid p, ReferenceVideoMedia m, int n, H3Settings s, CancellationToken ct = default) => Task.FromResult(new ReelKeyframeSet {
            Frames = [new() { Frame = new(m.Id, m.Sha256, 0, 0) }, new() { Frame = new(m.Id, m.Sha256, 168, 7) }] });
        public Task<AssetMedia> OpenFrameAsync(Guid p, ReelFrameIdentity f, H3Settings s, CancellationToken ct = default) => Task.FromResult(new AssetMedia(new MemoryStream(Png(16, 16)), "image/png", DateTime.UtcNow));
        public Task<ReferenceVideoMedia> ImportAsync(Guid p, Stream s, string n, H3Settings h, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReferenceVideoMedia> CopyTakeAsync(Guid p, Guid t, H3Settings h, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AssetMedia?> OpenAsync(Guid p, Guid m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateAsync(Guid p, IEnumerable<ShotVideoBinding> b, CancellationToken ct = default) => Task.CompletedTask;
        public Task PrepareAsync(Guid p, Shot s, string d, List<PreparedVideoInput> i, H3Settings h, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
