using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

// A continuity picture is a take frame used as a Picture reference, read from the take instead of saved to Assets.
public sealed partial class ShotTests
{
    private static ShotContinuityFrame Continuity(Guid take, int frame = 0) => new(Guid.NewGuid(), take, frame, "Sip · last frame");

    [Fact]
    public void AContinuityPictureFollowsTheImagesAndPrecedesReelKeyframes()
    {
        var reel = Clip(false); reel.Visuals = ReelVisuals.Keyframes; reel.OwnerCategory = AssetCategory.Environment;
        reel.Keyframes = new() { Frames = [new() { Frame = new(reel.Media.Id, reel.Media.Sha256, 12, .5) }] };
        var shot = Ready() with { Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Face" }], Videos = [reel] };
        shot.ShotFrames = [Continuity(Guid.NewGuid())];
        var pictures = ResolvedReferences.For(shot).Pictures;
        Assert.Equal(3, pictures.Count);
        Assert.Equal((2, shot.ShotFrames[0].Id), (pictures[1].Number, pictures[1].BindingId));
        Assert.Same(shot.ShotFrames[0], pictures[1].Continuity); Assert.NotNull(pictures[2].Keyframe);
        Assert.All(ResolvedReferences.For(shot).InputOrder(), i => Assert.Equal(VideoInputKind.Image, i.Kind));
        Assert.DoesNotContain("continuityFrame", JsonSerializer.Serialize(Ready(), AtomicJsonFile.Options));
        // It is a reference of the shot's setups, saved with the other references.
        var composition = new ProductionComposition { ShotId = shot.Id, Shot = shot.Copy() };
        var restored = new ProductionComposition { ShotId = shot.Id, Shot = Ready() };
        ShotProductionContent.From(composition).Apply(restored);
        Assert.Equal(shot.ShotFrames, restored.Shot.ShotFrames);
        // Copying another shot's references never carries over the picture of the shot before that one.
        Assert.Empty(ReferenceCopies.Into(shot, Ready(), new() { ProjectId = Guid.NewGuid() }).Inputs.ShotFrames);
    }

    [Fact]
    public async Task AContinuityPictureIsCapturedAndPreparedAsTheSameImage()
    {
        var f = Fixture(); var source = Ready(); var next = Ready();
        var doc = await f.Shots.SaveAsync(f.Project.Id, [source, next], 0, ct: _ct);
        var take = Assert.Single((await AddTake(f.Project.Id, f.Shots, source)).Takes);
        next.ShotFrames = [Continuity(take.Id, take.FrameCount - 1)];
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => ProductionInputs.CaptureAsync(f.Project.Id, next, f.Assets, _ct));
        var captured = Assert.Single(await ProductionInputs.CaptureAsync(f.Project.Id, next, f.Assets, _ct, shots: f.Shots));
        Assert.Equal(next.ShotFrames[0].Id, captured.Identity.BindingId);

        var generator = new ComfyH3Video(null!, null!, f.Assets, f.Assets, f.Shots, new ProductionMediaTools());
        var run = new VideoRun { Snapshot = Snapshot(f.Project.Id, next) };
        var directory = Path.Combine(_root, "continuity");
        await generator.PrepareAsync(run, directory, _ct);
        var input = Assert.Single(run.Inputs);
        Assert.Equal(VideoInputKind.Image, input.EffectiveKind);
        Assert.Equal(captured.Identity.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "inputs", input.FileName), _ct))));
        var graph = Graph(Snapshot(f.Project.Id, next), [new("lumibelle/frame.png", false)]);
        Assert.Equal("100", Link(graph, "5", "ref_images.ref_image_0"));

        next.ShotFrames = [Continuity(Guid.NewGuid())];
        Assert.Contains("continuity picture", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => generator.ValidateInputsAsync(Snapshot(f.Project.Id, next), _ct))).Message);
    }

    [Fact]
    public void TheComposerKnowsWhichPictureIsTheContinuityFrame()
    {
        var shot = Ready(); shot.ShotFrames = [Continuity(Guid.NewGuid())];
        var frame = new byte[] { 4, 5, 6 };
        var request = new PromptCompositionRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "context", "source", shot, "Scene", [], [], [],
            [new(shot.ShotFrames[0].Id, Convert.ToHexString(SHA256.HashData(frame)))], "", "", "", new(AiBackend.OpenRouter, "vision", "Vision"));
        var messages = PromptComposer.BuildMessages(request, [frame]);
        using (var data = JsonDocument.Parse(messages[1].Text))
        {
            var continuity = data.RootElement.GetProperty("continuityPicture");
            Assert.Equal((1, "Sip · last frame"), (continuity.GetProperty("picture").GetInt32(), continuity.GetProperty("name").GetString()));
            Assert.Empty(data.RootElement.GetProperty("references").EnumerateArray());
        }
        Assert.Contains("<Picture 1> is a continuity frame", messages[0].Text);
        Assert.Contains("<Picture 1> is the continuityPicture", PromptComposer.BuildBriefMessages(request, [frame], [])[0].Text);
        Assert.Equal(1, PromptComposer.BriefEntries(request, []));

        // Requests without one keep their exact task data, so cached briefs still match.
        var plain = request with { Shot = Ready(), Images = [] };
        Assert.DoesNotContain("continuityPicture", PromptComposer.BuildMessages(plain, [])[1].Text);
        Assert.DoesNotContain("continuity", PromptComposer.BuildBriefMessages(plain, [], [])[0].Text);
    }

    [Fact]
    public void SeveralShotFramesAreNumberedInOrderAndOneFrameSavesInTheEarlierFormat()
    {
        var shot = Ready() with { Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Face" }] };
        var earlier = Continuity(Guid.NewGuid(), 3) with { Name = "Shot 1 · Take 2 · Frame 4" }; var previous = Continuity(Guid.NewGuid(), 48);
        shot.ShotFrames = [earlier];
        var single = JsonSerializer.Serialize(shot, AtomicJsonFile.Options);
        Assert.Contains("\"continuityFrame\":", single); Assert.DoesNotContain("shotFrames", single);
        Assert.Equal([earlier], JsonSerializer.Deserialize<Shot>(single, AtomicJsonFile.Options)!.ShotFrames);
        Assert.Same(earlier, shot.ShotFrameFingerprint);

        shot.ShotFrames = [earlier, previous];
        var pictures = ResolvedReferences.For(shot).Pictures;
        Assert.Equal([(2, earlier.Id), (3, previous.Id)], pictures.Where(p => p.Continuity is not null).Select(p => (p.Number, p.BindingId)));
        var several = JsonSerializer.Serialize(shot, AtomicJsonFile.Options);
        Assert.Contains("\"shotFrames\":", several); Assert.DoesNotContain("continuityFrame", several);
        Assert.Equal([earlier, previous], JsonSerializer.Deserialize<Shot>(several, AtomicJsonFile.Options)!.ShotFrames);
        Assert.Equal([earlier, previous], ShotCopy.Of(ShotProductionContent.From(new ProductionComposition { ShotId = shot.Id, Shot = shot })).ShotFrames);
        var restored = new ProductionComposition { ShotId = shot.Id, Shot = Ready() };
        ShotProductionContent.From(new ProductionComposition { ShotId = shot.Id, Shot = shot.Copy() }).Apply(restored);
        Assert.Equal([earlier, previous], restored.Shot.ShotFrames);
        var bare = Ready(); bare.ShotFrames = [earlier]; var one = PromptReferenceFreshness.Fingerprint(bare, []);
        bare.ShotFrames = [earlier, previous];
        Assert.NotEqual(one, PromptReferenceFreshness.Fingerprint(bare, []));
    }

    [Fact]
    public void TheComposerDescribesSeveralShotFramesTogether()
    {
        var shot = Ready(); shot.ShotFrames = [Continuity(Guid.NewGuid()) with { Name = "Shot 1 · Last frame" }, Continuity(Guid.NewGuid()) with { Name = "Shot 2 · Last frame" }];
        byte[] first = [1], second = [2];
        var request = new PromptCompositionRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "context", "source", shot, "Scene", [], [], [],
            [new(shot.ShotFrames[0].Id, Convert.ToHexString(SHA256.HashData(first))), new(shot.ShotFrames[1].Id, Convert.ToHexString(SHA256.HashData(second)))],
            "", "", "", new(AiBackend.OpenRouter, "vision", "Vision"));
        var messages = PromptComposer.BuildMessages(request, [first, second]);
        using (var data = JsonDocument.Parse(messages[1].Text))
        {
            Assert.False(data.RootElement.TryGetProperty("continuityPicture", out _));
            Assert.Equal([(1, "Shot 1 · Last frame"), (2, "Shot 2 · Last frame")], data.RootElement.GetProperty("shotFrames").EnumerateArray()
                .Select(f => (f.GetProperty("picture").GetInt32(), f.GetProperty("name").GetString())));
        }
        Assert.Contains("<Picture 1> and <Picture 2> are shot frames", messages[0].Text);
        Assert.Contains("<Picture 1> and <Picture 2> are shotFrames", PromptComposer.BuildBriefMessages(request, [first, second], [])[0].Text);
        Assert.Equal(2, PromptComposer.BriefEntries(request, []));
    }

    [Fact]
    public async Task SeveralShotFramesAreCapturedAndPreparedInOrder()
    {
        var f = Fixture(); var earlier = Ready(); var previous = Ready(); var next = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [earlier, previous, next], 0, ct: _ct);
        var a = (await AddTake(f.Project.Id, f.Shots, earlier)).Takes.Single(t => t.ShotId == earlier.Id);
        var b = (await AddTake(f.Project.Id, f.Shots, previous)).Takes.Single(t => t.ShotId == previous.Id);
        next.ShotFrames = [Continuity(a.Id, 0), Continuity(b.Id, b.FrameCount - 1)];
        var captured = await ProductionInputs.CaptureAsync(f.Project.Id, next, f.Assets, _ct, shots: f.Shots);
        Assert.Equal(next.ShotFrames.Select(s => s.Id), captured.Select(c => c.Identity.BindingId));
        var generator = new ComfyH3Video(null!, null!, f.Assets, f.Assets, f.Shots, new ProductionMediaTools());
        var run = new VideoRun { Snapshot = Snapshot(f.Project.Id, next) };
        var directory = Path.Combine(_root, "several-frames");
        await generator.PrepareAsync(run, directory, _ct);
        Assert.Equal(2, run.Inputs.Count);
        for (var i = 0; i < 2; i++)
            Assert.Equal(captured[i].Identity.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "inputs", run.Inputs[i].FileName), _ct))));
    }

    [Fact]
    public void ContinuityPicturesChangeReferenceFingerprintsOnlyWhenSet()
    {
        var shot = Ready(); var before = PromptReferenceFreshness.Fingerprint(shot, []);
        shot.ShotFrames = [Continuity(Guid.NewGuid())];
        Assert.NotEqual(before, PromptReferenceFreshness.Fingerprint(shot, []));
        shot.ShotFrames = [];
        Assert.Equal(before, PromptReferenceFreshness.Fingerprint(shot, []));
    }
}
