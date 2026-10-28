using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed partial class AssetBatchWorkflow
{
    private async Task<AssetBatch> PreparePromptAsync(Guid project, AssetBatch run, AssetBatchItem item, CancellationToken ct)
    {
        if (item.PromptAttempts >= 2)
            throw new WorkspaceStoreException("This item reached its prompt enhancement retry limit. Inspect its saved responses.");
        var workflow = run.Settings.DefaultImageWorkflow;
        var prompt = AssetBatchPlan.ImagePrompt(item);
        var context = new PromptEnhancementContext
        {
            ProjectId = project, AssetId = item.Context.AssetId, TargetLook = item.Context,
            AssetName = item.Context.AssetName, Category = item.Category, Prompt = prompt,
            VisualNotes = item.Context.IdentityNotes, VisualStyle = item.VisualStyle ?? new(),
            Workflow = workflow, IsEdit = item.References.Count > 0, AspectRatio = run.AspectRatio,
            Resolution = workflow is ImageWorkflow.QwenImage21 or ImageWorkflow.CodexImages ? 1024 : run.Resolution,
            QwenImage21 = workflow == ImageWorkflow.QwenImage21 ? new(run.Resolution) : null,
            MaximumReferences = ImageWorkflowLimits.MaximumReferences(workflow),
            References = item.References.Select((r, n) => new PromptReference(r.Reference,
                $"Image {n + 1} · {r.Context.AssetName} · {r.Context.LookName}", r.Context.Description, null) { Look = r.Context }).ToArray(),
            Loras = item.Loras,
            ProtectedTriggers = run.Settings.LoraLibrary.Where(d => item.Loras.Any(l => LoraPolicy.Same(d.Reference, l.Reference)))
                .Select(d => d.TriggerText).Where(t => !string.IsNullOrWhiteSpace(t) && prompt.Contains(t, StringComparison.Ordinal)).Distinct().ToArray()
        };
        var id = Guid.NewGuid();
        var submission = await text.EnhanceAssetBatchAsync(id, run.TabId, new(context, run.PromptModel!), run.Settings, ct);
        item = item with { State = AssetBatchItemState.Enhancing, PromptAttempts = item.PromptAttempts + 1,
            PromptJobs = [.. item.PromptJobs, id], Error = null };
        return FileAssetBatchStore.Replace(run, item) with { Pending = submission, PendingItemId = item.Id, Error = null };
    }

    private async Task<AssetBatch> ConsumePromptAsync(AssetBatch run, AssetBatchItem item, AiJobHeader job, CancellationToken ct)
    {
        var result = await jobs.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, ct);
        var prompt = result is { Error: null, Complete: true } && job.State == AiJobState.Completed ? result.Read<PromptEnhancementResult>() : null;
        if (prompt is { Kind: PromptEnhancementKind.Prompt } && !string.IsNullOrWhiteSpace(prompt.Text) && prompt.Text.Length <= 30000)
            item = item with { EnhancedPrompt = prompt.Text,
                State = run.State == AssetBatchState.Stopped ? AssetBatchItemState.Skipped : AssetBatchItemState.Pending, Error = null };
        else
            item = item with { State = AssetBatchItemState.NeedsAttention,
                Error = result?.Error ?? job.Error ?? (prompt is { Kind: not PromptEnhancementKind.Prompt } ? prompt.Text : "Prompt enhancement did not return a usable prompt. Inspect the saved response and retry.") };
        return FileAssetBatchStore.Replace(run, item) with { Pending = null, PendingItemId = null };
    }
}
