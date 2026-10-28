using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BulkReelEvaluationUsesOneBoundSheetAndNeverAppliesSuggestedViews(bool styled)
    {
        var media = new ReferenceVideoMedia(Guid.NewGuid(), new('a', 64), 100, 640, 360, 241, 24, 241/24d, false);
        var recipe = new ReferenceReelDraft { AssetId = _asset.Id, Prompt = "Held front and side views", Instructions = "Preserve the green outline",
            CapturedVisualStyle = styled ? new() { Preset = VisualStylePreset.Illustration, Direction = "Ink wash." } : null };
        var reel = new AssetReferenceReel { AssetId = _asset.Id, Name = "Juniper reference", Media = media,
            Generation = new(recipe, Guid.NewGuid(), Guid.NewGuid(), 1, 42, null!) };
        _assets.Library = _assets.Library with { Reels = [reel] };
        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, providers: _providers, referenceVideos: new ViewMedia(media));
        var item = new AssetBatchItem { Context = LookPolicy.Capture(_asset, null), Prompt = recipe.Prompt, Recipe = recipe,
            VisualStyle = new() { Preset = VisualStylePreset.Photographic },
            References = [new(new(_asset.Id, _asset.Images[0].Id), LookPolicy.Capture(_asset, null))], ReelCandidates = [reel.Id] };
        var submitted = await capture.EvaluateAssetReelAsync(Guid.NewGuid(), Guid.NewGuid(), _project, Guid.NewGuid(), item, reel.Id, _model, _settings.Value, _ct);
        var snapshot = submitted.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var request = snapshot.Payload<AssetReelEvaluationRequest>();
        Assert.DoesNotContain("Photographic", request.Views.CameraPlan);
        if (styled) Assert.Contains("Ink wash.", request.Views.CameraPlan);
        else Assert.Equal(recipe.Prompt, request.Views.CameraPlan);
        Assert.Equal(2, request.Views.Candidates.Count); Assert.Single(request.Views.ReferenceHashes);
        var image = Assert.Single(snapshot.Messages.SelectMany(m => m.Parts), p => p.Image is not null);
        Assert.Equal(request.ContactSheetHash, AssetImageEvaluator.Hash(image.Image!));
        _providers.Chat.Output = JsonSerializer.Serialize(new { verdict = "CannotJudge", summary = "Two sampled views only", checks =
            AssetReelEvaluator.CheckNames.Select(name => new { name, verdict = "CannotJudge", reason = "Limited sampled evidence" }),
            views = new[] { new { candidate = 1, name = "Front", notes = "Readable silhouette" } }, warnings = new[] { "Watch the video" } });
        var context = await Claim(submitted); await Handler().ExecuteAsync(context, submitted.Snapshot, _ct);
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(submitted.Id, AiJobArtifact.Result, _ct);
        Assert.Null(result!.Error); Assert.Single(result.Read<AssetReelEvaluation>()!.Selection.Views);
        Assert.Equal(0, _assets.SaveCalls); Assert.Null(_assets.Library.Reels[0].Keyframes);
        Assert.Throws<WorkspaceStoreException>(() => AssetReelEvaluator.Parse(_providers.Chat.Output.Replace("\"candidate\":1", "\"candidate\":999"), request));
        Assert.Throws<WorkspaceStoreException>(() => AssetReelEvaluator.Parse(_providers.Chat.Output[..^3], request));
        var wrong = snapshot with { Task = JsonSerializer.SerializeToElement(request with { ContactSheetHash = new string('0', 64) }, AtomicJsonFile.Options) };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Handler().ExecuteAsync(context, JsonSerializer.SerializeToElement(wrong, AtomicJsonFile.Options), _ct));
    }
}
