using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    public async Task<AiJobSubmission> SuggestReelViewsAsync(Guid id, Guid tab, Guid project, Guid mediaId,
        ReelKeyframeSet current, int count, string instructions, TextModelReference model, bool followsDefault, CancellationToken ct = default)
    {
        current = ShotCopy.Of(current);
        if (count is < 1 or > 9 || instructions.Length > 12000) throw new WorkspaceStoreException("Choose one to nine views and shorter selection instructions.");
        if (!TextVisionPolicy.SupportsBackend(model.Backend)) throw new WorkspaceStoreException(TextVisionPolicy.SetupHint);
        var library = await assets.LoadAsync(project, ct);
        var reel = library.Reels.SingleOrDefault(r => r.Media.Id == mediaId) ?? throw new WorkspaceStoreException("Restore the source reel before suggesting views.");
        var owner = library.Assets.Single(a => a.Id == reel.AssetId);
        ReferenceVideos.ValidateKeyframes(reel.Media, current);
        var configured = Copy(await settings.LoadAsync(ct));
        var store = referenceVideos ?? throw new WorkspaceStoreException("Reference video storage is unavailable.");
        var candidates = await store.SuggestFramesAsync(project, reel.Media, 9, configured.H3, ct);
        if (candidates.Frames.Count == 0) throw new WorkspaceStoreException("No usable candidate views were found. Try choosing frames manually.");
        var images = new List<byte[]>(); var captured = new List<ReelViewCandidate>();
        foreach (var frame in candidates.Frames)
        {
            await using var media = await store.OpenFrameAsync(project, frame.Frame, configured.H3, ct);
            using var bytes = new MemoryStream(); await media.Content.CopyToAsync(bytes, ct);
            var png = bytes.ToArray(); images.Add(png); captured.Add(new(captured.Count + 1, frame, ReelViewSuggestions.Hash(png)));
        }
        var refs = new List<string>();
        foreach (var binding in reel.Generation?.Recipe.Images ?? [])
        {
            // Original references are helpful evidence but old/missing source images must not make a saved reel unusable.
            var sourceOwner = AssetImageLocations.RecordedOwner(library, new(binding.AssetId, binding.MediaId));
            if (sourceOwner is null) continue;
            var png = await PrepareImageAsync(project, new(sourceOwner.Id, binding.MediaId), binding.Crop, ct);
            images.Add(png); refs.Add(ReelViewSuggestions.Hash(png));
        }
        var recipe = reel.Generation?.Recipe;
        var request = new ReelViewSuggestionRequest(1, project, owner.Id, reel.Id, reel.Media, reel.Name, owner.Name,
            owner.Description, reel.UseGuidance, recipe?.Prompt ?? "", recipe?.Instructions ?? "", instructions, count,
            ReelViewSuggestions.Baseline(current), captured, refs);
        return await BuildAsync(id, tab, AiJobKind.ReelViewSuggestion, new(project, owner.Id, ReelId: reel.Id),
            reel.Name + " · Suggest views", request, model, followsDefault, configured, ReelViewSuggestions.Profile,
            ReelViewSuggestions.Messages(request, images), .3f, null, ct);
    }
}
