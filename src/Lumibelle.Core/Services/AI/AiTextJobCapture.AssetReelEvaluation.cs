using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobCapture
{
    public async Task<AiJobSubmission> EvaluateAssetReelAsync(Guid id, Guid tab, Guid project, Guid batch, AssetBatchItem item,
        Guid reelId, TextModelReference model, AiSettings configured, CancellationToken ct = default)
    {
        configured = Copy(configured); model = Copy(model); item = Copy(item);
        var check = await (providers ?? throw new AiGenerationException("Model availability is unavailable.")).CheckAsync(model.Backend, configured, cancellationToken: ct);
        if (TextModelPolicy.Issue(model, configured, check) is { } issue) throw new AiGenerationException(issue);
        if (!TextVisionPolicy.SupportsBackend(model.Backend) || TextModelPolicy.CatalogModel(model, check)?.SupportsImages != true) throw new AiGenerationException(TextVisionPolicy.SetupHint);
        var library = await assets.LoadAsync(project, ct); LookPolicy.ValidateTarget(library, item.Context, requireCurrent: true);
        var reel = library.Reels.SingleOrDefault(r => r.Id == reelId && r.AssetId == item.Context.AssetId && r.LookId == item.Context.LookId)
            ?? throw new WorkspaceStoreException("Restore this reel before inspecting it.");
        var store = referenceVideos ?? throw new WorkspaceStoreException("Reel frame extraction is unavailable.");
        var frames = await store.SuggestFramesAsync(project, reel.Media, 9, configured.H3, ct);
        if (frames.Frames.Count == 0) throw new WorkspaceStoreException("No sampled frames are available. Review the video and choose keyframes manually.");
        List<byte[]> images = []; List<ReelViewCandidate> candidates = []; List<string> hashes = [];
        foreach (var frame in frames.Frames.Take(9))
        {
            await using var media = await store.OpenFrameAsync(project, frame.Frame, configured.H3, ct);
            var png = await ComfyReferenceImageEditor.PrepareSourcePngAsync(media.Content, null, ct);
            images.Add(png); candidates.Add(new(candidates.Count + 1, ShotCopy.Of(frame), ReelViewSuggestions.Hash(png)));
        }
        foreach (var reference in item.References.Take(2))
        {
            var png = await PrepareImageAsync(project, reference.Reference, null, ct); images.Add(png); hashes.Add(ReelViewSuggestions.Hash(png));
        }
        var recipe = reel.Generation?.Recipe ?? item.Recipe!;
        var style = reel.Generation is { } generation ? generation.Recipe.CapturedVisualStyle ?? generation.Recipe.VisualStyle : item.VisualStyle;
        var views = new ReelViewSuggestionRequest(1, project, item.Context.AssetId, reel.Id, reel.Media, reel.Name, item.Context.AssetName,
            item.Context.IdentityNotes, reel.UseGuidance, VisualStyles.Apply(recipe.Prompt, style), recipe.Instructions, ReferenceReels.Label(recipe.Framing), 9,
            ReelViewSuggestions.Baseline(reel.Keyframes ?? new()), candidates, hashes);
        var sheet = await AssetImageEvaluator.ContactSheetAsync(images, ct);
        var request = new AssetReelEvaluationRequest(batch, item.Id, item.Context, views, AssetImageEvaluator.Hash(sheet));
        var submission = await BuildAsync(id, tab, AiJobKind.AssetReelEvaluation, new(project, item.Context.AssetId, ReelId: reel.Id),
            item.Context.AssetName + " · Review reel", request, model, false, configured, AssetReelEvaluator.Profile, AssetReelEvaluator.Messages(request, images, sheet), .2f, null, ct);
        var snapshot = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        request = request with { ContactSheetHash = AssetImageEvaluator.Hash(snapshot.Messages.SelectMany(m => m.Parts).Single(p => p.Image is not null).Image!) };
        return submission with { Snapshot = JsonSerializer.SerializeToElement(snapshot with { Task = JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options) }, AtomicJsonFile.Options) };
    }
}
