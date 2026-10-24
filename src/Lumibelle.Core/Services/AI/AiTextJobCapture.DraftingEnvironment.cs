using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    public async Task<ShotDirectionContext?> CaptureDirectionContextAsync(ShotDocument document, Shot shot, CancellationToken ct = default)
    {
        if (ShotDirections.Context(document, shot)?.Environment is not { } accepted) return ShotDirections.Context(document, shot);
        string? issue;
        try {
            var ids = accepted.InspectedViews.Select(v => DraftingEnvironments.LiveCandidateId(accepted, v)).ToHashSet();
            var pool = await CaptureEnvironmentSourcesAsync(document.ProjectId, accepted.LiveAssetId, ids, ct);
            issue = DraftingEnvironments.LiveSourceIssue(accepted, pool);
        } catch (Exception e) when (e is WorkspaceStoreException or IOException or AiGenerationException) {
            issue = "Environment evidence is unavailable. Staging, continuity and reference support need review; the captured assessment is preserved in history. " + e.Message;
        }
        return ShotDirections.Context(document, shot, issue);
    }
    public async Task<bool> InspectionTooLargeAsync(AiJobSubmission submission, CancellationToken ct = default)
    {
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        if (ComfyTextCapacity.Assess(captured)?.Any(s => s.TooLarge) == true) return true;
        if (providers is null) return false;
        var check = await providers.CheckAsync(captured.Model.Backend, captured.Settings, cancellationToken: ct);
        var model = TextModelPolicy.CatalogModel(captured.Model, check);
        if (model?.SupportsImages != true) throw new WorkspaceStoreException(TextVisionPolicy.SetupHint);
        try { DraftingEnvironments.ValidateCapacity(captured, model.Catalog?.ContextLength); return false; }
        catch (WorkspaceStoreException) { return true; }
    }
    public async Task ValidateDraftingEnvironmentsAsync(ShotPlanningRequest request, Shot? shot = null, CancellationToken ct = default)
    {
        var document = await shots!.LoadAsync(request.Script.ProjectId, ct);
        foreach (var captured in request.Environments) {
            var current = DraftingEnvironments.Effective(shot, document.SceneSetups.FirstOrDefault(s => s.SceneId == captured.SceneId));
            if (current is null || DraftingEnvironments.Semantic(current.AssetId, captured.Guidance, current.Constraints, current.Views) != captured.Fingerprint)
                throw new WorkspaceStoreException("The scene's environment setup changed after drafting. Your draft is preserved; review a fresh draft before applying.");
        }
        _ = await DraftingMessagesAsync(request, ct);
    }
    public async Task<AiJobSubmission> SelectSceneEnvironmentViewsAsync(Guid id, Guid tab, SceneEnvironmentSelectionRequest request,
        EnvironmentSourcePool pool, TextModelReference model, bool followsDefault, CancellationToken ct = default)
    {
        if (!TextVisionPolicy.SupportsBackend(model.Backend)) throw new WorkspaceStoreException(TextVisionPolicy.SetupHint);
        return await BuildAsync(id, tab, AiJobKind.SceneEnvironmentSelection, new(request.ProjectId, ShotId: request.ShotId, SceneId: request.SceneId),
            request.AssetName + " · Scene inspection views", request, model, followsDefault, Copy(await settings.LoadAsync(ct)),
            DraftingEnvironments.SelectionProfile, DraftingEnvironments.SelectionMessages(request, request.Candidates.Select(c => pool.Pictures[c.Id]).ToArray()), .3f, null, ct);
    }
    public async Task<IReadOnlyList<DraftingEnvironmentSnapshot>> CaptureDraftingEnvironmentsAsync(Guid projectId,
        IReadOnlyList<Guid> scenes, Shot? shot = null, CancellationToken ct = default)
    {
        var document = await shots!.LoadAsync(projectId, ct);
        var result = new List<DraftingEnvironmentSnapshot>();
        foreach (var sceneId in scenes) {
            var setup = DraftingEnvironments.Effective(shot, document.SceneSetups.FirstOrDefault(s => s.SceneId == sceneId));
            if (setup is null) continue;
            var pool = await CaptureEnvironmentSourcesAsync(projectId, setup.AssetId, ct);
            result.Add(DraftingEnvironments.Snapshot(sceneId, setup, pool));
        }
        return result;
    }
    private async Task<List<ChatMessage>> DraftingMessagesAsync(ShotPlanningRequest request, CancellationToken ct)
    {
        var pictures = new Dictionary<string, byte[]>();
        foreach (var environment in request.Environments) {
            if (!request.SceneIds.Contains(environment.SceneId) || environment.Fingerprint !=
                DraftingEnvironments.Semantic(environment.AssetId, environment.Guidance, environment.Constraints, environment.Views))
                throw new WorkspaceStoreException("The captured scene environment is incomplete.");
            var current = await CaptureEnvironmentSourcesAsync(request.Script.ProjectId, environment.AssetId, ct);
            if (current.Guidance != environment.Guidance) throw new WorkspaceStoreException("Environment guidance changed. Review the environment setup again.");
            foreach (var view in environment.Views) {
                var actual = current.Candidates.SingleOrDefault(c => c.Id == view.Id);
                if (actual is null || actual.Sha256 != view.Sha256 || actual.Name != view.Name || actual.Notes != view.Notes || actual.Approved != view.Approved)
                    throw new WorkspaceStoreException("A selected environment view changed. Review the environment setup again.");
                pictures.TryAdd(view.Sha256, current.Pictures[view.Id]);
            }
        }
        return ShotPlanner.BuildMessages(request, pictures);
    }
}
