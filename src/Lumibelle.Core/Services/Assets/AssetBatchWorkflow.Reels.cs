using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class AssetBatchWorkflow
{
    public async Task<AssetBatches> StartReelsAsync(Guid project, Guid tab, AssetBatchKind kind, IReadOnlyList<AssetBatchItem> items,
        TextModelReference? reviewer, CancellationToken ct = default)
    {
        if (kind is not (AssetBatchKind.CharacterReels or AssetBatchKind.EnvironmentReels) || items.Count is < 1 or > 200)
            throw new WorkspaceStoreException("Choose one to 200 character/look or environment recipes.");
        if (reelCapture is null || reels is null) throw new WorkspaceStoreException("Reference reel generation is unavailable.");
        items = Copy(items); reviewer = reviewer is null ? null : Copy(reviewer);
        var configured = Copy(await settings.LoadAsync(ct));
        if (reviewer is not null)
        {
            var check = await providers.CheckAsync(reviewer.Backend, configured, cancellationToken: ct);
            if (TextModelPolicy.Issue(reviewer, configured, check) is { } issue) throw new AiGenerationException(issue);
            if (!TextVisionPolicy.SupportsBackend(reviewer.Backend) || TextModelPolicy.CatalogModel(reviewer, check)?.SupportsImages != true) throw new AiGenerationException(TextVisionPolicy.SetupHint);
        }
        var library = await assets.LoadAsync(project, ct);
        var prepared = new List<AssetBatchItem>();
        foreach (var item in items)
        {
            if (item.State != AssetBatchItemState.Pending || item.Attempts != 0 || item.ReviewAttempts != 0 || item.GenerateAgain || item.OutputCount != 0 ||
                item.GenerationJobs.Count != 0 || item.ReviewJobs.Count != 0 || item.ReelReviews.Count != 0 || item.ReviewedReels.Count != 0 || item.ApprovedImageId is not null)
                throw new WorkspaceStoreException("Start with a fresh reviewed reel plan.");
            LookPolicy.ValidateTarget(library, item.Context, requireCurrent: true);
            var owner = library.Assets.Single(a => a.Id == item.Context.AssetId);
            if (owner.Category != item.Category || owner.Category != (kind == AssetBatchKind.CharacterReels ? AssetCategory.Character : AssetCategory.Environment))
                throw new WorkspaceStoreException("The asset type changed. Refresh the reel plan.");
            var recipe = item.Recipe?.Copy() ?? throw new WorkspaceStoreException("Choose a reel recipe.");
            if (recipe.AssetId != item.Context.AssetId || recipe.LookId != item.Context.LookId || recipe.VoiceMode != ReelVoiceMode.Silent)
                throw new WorkspaceStoreException("The visual reel recipe belongs to another asset/look or includes speech.");
            if (item.ExistingReelId is { } existing)
            {
                var source = library.Reels.SingleOrDefault(r => r.Id == existing && r.AssetId == owner.Id && r.LookId == item.Context.LookId && r.Generation?.Recipe.Framing == recipe.Framing)
                    ?? throw new WorkspaceStoreException("The saved coverage reel changed. Refresh the plan.");
                prepared.Add(item with { Recipe = recipe, Prompt = string.IsNullOrWhiteSpace(item.Prompt) ? source.Generation!.Recipe.Prompt : item.Prompt, ReelCandidates = [source.Id] });
            }
            else
            {
                ValidateReelInputs(library, item, recipe);
                recipe.Prompt = item.Prompt; ReferenceReels.Validate(recipe, true);
                prepared.Add(item with { Recipe = recipe });
            }
        }
        var run = new AssetBatch { Kind = kind, TabId = tab, Settings = configured, Reviewer = reviewer, CandidateCount = 1, Items = prepared };
        return await store.UpdateAsync(project, async doc =>
        {
            if (doc.Runs.Any(r => r.Active || r.Pending is not null)) throw new WorkspaceStoreException("Finish or stop the current asset batch before starting another.");
            run = await PrepareAsync(project, run, run.Items[0], ct);
            return doc with { Runs = [.. doc.Runs, run] };
        }, ct);
    }

    private static void ValidateReelInputs(AssetLibrary library, AssetBatchItem item, ReferenceReelDraft recipe)
    {
        LookPolicy.ValidateReferences(library, item.References);
        if (item.References.Count == 0 || item.References.Any(r => !library.Assets.Any(a => a.Id == r.Reference.AssetId && a.Images.Any(i => i.Id == r.Reference.ImageId && i.IsReference))))
            throw new WorkspaceStoreException("Approve the selected reference images before generating a reel.");
        if (!recipe.Images.Select(i => new AssetImageReference(i.AssetId, i.MediaId)).SequenceEqual(item.References.Select(r => r.Reference)))
            throw new WorkspaceStoreException("The recipe's inputs changed. Review the selected references again.");
        foreach (var reference in item.References) LookPolicy.ValidateTarget(library, reference.Context, requireCurrent: true);
    }
    private static ReferenceReelDraft Comparable(ReferenceReelDraft recipe) => recipe.Copy() with { Revision = 0, PendingJobId = null, ResolvedJobs = [], CheckedInputs = null };
    private async Task<AssetBatch> PrepareReelAsync(Guid project, AssetBatch run, AssetBatchItem item, AssetLibrary library, CancellationToken ct)
    {
        var id = Guid.NewGuid(); AiJobSubmission pending;
        if (item.ReelCandidates.Count > 0 && !item.GenerateAgain)
        {
            var reel = item.ReelCandidates[^1];
            if (run.Reviewer is null || item.ReviewedReels.Contains(reel) || item.ReelReviews.Any(r => r.Request.Views.ReelId == reel))
                return FileAssetBatchStore.Replace(run, item with { State = AssetBatchItemState.Ready });
            if (item.ReviewAttempts >= 2) throw new WorkspaceStoreException("This item reached its review retry limit. Review the saved reel manually.");
            pending = await text.EvaluateAssetReelAsync(id, run.TabId, project, run.Id, item, reel, run.Reviewer, run.Settings, ct);
            item = item with { State = AssetBatchItemState.Evaluating, ReviewJobId = id, ReviewJobs = [.. item.ReviewJobs, id], ReviewAttempts = item.ReviewAttempts + 1 };
        }
        else
        {
            if (item.Attempts >= 2) throw new WorkspaceStoreException("This item reached its generation retry limit. Saved reels remain available.");
            var recipe = item.Recipe!.Copy(); recipe.Prompt = item.Prompt;
            ValidateReelInputs(library, item, recipe);
            var saved = library.ReelDrafts.SingleOrDefault(r => r.Id == recipe.Id);
            if (saved is null) saved = await reels!.SaveDraftAsync(project, recipe, 0, ct);
            else if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(Comparable(saved), AtomicJsonFile.Options), JsonSerializer.SerializeToElement(Comparable(recipe), AtomicJsonFile.Options)))
                throw new WorkspaceStoreException("This reel recipe changed elsewhere. Start a new reviewed plan; the authored recipe is retained.");
            pending = await reelCapture!.CaptureAsync(id, run.TabId, project, saved.Id, saved.Revision, 1, ct, run.Settings);
            item = item with { State = AssetBatchItemState.Generating, GenerateAgain = false, Attempts = item.Attempts + 1, GenerationJobs = [.. item.GenerationJobs, id] };
        }
        return FileAssetBatchStore.Replace(run, item) with { Pending = pending, PendingItemId = item.Id, Error = null };
    }
    private async Task<AssetBatch> ConsumeReelAsync(Guid project, AssetBatch run, AiJobHeader job, CancellationToken ct)
    {
        var item = run.Items.Single(i => i.Id == run.PendingItemId);
        if (job.Kind == AiJobKind.AssetReelEvaluation)
        {
            var result = await jobs.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, ct);
            var evaluation = result?.Error is null ? result?.Read<AssetReelEvaluation>() : null;
            if (evaluation is null) item = item with { State = AssetBatchItemState.NeedsAttention, Error = result?.Error ?? job.Error ?? "The reel review did not finish. Saved videos can be reviewed manually." };
            else
            {
                var request = (await jobs.ReadSnapshotAsync(job.Id, ct)).Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<AssetReelEvaluationRequest>();
                AssetReelEvaluator.Validate(request);
                if (request.ItemId != item.Id || request.BatchId != run.Id || !item.ReelCandidates.Contains(request.Views.ReelId)) throw new WorkspaceStoreException("The saved reel review belongs to another batch item.");
                item = item with { State = AssetBatchItemState.Ready, ReelReviews = [.. item.ReelReviews.Where(r => r.Request.Views.ReelId != request.Views.ReelId), new(job.Id, request, evaluation)] };
            }
        }
        else
        {
            var library = await assets.LoadAsync(project, ct);
            var found = library.ReelPublications.Where(p => p.JobId == job.Id).Select(p => p.ReelId)
                .Where(id => library.Reels.Any(r => r.Id == id && r.AssetId == item.Context.AssetId && r.LookId == item.Context.LookId)).ToArray();
            item = item with { ReelCandidates = item.ReelCandidates.Concat(found).Distinct().ToArray(),
                State = found.Length > 0 && job.State == AiJobState.Completed ? run.State == AssetBatchState.Stopped ? AssetBatchItemState.Ready : AssetBatchItemState.Pending : AssetBatchItemState.NeedsAttention,
                Error = job.State == AiJobState.Completed ? null : job.Error ?? "Generation stopped. Saved reels remain available." };
        }
        return FileAssetBatchStore.Replace(run, item) with { Pending = null, PendingItemId = null };
    }

    public Task<AssetBatches> ApproveReelViewsAsync(Guid project, Guid batch, Guid itemId, Guid reelId, IReadOnlyList<int> selected, CancellationToken ct = default) => store.UpdateAsync(project, async doc =>
    {
        var run = Find(doc, batch); var item = run.Items.Single(i => i.Id == itemId);
        var review = item.ReelReviews.SingleOrDefault(r => r.Request.Views.ReelId == reelId) ?? throw new WorkspaceStoreException("Review the saved reel before choosing suggested keyframes.");
        if (selected.Count == 0 || selected.Distinct().Count() != selected.Count || selected.Any(n => !review.Result.Selection.Views.Any(v => v.Candidate == n)))
            throw new WorkspaceStoreException("Choose available suggested views.");
        var library = await assets.LoadAsync(project, ct); var reel = ReelTarget(library, item, reelId);
        var choices = review.Result.Selection.Views.Where(v => selected.Contains(v.Candidate)).ToArray();
        var current = reel.Keyframes ?? new();
        var alreadySaved = choices.All(v => current.Frames.Any(f => f.Frame == review.Request.Views.Candidates.Single(c => c.Number == v.Candidate).Keyframe.Frame && f.Name == v.Name.Trim() && f.Notes == v.Notes.Trim() &&
            JsonElement.DeepEquals(JsonSerializer.SerializeToElement(f.Crop, AtomicJsonFile.Options), JsonSerializer.SerializeToElement(review.Request.Views.Candidates.Single(c => c.Number == v.Candidate).Keyframe.Crop, AtomicJsonFile.Options))));
        if (!alreadySaved)
        {
            var staged = ReelViewSuggestions.Stage(review.Request.Views, choices, current, false);
            await SaveReelFramesAsync(project, run, reel, staged, library.Revision, ct);
        }
        return FileAssetBatchStore.Replace(doc, FileAssetBatchStore.Replace(run, item with { ReviewedReels = item.ReviewedReels.Append(reelId).Distinct().ToArray() }));
    }, ct);
    private static AssetReferenceReel ReelTarget(AssetLibrary library, AssetBatchItem item, Guid reelId)
    {
        LookPolicy.ValidateTarget(library, item.Context, requireCurrent: true);
        if (!item.ReelCandidates.Contains(reelId)) throw new WorkspaceStoreException("Choose a saved reel from this batch item.");
        return library.Reels.SingleOrDefault(r => r.Id == reelId && r.AssetId == item.Context.AssetId && r.LookId == item.Context.LookId)
            ?? throw new WorkspaceStoreException("The reel was moved or removed. Restore it before reviewing keyframes.");
    }
    private async Task SaveReelFramesAsync(Guid project, AssetBatch run, AssetReferenceReel baseline, ReelKeyframeSet frames, long revision, CancellationToken ct)
    {
        ReferenceVideos.ValidateKeyframes(baseline.Media, frames);
        await (videos ?? throw new WorkspaceStoreException("Reel frame extraction is unavailable.")).PrepareFramesAsync(project, frames.Frames.Select(f => f.Frame), run.Settings.H3, ct);
        await reels!.SaveKeyframesAsync(project, baseline, frames, revision, ct);
    }
    public Task<AssetBatches> SaveManualReelViewsAsync(Guid project, Guid batch, Guid itemId, AssetReferenceReel baseline, ReelKeyframeSet frames, CancellationToken ct = default) => store.UpdateAsync(project, async doc =>
    {
        var run = Find(doc, batch); var item = run.Items.Single(i => i.Id == itemId);
        var library = await assets.LoadAsync(project, ct);
        if (!item.ReelCandidates.Contains(baseline.Id) && library.ReelPublications.Any(p => p.ReelId == baseline.Id && item.GenerationJobs.Contains(p.JobId)))
            item = item with { ReelCandidates = [.. item.ReelCandidates, baseline.Id] };
        _ = ReelTarget(library, item, baseline.Id);
        await SaveReelFramesAsync(project, run, baseline, frames, library.Revision, ct);
        return FileAssetBatchStore.Replace(doc, FileAssetBatchStore.Replace(run, item with { ReviewedReels = frames.Frames.Count > 0 ? item.ReviewedReels.Append(baseline.Id).Distinct().ToArray() : item.ReviewedReels.Where(id => id != baseline.Id).ToArray() }));
    }, ct);
}
