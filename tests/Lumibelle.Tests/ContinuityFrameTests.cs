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
        shot.ContinuityFrame = Continuity(Guid.NewGuid());
        var pictures = ResolvedReferences.For(shot).Pictures;
        Assert.Equal(3, pictures.Count);
        Assert.Equal((2, shot.ContinuityFrame.Id), (pictures[1].Number, pictures[1].BindingId));
        Assert.Same(shot.ContinuityFrame, pictures[1].Continuity); Assert.NotNull(pictures[2].Keyframe);
        Assert.All(ResolvedReferences.For(shot).InputOrder(), i => Assert.Equal(VideoInputKind.Image, i.Kind));
        Assert.DoesNotContain("continuityFrame", JsonSerializer.Serialize(Ready(), AtomicJsonFile.Options));
        // It is a reference of the shot's setups, saved with the other references.
        var composition = new ProductionComposition { ShotId = shot.Id, Shot = shot.Copy() };
        var restored = new ProductionComposition { ShotId = shot.Id, Shot = Ready() };
        ShotProductionContent.From(composition).Apply(restored);
        Assert.Equal(shot.ContinuityFrame, restored.Shot.ContinuityFrame);
        // Copying another shot's references never carries over the picture of the shot before that one.
        Assert.Null(ReferenceCopies.Into(shot, Ready(), new() { ProjectId = Guid.NewGuid() }).Inputs.ContinuityFrame);
    }

    [Fact]
    public async Task AContinuityPictureIsCapturedAndPreparedAsTheSameImage()
    {
        var f = Fixture(); var source = Ready(); var next = Ready();
        var doc = await f.Shots.SaveAsync(f.Project.Id, [source, next], 0, ct: _ct);
        var take = Assert.Single((await AddTake(f.Project.Id, f.Shots, source)).Takes);
        next.ContinuityFrame = Continuity(take.Id, take.FrameCount - 1);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => ProductionInputs.CaptureAsync(f.Project.Id, next, f.Assets, _ct));
        var captured = Assert.Single(await ProductionInputs.CaptureAsync(f.Project.Id, next, f.Assets, _ct, shots: f.Shots));
        Assert.Equal(next.ContinuityFrame.Id, captured.Identity.BindingId);

        var generator = new ComfyH3Video(null!, null!, f.Assets, f.Assets, f.Shots, new ProductionMediaTools());
        var run = new VideoRun { Snapshot = Snapshot(f.Project.Id, next) };
        var directory = Path.Combine(_root, "continuity");
        await generator.PrepareAsync(run, directory, _ct);
        var input = Assert.Single(run.Inputs);
        Assert.Equal(VideoInputKind.Image, input.EffectiveKind);
        Assert.Equal(captured.Identity.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "inputs", input.FileName), _ct))));
        var graph = Graph(Snapshot(f.Project.Id, next), [new("lumibelle/frame.png", false)]);
        Assert.Equal("100", Link(graph, "5", "ref_images.ref_image_0"));

        next.ContinuityFrame = Continuity(Guid.NewGuid());
        Assert.Contains("continuity picture", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => generator.ValidateInputsAsync(Snapshot(f.Project.Id, next), _ct))).Message);
    }

    [Fact]
    public void TheComposerKnowsWhichPictureIsTheContinuityFrame()
    {
        var shot = Ready(); shot.ContinuityFrame = Continuity(Guid.NewGuid());
        var frame = new byte[] { 4, 5, 6 };
        var request = new PromptCompositionRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "context", "source", shot, "Scene", [], [], [],
            [new(shot.ContinuityFrame.Id, Convert.ToHexString(SHA256.HashData(frame)))], "", "", "", new(AiBackend.OpenRouter, "vision", "Vision"));
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
    public void ContinuityPicturesChangeReferenceFingerprintsOnlyWhenSet()
    {
        var shot = Ready(); var before = PromptReferenceFreshness.Fingerprint(shot, []);
        shot.ContinuityFrame = Continuity(Guid.NewGuid());
        Assert.NotEqual(before, PromptReferenceFreshness.Fingerprint(shot, []));
        shot.ContinuityFrame = null;
        Assert.Equal(before, PromptReferenceFreshness.Fingerprint(shot, []));
    }
}
