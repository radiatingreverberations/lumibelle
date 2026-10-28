using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    private AssetBatchItem ReviewItem => new() { Context = LookPolicy.Capture(_asset, null), Category = _asset.Category,
        Prompt = "A small green robot against a plain background", Candidates = _asset.Images.Select(i => i.Id).ToArray() };
    private static string ImageReviewReply(AssetImageEvaluationRequest request, Guid? substitute = null) => JsonSerializer.Serialize(new {
        candidates = request.CandidateIds.Select((id, i) => new { imageId = substitute ?? id, rank = i + 1, verdict = "Pass", summary = "Clear silhouette", checks =
            AssetImageEvaluator.CheckNames.Select(name => new { name, verdict = name == "reference" ? "CannotJudge" : "Pass", reason = name == "reference" ? "No reference supplied" : "Visible and consistent" }) }),
        recommendedImageId = request.CandidateIds[0] });

    [Fact]
    public async Task ImageEvaluationUsesTheCapturedBatchFramingInstructions()
    {
        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, providers: _providers);
        var item = ReviewItem with { AdditionalInstructions = "Full-body view, entire figure visible.", VisualStyle = new() { Preset = VisualStylePreset.Photographic } };
        var submission = await capture.EvaluateAssetImagesAsync(Guid.NewGuid(), Guid.NewGuid(), _project, Guid.NewGuid(), item, _model, _settings.Value, _ct);
        var snapshot = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var request = snapshot.Payload<AssetImageEvaluationRequest>();
        Assert.Contains(item.AdditionalInstructions, request.Prompt);
        Assert.Contains("Photographic imagery", request.Prompt);
        Assert.DoesNotContain(item.AdditionalInstructions, item.Prompt);
        Assert.Contains(snapshot.Messages.SelectMany(m => m.Parts), part => part.Text?.Contains(item.AdditionalInstructions, StringComparison.Ordinal) == true);
        Assert.Equal(0, _assets.SaveCalls);
    }

    [Fact]
    public async Task ImageEvaluationCapturesOneBoundSheetAndPersistsAdvisoryResultsWithoutApproval()
    {
        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, providers: _providers);
        var submission = await capture.EvaluateAssetImagesAsync(Guid.NewGuid(), Guid.NewGuid(), _project, Guid.NewGuid(), ReviewItem, _model, _settings.Value, _ct);
        var snapshot = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var request = snapshot.Payload<AssetImageEvaluationRequest>();
        Assert.Equal(2, request.Tiles.Count); Assert.Equal(AssetImageEvaluator.Profile, snapshot.Profile);
        Assert.Equal(request.ContactSheetHash, AssetImageEvaluator.Hash(snapshot.Messages.SelectMany(m => m.Parts).Single(p => p.Image is not null).Image!));
        _providers.Chat.Output = ImageReviewReply(request);
        var context = await Claim(submission); await Handler().ExecuteAsync(context, submission.Snapshot, _ct);
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct);
        Assert.Null(result!.Error); Assert.Equal(2, result.Read<AssetImageEvaluation>()!.Candidates.Count); Assert.Equal(0, _assets.SaveCalls);
        Assert.All(_assets.Library.Assets[0].Images, image => Assert.False(image.IsReference));
        var changed = snapshot with { Task = JsonSerializer.SerializeToElement(request with { ContactSheetHash = new string('0', 64) }, AtomicJsonFile.Options) };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Handler().ExecuteAsync(context, JsonSerializer.SerializeToElement(changed, AtomicJsonFile.Options), _ct));
    }

    [Fact]
    public async Task ImageEvaluationRequiresVerifiedVisionAndRejectsWrongIdsTruncationAndMissingChecks()
    {
        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, providers: _providers);
        _providers.Vision = false;
        await Assert.ThrowsAsync<AiGenerationException>(() => capture.EvaluateAssetImagesAsync(Guid.NewGuid(), Guid.NewGuid(), _project, Guid.NewGuid(), ReviewItem, _model, _settings.Value, _ct));
        var (request, _) = await AssetImageEvaluator.CaptureAsync(_assets, _project, Guid.NewGuid(), ReviewItem, _ct);
        Assert.Throws<WorkspaceStoreException>(() => AssetImageEvaluator.Parse(ImageReviewReply(request, Guid.NewGuid()), request));
        Assert.Throws<WorkspaceStoreException>(() => AssetImageEvaluator.Parse(ImageReviewReply(request)[..^3], request));
        Assert.Throws<WorkspaceStoreException>(() => AssetImageEvaluator.Parse(ImageReviewReply(request).Replace("framing", "unexpected"), request));
        Assert.Equal(request.CandidateIds[0], AssetImageEvaluator.Parse(ImageReviewReply(request), request).RecommendedImageId);
    }
}
