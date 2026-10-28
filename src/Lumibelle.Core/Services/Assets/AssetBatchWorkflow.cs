using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class AssetBatchWorkflow(FileAssetBatchStore store, IAssetStore assets, IAiSettingsStore settings,
    AiImageJobCapture images, AiTextJobCapture text, IAiJobStore jobs, AiJobCoordinator queue,
    IReferenceImageGenerator generator, IReferenceImageEditor editor, IAiProviderRegistry providers,
    AiReelCapture? reelCapture = null, IAssetReelStore? reels = null, lumibelle.Services.Production.IReferenceVideoStore? videos = null)
{
    public event Action<Guid> Changed { add => store.Changed += value; remove => store.Changed -= value; }
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
    public async Task<AssetBatches> StartAsync(Guid project, Guid tab, ImageWorkflow workflow, IReadOnlyList<AssetBatchItem> items,
        int count, string aspect, int resolution, TextModelReference? reviewer, CancellationToken ct = default, TextModelReference? promptModel = null)
    {
        items = Copy(items).Select(i => i with { AdditionalInstructions = i.AdditionalInstructions?.Trim() ?? "" }).ToArray();
        reviewer = reviewer is null ? null : Copy(reviewer);
        promptModel = promptModel is null ? null : Copy(promptModel);
        var configured = Copy(await settings.LoadAsync(ct)) with { DefaultImageWorkflow = workflow };
        if (items.Count is < 1 or > 200 || count is < 1 or > 4 || !ImageAspectPolicy.IsSupported(aspect, false) || !ImageAspectPolicy.IsResolutionSupported(resolution))
            throw new WorkspaceStoreException("Choose one to 200 asset/looks, one to four candidates and supported output settings.");
        if (promptModel is not null)
        {
            var check = await providers.CheckAsync(promptModel.Backend, configured, cancellationToken: ct);
            if (TextModelPolicy.Issue(promptModel, configured, check) is { } issue) throw new AiGenerationException(issue);
        }
        if (reviewer is not null)
        {
            var check = await providers.CheckAsync(reviewer.Backend, configured, cancellationToken: ct);
            if (TextModelPolicy.Issue(reviewer, configured, check) is { } issue) throw new AiGenerationException(issue);
            if (!TextVisionPolicy.SupportsBackend(reviewer.Backend) || TextModelPolicy.CatalogModel(reviewer, check)?.SupportsImages != true)
                throw new AiGenerationException(TextVisionPolicy.SetupHint);
        }
        var library = await assets.LoadAsync(project, ct);
        foreach (var item in items)
        {
            if (item.AdditionalInstructions.Length > AssetBatchPlan.MaximumAdditionalInstructions || AssetBatchPlan.ImagePrompt(item).Length > 30000)
                throw new WorkspaceStoreException("Keep additional instructions within 6,000 characters and the combined prompt within 30,000 characters.");
            if (library.Assets.SingleOrDefault(a => a.Id == item.Context.AssetId)?.Category != item.Category)
                throw new WorkspaceStoreException("The asset category changed. Refresh the batch plan.");
            if (item.State != AssetBatchItemState.Pending || item.Attempts != 0 || item.ReviewAttempts != 0 || item.PromptAttempts != 0 || item.EnhancedPrompt is not null || item.PromptJobs.Count != 0 || item.GenerateAgain || item.Candidates.Count != 0 || item.GenerationJobs.Count != 0 || item.ReviewJobs.Count != 0 || item.Evaluation is not null || item.ApprovedImageId is not null)
                throw new WorkspaceStoreException("Start with a fresh reviewed asset plan.");
            LookPolicy.ValidateTarget(library, item.Context, requireCurrent: true); LookPolicy.ValidateReferences(library, item.References);
            if (item.References.Any(r => !library.Assets.Any(a => a.Id == r.Reference.AssetId && a.Images.Any(i => i.Id == r.Reference.ImageId && i.IsReference))))
                throw new WorkspaceStoreException("The batch requires approved input references. Review the plan again.");
            if (item.Context.LookId is not null && item.Category == AssetCategory.Character && item.References.Count == 0)
                throw new WorkspaceStoreException("Approve an identity reference before generating a character look.");
            if (string.IsNullOrWhiteSpace(item.Prompt)) throw new WorkspaceStoreException("Every selected asset needs a prompt.");
        }
        if (items.Any(i => i.References.Count == 0))
        { var check = await generator.CheckAsync(configured, ct); if (!check.Success) throw new AiGenerationException(check.Message); }
        if (items.Any(i => i.References.Count > 0))
        {
            var check = await editor.CheckAsync(configured, ct); if (!check.Success) throw new AiGenerationException(check.Message);
            if (items.Any(i => i.References.Count > check.MaximumReferences)) throw new AiGenerationException("This installation cannot accept every selected reference.");
        }
        var run = new AssetBatch { TabId = tab, Settings = configured, Reviewer = reviewer, PromptModel = promptModel, CandidateCount = count,
            AspectRatio = aspect, Resolution = resolution, Items = items };
        return await store.UpdateAsync(project, async doc =>
        {
            if (doc.Runs.Any(r => r.Active || r.Pending is not null)) throw new WorkspaceStoreException("Finish or stop the current asset batch before starting another.");
            run = await PrepareAsync(project, run, items[0], ct);
            return doc with { Runs = [.. doc.Runs, run] };
        }, ct);
    }

    public Task<AssetBatches> AdvanceAsync(Guid project, CancellationToken ct = default) => store.UpdateAsync(project, async doc =>
    {
        var run = doc.Runs.LastOrDefault(r => r.Active || r.Pending is not null); if (run is null) return doc;
        try
        {
            if (run.Pending is { } pending)
            {
                var job = (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == pending.Id);
                if (job is null)
                {
                    if (run.State == AssetBatchState.Running) await queue.EnqueueAsync(pending, ct);
                    return doc;
                }
                if (job.State is AiJobState.Waiting or AiJobState.Running) return doc;
                run = await ConsumeAsync(project, run, job, ct);
            }
            if (run.State != AssetBatchState.Running) return FileAssetBatchStore.Replace(doc, run);
            var next = run.Items.FirstOrDefault(i => i.State == AssetBatchItemState.Pending);
            if (next is null) run = run with { State = AssetBatchState.Completed };
            else
            {
                try { run = await PrepareAsync(project, run, next, ct); }
                catch (Exception e) when (e is AiGenerationException or WorkspaceStoreException or ProjectStoreException or IOException)
                { run = FileAssetBatchStore.Replace(run, next with { State = AssetBatchItemState.NeedsAttention, Error = e.Message }); }
            }
        }
        catch (Exception e) when (e is AiGenerationException or WorkspaceStoreException or ProjectStoreException or IOException)
        { run = run with { State = AssetBatchState.Paused, Error = e.Message }; }
        return FileAssetBatchStore.Replace(doc, run);
    }, ct);

    private async Task<AssetBatch> PrepareAsync(Guid project, AssetBatch run, AssetBatchItem item, CancellationToken ct)
    {
        var library = await assets.LoadAsync(project, ct); LookPolicy.ValidateTarget(library, item.Context, requireCurrent: true);
        if (run.Kind != AssetBatchKind.Images) return await PrepareReelAsync(project, run, item, library, ct);
        if (run.PromptModel is not null && item.EnhancedPrompt is null && item.Candidates.Count == 0)
            return await PreparePromptAsync(project, run, item, ct);
        if (item.Candidates.Count > 0 && !item.GenerateAgain && run.Reviewer is null)
            return FileAssetBatchStore.Replace(run, item with { State = AssetBatchItemState.Ready });
        var id = Guid.NewGuid(); AiJobSubmission pending;
        var qwen = run.Settings.DefaultImageWorkflow == ImageWorkflow.QwenImage21 ? new QwenImage21Options(run.Resolution) : null;
        var resolution = qwen is not null || run.Settings.DefaultImageWorkflow == ImageWorkflow.CodexImages ? 1024 : run.Resolution;
        if (item.Candidates.Count > 0 && !item.GenerateAgain)
        {
            if (item.ReviewAttempts >= 2) throw new WorkspaceStoreException("This item reached its review retry limit. Review the saved images manually.");
            pending = await text.EvaluateAssetImagesAsync(id, run.TabId, project, run.Id, item, run.Reviewer!, run.Settings, ct);
            item = item with { State = AssetBatchItemState.Evaluating, ReviewJobId = id, ReviewJobs = [.. item.ReviewJobs, id], ReviewAttempts = item.ReviewAttempts + 1 };
        }
        else
        {
            if (item.Attempts >= 2) throw new WorkspaceStoreException("This item reached its generation retry limit. Its saved responses remain available.");
            var baseReference = item.References.FirstOrDefault()?.Reference;
            pending = baseReference is null
                ? await images.CreateAsync(id, run.TabId, item.Context.AssetId, new() { ProjectId = project, Workflow = run.Settings.DefaultImageWorkflow,
                    Prompt = AssetBatchPlan.ImagePrompt(item), PendingReview = true, VisualStyle = item.VisualStyle ?? new(), Look = item.Context, AspectRatio = run.AspectRatio, Resolution = resolution, QwenImage21 = qwen, Count = run.CandidateCount, Loras = item.Loras }, ct, run.Settings)
                : await images.EditAsync(id, run.TabId, item.Context.AssetId, new() { ProjectId = project, Workflow = run.Settings.DefaultImageWorkflow,
                    SourceAssetId = baseReference.AssetId, SourceImageId = baseReference.ImageId, ReferenceLooks = item.References,
                    Prompt = AssetBatchPlan.ImagePrompt(item), PendingReview = true, VisualStyle = item.VisualStyle ?? new(), Look = item.Context, AspectRatio = run.AspectRatio, Resolution = resolution, QwenImage21 = qwen, Count = run.CandidateCount, Loras = item.Loras },
                    item.References.Select(r => r.Reference).ToArray(), ct, run.Settings);
            item = item with { State = AssetBatchItemState.Generating, GenerateAgain = false, Attempts = item.Attempts + 1, GenerationJobs = [.. item.GenerationJobs, id] };
        }
        // Persist the exact submission before enqueueing. Lost acknowledgements reuse this ID.
        return FileAssetBatchStore.Replace(run, item) with { PendingItemId = item.Id, Pending = pending, Error = null };
    }

    private async Task<AssetBatch> ConsumeAsync(Guid project, AssetBatch run, AiJobHeader job, CancellationToken ct)
    {
        if (run.Kind != AssetBatchKind.Images) return await ConsumeReelAsync(project, run, job, ct);
        var item = run.Items.Single(i => i.Id == run.PendingItemId);
        if (job.Kind == AiJobKind.AssetPromptEnhancement)
            return await ConsumePromptAsync(run, item, job, ct);
        if (job.Kind == AiJobKind.AssetImageEvaluation)
        {
            var result = await jobs.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, ct);
            var evaluation = result?.Error is null ? result?.Read<AssetImageEvaluation>() : null;
            item = item with { Evaluation = evaluation, State = evaluation is null ? AssetBatchItemState.NeedsAttention : AssetBatchItemState.Ready,
                Error = evaluation is null ? result?.Error ?? job.Error ?? "The review did not complete. Saved images are available for manual review." : item.Error };
        }
        else
        {
            var library = await assets.LoadAsync(project, ct);
            var candidates = library.ImagePublications.Where(p => p.JobId == job.Id && p.AssetId == item.Context.AssetId)
                .Select(p => p.ImageId).Where(id => library.Assets.Any(a => a.Id == item.Context.AssetId && a.Images.Any(i => i.Id == id))).ToArray();
            item = item with { Candidates = item.Candidates.Concat(candidates).Distinct().ToArray(),
                State = candidates.Length > 0 && job.State == AiJobState.Completed ? run.State == AssetBatchState.Stopped ? AssetBatchItemState.Ready : AssetBatchItemState.Pending : AssetBatchItemState.NeedsAttention,
                Error = job.State != AiJobState.Completed ? job.Error ?? "Generation stopped. Completed takes are preserved." : null };
        }
        return FileAssetBatchStore.Replace(run, item) with { Pending = null, PendingItemId = null };
    }
}
