using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static partial class AssetBatchPlan
{
    public static string Label(AssetBatchKind kind) => kind switch { AssetBatchKind.CharacterReels => "Character reels", AssetBatchKind.EnvironmentReels => "Environment reels", _ => "Images" };
    public static IReadOnlyList<AssetBatchPlanRow> BuildReels(AssetLibrary library, AssetBatchKind kind, ReelFraming framing, GenerationSetup? preset = null, VisualStyle? projectStyle = null, VisualStyle? runStyle = null)
    {
        if (kind is not (AssetBatchKind.CharacterReels or AssetBatchKind.EnvironmentReels)) throw new WorkspaceStoreException("Choose a reel batch type.");
        var category = kind == AssetBatchKind.CharacterReels ? AssetCategory.Character : AssetCategory.Environment;
        var rows = new List<AssetBatchPlanRow>();
        foreach (var imageRow in Build(library, ImageWorkflow.Krea2, projectStyle, runStyle).Where(r => r.Item.Category == category))
        {
            var item = imageRow.Item;
            var owner = library.Assets.Single(a => a.Id == item.Context.AssetId);
            var recipe = ReferenceReels.NewDraft(owner, item.Context.LookId);
            recipe.VisualStyle = item.VisualStyle;
            if (category == AssetCategory.Character) ReferenceReels.SelectCharacterPreset(recipe, framing);
            else ReferenceReels.SelectCameraPreset(recipe, framing);
            recipe.VoiceMode = ReelVoiceMode.Silent; recipe.Line = ""; recipe.Voice = null;
            recipe.Name = owner.Name + " · " + (item.Context.LookId is null ? "" : item.Context.LookName + " · ") + ReferenceReels.Label(framing);
            recipe.Images = item.References.Select(r => new ShotImageBinding { Kind = ShotImageKind.AssetImage, AssetId = r.Reference.AssetId,
                MediaId = r.Reference.ImageId, LookId = r.Context.LookId, Name = r.Context.AssetName, InferUsage = true }).ToList();
            if (preset is not null) ReelGenerationSetups.Apply(recipe, preset);
            var matching = library.Reels.Where(r => r.AssetId == owner.Id && r.LookId == item.Context.LookId && r.Generation?.Recipe.Framing == framing).ToArray();
            var complete = matching.Any(r => r.Keyframes?.Frames.Count > 0);
            var pendingReview = complete ? null : matching.OrderByDescending(r => r.CreatedUtc).FirstOrDefault();
            item = item with { Recipe = recipe, Prompt = "", Loras = [], ExistingReelId = pendingReview?.Id };
            string? issue = item.References.Count == 0 ? "Approve a reference image before generating this reel." : null;
            try { item = BuildReelPrompt(item, library); }
            catch (WorkspaceStoreException e) { issue ??= e.Message; }
            rows.Add(new(item, !complete, pendingReview is not null ? "A saved reel needs keyframe review. This batch can review it without generating again." : issue));
        }
        return rows;
    }

    public static AssetBatchItem BuildReelPrompt(AssetBatchItem item, AssetLibrary library)
    {
        var recipe = item.Recipe?.Copy() ?? throw new WorkspaceStoreException("Choose a reel recipe.");
        var pair = ReferenceReels.Preset(recipe, library);
        recipe.Prompt = pair.Prompt; recipe.UseGuidance = pair.UseGuidance;
        return item with { Recipe = recipe, Prompt = pair.Prompt };
    }
}
