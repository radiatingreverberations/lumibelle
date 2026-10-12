using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    [Fact] public async Task ReelViewCaptureUsesStoredRecipeAndCroppedSourceImagesAndNeverAppliesPicks()
    {
        var media = new ReferenceVideoMedia(Guid.NewGuid(), new('a', 64), 100, 640, 360, 360, 24, 15, false);
        var recipe = new ReferenceReelDraft { AssetId = _asset.Id, Instructions = "Keep the podium and platform", Prompt = "Full circuit with holds",
            Images = [new() { AssetId = _asset.Id, MediaId = _asset.Images[0].Id, Crop = new() { Width = .5 } }] };
        var reel = new AssetReferenceReel { AssetId = _asset.Id, Name = "Plaza", Media = media, UseGuidance = "Environment geometry",
            Generation = new(recipe, Guid.NewGuid(), Guid.NewGuid(), 1, 42, null!) };
        _assets.Library = _assets.Library with { Reels = [reel] };
        var frames = new ViewMedia(media);
        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, referenceVideos: frames);
        var current = new ReelKeyframeSet();
        var submission = await capture.SuggestReelViewsAsync(Guid.NewGuid(), Guid.NewGuid(), _project, media.Id, current, 2, "Show the entrance", _model, true, _ct);
        var captured = AiTextJobHandler.Read(new() { Id = submission.Id, Kind = submission.Kind, Backend = submission.Backend, Target = submission.Target,
            ProjectName = submission.ProjectName, TargetName = submission.TargetName, OriginTabId = submission.OriginTabId, RequestFingerprint = "test" }, submission.Snapshot);
        var request = captured.Payload<ReelViewSuggestionRequest>();
        Assert.Equal("Keep the podium and platform", request.Instructions); Assert.Equal("Full circuit with holds", request.CameraPlan);
        Assert.Equal("Show the entrance", request.SelectionInstructions); Assert.Single(request.ReferenceHashes);
        var attached = captured.Messages.SelectMany(m => m.Parts).Where(p => p.Image is not null).Select(p => p.Image!).ToArray();
        Assert.Equal(3, attached.Length);
        Assert.Equal(40, SixLabors.ImageSharp.Image.Identify(attached[^1]).Width);
        Assert.Empty(current.Frames); Assert.Null(_assets.Library.Reels.Single().Keyframes);
        _providers.Chat.Output = """{"summary":"One useful view","views":[{"candidate":1,"name":"Toward platform","notes":"Platform beyond podium."}],"warnings":[]}""";
        var context = await Claim(submission);
        await Handler().ExecuteAsync(context, submission.Snapshot, _ct);
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(submission.Id, AiJobArtifact.Result, _ct);
        Assert.Null(result!.Error); Assert.Equal("Toward platform", result.Read<ReelViewSuggestionResult>()!.Views.Single().Name);
        Assert.Null(_assets.Library.Reels.Single().Keyframes);
    }
    private sealed class ViewMedia(ReferenceVideoMedia media) : IReferenceVideoStore
    {
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        public Task<ReelKeyframeSet> SuggestFramesAsync(Guid p, ReferenceVideoMedia m, int n, H3Settings s, CancellationToken ct = default) => Task.FromResult(new ReelKeyframeSet {
            Frames = [new() { Frame = new(media.Id, media.Sha256, 0, 0) }, new() { Frame = new(media.Id, media.Sha256, 168, 7) }] });
        public Task<AssetMedia> OpenFrameAsync(Guid p, ReelFrameIdentity f, H3Settings s, CancellationToken ct = default) => Task.FromResult(new AssetMedia(new MemoryStream(Png), "image/png", DateTime.UtcNow));
        public Task<ReferenceVideoMedia> ImportAsync(Guid p, Stream c, string n, H3Settings s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReferenceVideoMedia> CopyTakeAsync(Guid p, Guid t, H3Settings s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AssetMedia?> OpenAsync(Guid p, Guid m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateAsync(Guid p, IEnumerable<ShotVideoBinding> b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PrepareAsync(Guid p, Shot s, string d, List<PreparedVideoInput> i, H3Settings h, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
