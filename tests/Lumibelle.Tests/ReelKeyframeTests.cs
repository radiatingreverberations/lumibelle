using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

internal sealed class ReferenceEditorMediaFake : IReferenceVideoStore
{
    public Task<ReelKeyframeSet> SuggestFramesAsync(Guid p, ReferenceVideoMedia m, int count, H3Settings s, CancellationToken ct = default)
        => Task.FromResult(new ReelKeyframeSet { Frames = [new() { Frame = new(m.Id, m.Sha256, 0, 0) }] });
    public Task PrepareFramesAsync(Guid p, IEnumerable<ReelFrameIdentity> f, H3Settings s, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ReferenceVideoMedia> ImportAsync(Guid p, Stream c, string n, H3Settings s, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ReferenceVideoMedia> CopyTakeAsync(Guid p, Guid t, H3Settings s, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AssetMedia?> OpenAsync(Guid p, Guid m, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ValidateAsync(Guid p, IEnumerable<ShotVideoBinding> b, CancellationToken ct = default) => Task.CompletedTask;
    public Task PrepareAsync(Guid p, Shot s, string d, List<PreparedVideoInput> i, H3Settings h, CancellationToken ct = default) => throw new NotSupportedException();
}

public sealed class ReelKeyframeTests
{
    private static ReelFrameCandidate Candidate(int i, float view = .4f, double sharpness = .1) => new(i, i / 8d, sharpness, .2, .5, Enumerable.Repeat(view, 12).ToArray());
    [Fact] public void StaticViewsAreNotPaddedAndResultsAreDeterministic()
    {
        var candidates = Enumerable.Range(0, 40).Select(i => Candidate(i)).ToArray();
        Assert.Single(ReelFrameSelector.Select(candidates, 9));
        Assert.Equal(ReelFrameSelector.Select(candidates, 3), ReelFrameSelector.Select(candidates.Reverse().ToArray(), 3));
        Assert.Empty(ReelFrameSelector.Select(candidates.Select(c => c with { Contrast = 0 }).ToArray(), 3));
    }
    [Fact] public void ThreeHeldViewsPreferSharpRepresentativesAndExcludeFlashes()
    {
        var candidates = Enumerable.Range(0, 48).Select(i => Candidate(i, i < 16 ? .2f : i < 32 ? .45f : .7f, i % 16 == 5 ? .3 : .1)).ToArray();
        candidates[10] = Candidate(10, 1, 10) with { Luminance = 1 }; // Flash.
        candidates[35] = Candidate(35, .95f, 3); // Brief corrupted view between matching neighbors.
        var selected = ReelFrameSelector.Select(candidates, 3);
        Assert.Equal(3, selected.Count); Assert.DoesNotContain(selected, f => f.Index is 10 or 35);
        Assert.Contains(selected, f => f.Index < 16); Assert.Contains(selected, f => f.Index is >= 16 and < 32); Assert.Contains(selected, f => f.Index >= 32);
    }
    [Fact] public void ContinuousTurnUsesTemporalCoverageAndExactOriginalIndices()
    {
        var candidates = Enumerable.Range(0, 80).Select(i => Candidate(i, .1f + i * .01f)).ToArray();
        var selected = ReelFrameSelector.Select(candidates, 3);
        Assert.Equal(3, selected.Count); Assert.True(selected[^1].Index - selected[0].Index > 50);
        var times = Enumerable.Range(0, 150).Select(i => i / 30d).ToArray();
        var sampled = ReelFrameSelector.Sample(times);
        Assert.InRange(sampled.Count, 39, 41); Assert.Equal(sampled, sampled.Distinct().Order());
    }
    [Fact] public void DescriptionMeasuresRealImageDetail()
    {
        using var image = new Image<Rgb24>(96, 64, new(120, 120, 120));
        using var plain = new MemoryStream(); image.SaveAsPng(plain); plain.Position = 0;
        var flat = ReelFrameSelector.Describe(0, 0, plain);
        for (var y = 0; y < 64; y++) for (var x = 0; x < 96; x++) image[x, y] = (x / 4 + y / 4) % 2 == 0 ? new(230, 170, 150) : new(20, 40, 70);
        using var detail = new MemoryStream(); image.SaveAsPng(detail); detail.Position = 0;
        var sharp = ReelFrameSelector.Describe(1, .125, detail);
        Assert.True(sharp.Sharpness > flat.Sharpness); Assert.True(sharp.Contrast > flat.Contrast);
        Assert.True(ReelFrameSelector.Difference(flat, sharp) > .055);
    }
    [Fact] public void FadesBlurAndBlendedTransitionsDoNotDisplaceStableViews()
    {
        var stable = Candidate(0) with { Appearance = [0, .8f, .2f, .9f], Contrast = .3, Sharpness = .4 };
        var faded = stable with { Index = 1, Seconds = .125, Appearance = [0, .16f, .04f, .18f], Contrast = .06, Sharpness = .016 };
        var nearDuplicate = stable with { Index = 2, Seconds = .25, Appearance = [.01f, .79f, .21f, .88f] };
        var closeUp = stable with { Index = 4, Seconds = .5, Appearance = [.8f, 0, .9f, .1f] };
        var transition = stable with { Index = 3, Seconds = .375, Appearance = [.4f, .4f, .55f, .5f], Contrast = .04, Sharpness = .005 };
        var blurred = closeUp with { Index = 5, Seconds = .625, Sharpness = .00001 };
        var selected = ReelFrameSelector.Select([stable, faded, nearDuplicate, transition, closeUp, blurred], 9);
        Assert.Equal(new[] { 0, 4 }, selected.Select(c => c.Index));
    }
    [Fact] public void LegacyRecipesAndBindingsKeepTheirSerializedBehavior()
    {
        var binding = new ShotVideoBinding(); var json = JsonSerializer.Serialize(binding, AtomicJsonFile.Options);
        Assert.DoesNotContain("keyframes", json); Assert.DoesNotContain("visuals", json); Assert.DoesNotContain("audioExcerpt", json);
        Assert.Equal(ReelVisuals.FullReel, JsonSerializer.Deserialize<ShotVideoBinding>(json, AtomicJsonFile.Options)!.EffectiveVisuals);
        var draft = new ReferenceReelDraft(); Assert.DoesNotContain("saveLosslessFrames", JsonSerializer.Serialize(draft, AtomicJsonFile.Options));
        Assert.False(ReferenceReels.Inputs(draft).SaveLosslessFrames);
        var fresh = ReferenceReels.NewDraft(new() { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment }, null);
        Assert.True(fresh.SaveLosslessFrames); Assert.True(ReferenceReels.Inputs(fresh).SaveLosslessFrames);
    }
}

public sealed partial class ShotTests
{
    [Fact] public void SurveyFramesBeyondTheTargetDurationRemainAvailableAsPictures()
    {
        var reel = Clip(false); reel.Media = reel.Media with { Duration = 362 / 24d, Frames = 362, Fps = 24 };
        reel.OwnerCategory = AssetCategory.Environment;
        var shot = Ready() with { Duration = 5, Videos = [reel] };
        Assert.Equal(124, ReferenceVideos.EffectiveFrames(reel.Media, shot));
        reel.Visuals = ReelVisuals.Keyframes;
        reel.Keyframes = new() { Frames = [new() { Frame = new(reel.Media.Id, reel.Media.Sha256, 336, 14), Notes = "Opposite wall" }] };
        ReferenceVideos.Validate(shot);
        var plan = ResolvedReferences.For(shot);
        Assert.Empty(plan.Videos); Assert.Equal(14, plan.Pictures.Single().Keyframe!.Frame.Seconds);
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(ReferenceSnapshot(shot), 42, "late-frame",
            [new("late-frame.png", false) { Kind = VideoInputKind.Image }])).GetProperty("prompt");
        Assert.DoesNotContain("LoadVideo", graph.ToString());
        Assert.True(graph.GetProperty("5").GetProperty("inputs").TryGetProperty("ref_images.ref_image_0", out _));
    }
    private static ShotVideoBinding KeyframeClip(int count = 2, bool audio = true)
    {
        var clip = Clip(audio); clip.Visuals = ReelVisuals.Keyframes; clip.OwnerCategory = AssetCategory.Environment;
        clip.Keyframes = new() { Frames = Enumerable.Range(0, count).Select(i => new ReelKeyframe { Frame = new(clip.Media.Id, clip.Media.Sha256, i * 12, i * .5) }).ToList() };
        clip.AudioExcerpt = new(.5, 2); return clip;
    }
    [Fact] public void KeyframePicturesAndIndependentAudioReachNativeH3WithoutVideoNodes()
    {
        var shot = Ready() with { Duration = 2, Videos = [KeyframeClip()], Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Normal picture" }] };
        var resolved = ResolvedReferences.For(shot); Assert.Equal(3, resolved.Pictures.Count); Assert.Empty(resolved.Videos); Assert.Single(resolved.Audio);
        var inputs = resolved.InputOrder().Select((i, n) => new PreparedVideoInput($"ref-{n}{AiVideoJobPolicy.Extension(i.Kind)}", i.Kind == VideoInputKind.Audio) { Kind = i.Kind, VideoIndex = i.VideoIndex }).ToArray();
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(ReferenceSnapshot(shot), 42, "keyframes", inputs)).GetProperty("prompt");
        Assert.DoesNotContain("LoadVideo", graph.ToString()); Assert.DoesNotContain("ref_videos.ref_video_", graph.ToString());
        Assert.Equal("102", graph.GetProperty("5").GetProperty("inputs").GetProperty("ref_images.ref_image_2")[0].GetString());
        Assert.Equal("103", graph.GetProperty("5").GetProperty("inputs").GetProperty("ref_audios.ref_audio_0")[0].GetString());
        // No ordinary-image guidance is needed when isolating the keyframe composition context.
        shot.Images.Clear(); resolved = ResolvedReferences.For(shot);
        shot.Videos[0].Keyframes!.Frames[0].Name = "Toward judge platform";
        shot.Videos[0].Keyframes!.Frames[0].Notes = "Raised platform beyond the podium";
        var request = new PromptCompositionRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "context", "source", shot, "Scene", [], [], [], resolved.Pictures.Select(p => new CompositionInput(p.BindingId, "hash")).ToArray(), "", "", "", new(AiBackend.OpenRouter, "vision", "Vision"));
        var messages = PromptComposer.BuildMessages(request, [new byte[] { 1 }, new byte[] { 2 }]);
        Assert.Equal(2, messages[1].Contents.OfType<Microsoft.Extensions.AI.DataContent>().Count());
        using var context = JsonDocument.Parse(messages[1].Text!);
        Assert.Empty(context.RootElement.GetProperty("videos").EnumerateArray());
        Assert.Equal("Environment", context.RootElement.GetProperty("reelKeyframes")[0].GetProperty("ownerType").GetString());
        Assert.Equal("Toward judge platform", context.RootElement.GetProperty("reelKeyframes")[0].GetProperty("name").GetString());
        Assert.Equal("Raised platform beyond the podium", context.RootElement.GetProperty("reelKeyframes")[0].GetProperty("notes").GetString());
        Assert.Equal(2, context.RootElement.GetProperty("reelAudio")[0].GetProperty("excerpt").GetProperty("duration").GetDouble());
    }
    [Fact] public void MixedRepresentationsKeepCompactVideoIndicesAndNativeAudioOrder()
    {
        var shot = Ready() with { Videos = [KeyframeClip(1), Clip(), KeyframeClip(1, false)] };
        var plan = ResolvedReferences.For(shot);
        Assert.Equal(1, plan.Videos.Single().Number); Assert.Equal(1, plan.Videos.Single().BindingIndex);
        Assert.Equal(2, ReferenceVideos.SoundtrackNumber(shot, 0)); Assert.Equal(1, ReferenceVideos.SoundtrackNumber(shot, 1));
        var inputs = plan.InputOrder().Select((i, n) => new PreparedVideoInput($"ref-{n}{AiVideoJobPolicy.Extension(i.Kind)}", i.Kind is VideoInputKind.Audio or VideoInputKind.VideoSoundtrack) { Kind = i.Kind, VideoIndex = i.VideoIndex }).ToArray();
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(ReferenceSnapshot(shot), 1, "mixed", inputs)).GetProperty("prompt");
        var ports = graph.GetProperty("5").GetProperty("inputs");
        Assert.True(ports.TryGetProperty("ref_videos.ref_video_0", out _)); Assert.False(ports.TryGetProperty("ref_videos.ref_video_1", out _));
        Assert.True(ports.TryGetProperty("ref_video_audios.ref_video_audio_0", out _));
        shot.Videos[0].Visuals = ReelVisuals.None; Assert.Single(ResolvedReferences.For(shot).Pictures);
        shot.Videos[0].UseSoundtrack = false; Assert.Throws<WorkspaceStoreException>(() => ReferenceVideos.Validate(shot));
    }
    [Fact] public void EffectivePictureLimitsAndAudioExcerptsAreValidated()
    {
        var shot = Ready() with { Videos = [KeyframeClip(9)], Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid() }] };
        Assert.Throws<WorkspaceStoreException>(() => ReferenceVideos.Validate(shot)); shot.Images.Clear(); ReferenceVideos.Validate(shot);
        shot.Videos[0].AudioExcerpt = new(4, 3); Assert.Throws<WorkspaceStoreException>(() => ReferenceVideos.Validate(shot));
        shot.Videos[0].AudioExcerpt = new(0, 3); ReferenceVideos.Validate(shot);
        shot.Videos[0].Keyframes!.Frames.Add(shot.Videos[0].Keyframes!.Frames[0]); Assert.Throws<WorkspaceStoreException>(() => ReferenceVideos.Validate(shot));
    }
}
