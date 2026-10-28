using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;

namespace lumibelle.Components.Assets;

public partial class AssetBatchPanel
{
    private async Task LoadPresets()
    {
        _presets = await GenerationSetups.LoadAsync(_lifetime.Token);
        if (_presetId is null) _presetId = _presets.Setups.FirstOrDefault(s => s.Id == _presets.SelectedId && !s.Archived)?.Id;
    }
    private Task ChangeKind() => Act(async () =>
    {
        _categoryFilter = null;
        _framing = _kind == AssetBatchKind.EnvironmentReels ? ReelFraming.EnvironmentFullSet : ReelFraming.CharacterNeutralTurntable;
        await LoadPresets(); RebuildPlan();
    });
    private void EditRecipe(Guid id, Action<ReferenceReelDraft> edit)
    {
        var item = _drafts[id]; var recipe = item.Recipe!.Copy(); edit(recipe);
        _drafts[id] = item with { Recipe = recipe, Prompt = "" };
    }
    private void RecipeDuration(Guid id, string? text) { if (double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var seconds)) EditRecipe(id, r => r.Duration = seconds); }
    private void UpdateRecipePrompt(Guid id)
    {
        try { _drafts[id] = AssetBatchPlan.BuildReelPrompt(_drafts[id], _library!); _error = null; }
        catch (Exception e) { _error = e.Message; }
    }
    private void UseExisting(Guid id, Guid? existing, bool value)
    {
        _drafts[id] = _drafts[id] with { ExistingReelId = value ? existing : null };
        if (!value) UpdateRecipePrompt(id);
    }
    private IEnumerable<AssetReferenceReel> VisibleReels(AssetBatchItem item) => (_library?.Reels ?? [])
        .Where(r => r.AssetId == item.Context.AssetId && r.LookId == item.Context.LookId && (item.ReelCandidates.Contains(r.Id) || item.GenerationJobs.Contains(r.Generation?.JobId ?? Guid.Empty)));
    private Task ApproveViews(Guid item, Guid reel, int[] selected) => Act(async () => { await Workflow.ApproveReelViewsAsync(ProjectId, _run!.Id, item, reel, selected, _lifetime.Token); });
    private void ReviewKeyframes(Guid item, AssetReferenceReel reel) { _keyframeItem = item; _keyframeReel = reel; _open = false; }
    private async Task CloseKeyframes() { _keyframeReel = null; _open = true; await RefreshAsync(); }
    private Task SaveKeyframes(ReelKeyframeSet frames) => Act(async () =>
    {
        await Workflow.SaveManualReelViewsAsync(ProjectId, _run!.Id, _keyframeItem, _keyframeReel!, frames, _lifetime.Token);
        await CloseKeyframes();
    });
}
