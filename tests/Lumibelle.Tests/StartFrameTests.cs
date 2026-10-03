using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

// A shot can open exactly on a frame of an earlier take: H3 anchors it with MiniMaxH3AddGuide.
public sealed partial class ShotTests
{
    private static readonly PreparedVideoInput StartInput = new("lumibelle/start.png", false) { Kind = VideoInputKind.StartFrame };
    private static Shot Starting(Shot shot) { shot.StartFrame = new(Guid.NewGuid(), 106); return shot; }
    private static JsonElement Graph(VideoSnapshot snapshot, IReadOnlyList<PreparedVideoInput> inputs) =>
        JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "test", inputs), AtomicJsonFile.Options).GetProperty("prompt");
    private static string Link(JsonElement graph, string node, string input, int port = 0)
    {
        var link = graph.GetProperty(node).GetProperty("inputs").GetProperty(input);
        Assert.Equal(port, link[1].GetInt32());
        return link[0].GetString()!;
    }

    [Fact]
    public void AStartFrameIsAnchoredAsFrameZeroAfterTheReferencesAreEncoded()
    {
        var shot = Starting(Ready());
        var graph = Graph(Snapshot(Guid.NewGuid(), shot), [StartInput]);
        Assert.Equal("LoadImage", graph.GetProperty("60").GetProperty("class_type").GetString());
        Assert.Equal("lumibelle/start.png", graph.GetProperty("60").GetProperty("inputs").GetProperty("image").GetString());
        var guide = graph.GetProperty("61");
        Assert.Equal("MiniMaxH3AddGuide", guide.GetProperty("class_type").GetString());
        Assert.Equal(0, guide.GetProperty("inputs").GetProperty("frame_idx").GetInt32());
        Assert.Equal("5", Link(graph, "61", "positive")); Assert.Equal("5", Link(graph, "61", "latent", 1));
        Assert.Equal("3", Link(graph, "61", "vae")); Assert.Equal("60", Link(graph, "61", "image"));
        Assert.Equal("61", Link(graph, "6", "conditioning"));
        Assert.Equal("5", Link(graph, "10", "latent_image", 1));
        // It is not a reference: H3 sees no Picture for it.
        Assert.DoesNotContain(graph.GetProperty("5").GetProperty("inputs").EnumerateObject(), p => p.Name.StartsWith("ref_images", StringComparison.Ordinal));

        var plain = Graph(Snapshot(Guid.NewGuid(), Ready()), []);
        Assert.False(plain.TryGetProperty("61", out _));
        Assert.Equal("5", Link(plain, "6", "conditioning"));
        Assert.Throws<WorkspaceStoreException>(() => Graph(Snapshot(Guid.NewGuid(), shot), []));
    }

    [Fact]
    public void AStartFrameIsAnchoredAfterRefModReferencesWithoutJoiningTheirMedia()
    {
        var shot = Starting(Ready() with { Videos = [RefModBinding()], Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Face" }] });
        var graph = RefModGraph(shot, [new("face.png", false), StartInput]);
        Assert.Equal("5", Link(graph, "61", "latent", 2));
        Assert.Equal("61", Link(graph, "6", "conditioning"));
        using var media = JsonDocument.Parse(graph.GetProperty("refmedia").GetProperty("inputs").GetProperty("media_state").GetString()!);
        Assert.Equal(["face.png"], media.RootElement.EnumerateArray().Select(i => i.GetProperty("file").GetString()));
    }

    [Fact]
    public void TheStartFrameIsTheLastPreparedInputAndExcludesAFirstFrameReference()
    {
        var shot = Starting(Ready() with { Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Face" }] });
        Assert.Equal([(VideoInputKind.Image, (int?)null), (VideoInputKind.StartFrame, null)], ResolvedReferences.For(shot).InputOrder());
        Assert.Single(ResolvedReferences.For(shot).Pictures);
        Assert.Equal(".png", AiVideoJobPolicy.Extension(VideoInputKind.StartFrame));
        H3Policy.Validate(shot, true);
        shot.Images[0].Use = ShotImageUse.FirstFrame;
        Assert.Contains("first-frame reference", Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(shot, true)).Message);
        Assert.Throws<WorkspaceStoreException>(() => H3Policy.Validate(Ready() with { StartFrame = new(Guid.NewGuid(), -1) }));
        // Shots without one serialize as before, so their fingerprints are unchanged.
        Assert.DoesNotContain("startFrame", JsonSerializer.Serialize(Ready(), AtomicJsonFile.Options));
    }

    [Fact]
    public void StartingFromAFrameMarksPromptsAndTakesAsChangedOnlyWhenSet()
    {
        var shot = Ready();
        var before = ReferenceSetups.Hash(new
        {
            shot.Id, shot.Title, shot.ApprovedScriptId, shot.SceneId, shot.SceneTitle, shot.SourceBlockIds, shot.SourceExcerpt,
            shot.Duration, shot.Description, shot.Dialogue, Characters = ShotReferences.Characters(shot).Select(c => new { c.Id, c.Name }), shot.Atmosphere, shot.Music
        });
        Assert.Equal(before, ProductionPolicy.SourceFingerprint(shot));
        var started = Starting(shot.Copy());
        Assert.NotEqual(before, ProductionPolicy.SourceFingerprint(started));
        var copy = new Shot(); ProductionPolicy.CopyCoverage(started, copy);
        Assert.Equal(started.StartFrame, copy.StartFrame);
    }

    [Fact]
    public void AContinuationOpensOnTheFrameInTheSameSceneWithItsActionStillToWrite()
    {
        var source = Ready(); source.Title = "Regrettable Sip"; source.SceneTitle = "INT. BEDROOM - NIGHT"; source.SourceBlockIds = [Guid.NewGuid()];
        source.SourceExcerpt = "## INT. BEDROOM - NIGHT"; source.Atmosphere = "Quiet night"; source.Music = "None";
        source.Characters = [new(Guid.NewGuid(), "Riley")];
        source.Dialogue = [new() { Speaker = "Riley", Language = "en", Text = "Ugh." }];
        var take = new ShotTake { ShotId = source.Id, Snapshot = Snapshot(Guid.NewGuid(), source) };
        var shot = ProductionPolicy.Continuation(source, take, take.FrameCount - 1);
        Assert.Equal("Regrettable Sip (cont.)", shot.Title); Assert.NotEqual(source.Id, shot.Id);
        Assert.Equal((source.SceneId, source.SceneTitle, source.ApprovedScriptId, source.SourceExcerpt), (shot.SceneId, shot.SceneTitle, shot.ApprovedScriptId, shot.SourceExcerpt));
        Assert.Equal(source.SourceBlockIds, shot.SourceBlockIds); Assert.NotSame(source.SourceBlockIds, shot.SourceBlockIds);
        Assert.Equal(["Riley"], shot.Characters.Select(c => c.Name));
        Assert.Equal((source.Atmosphere, source.Music), (shot.Atmosphere, shot.Music));
        Assert.Equal(new ShotStartFrame(take.Id, take.FrameCount - 1), shot.StartFrame);
        Assert.Empty(shot.Description); Assert.Empty(shot.Dialogue); Assert.Null(shot.Duration); Assert.Null(shot.SelectedTakeId);
        Assert.Equal("Regrettable Sip (cont.)", ProductionPolicy.Continuation(shot, take, 0).Title);
        Assert.Throws<WorkspaceStoreException>(() => ProductionPolicy.Continuation(source, take, take.FrameCount));
    }

    [Fact]
    public async Task TheStartFrameIsPreparedFromItsTakeAndItsTakeCannotBeDiscardedWhileUsed()
    {
        var f = Fixture(); var source = Ready(); var next = Ready();
        var doc = await f.Shots.SaveAsync(f.Project.Id, [source, next], 0, ct: _ct);
        doc = await AddTake(f.Project.Id, f.Shots, source);
        var take = Assert.Single(doc.Takes);
        next.StartFrame = new(take.Id, take.FrameCount - 1);
        doc = await f.Shots.SaveAsync(f.Project.Id, [source, next], doc.Revision, ct: _ct);
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Shots.DiscardAsync(f.Project.Id, take.Id, ShotTrashKind.Take, doc.Revision, _ct));
        Assert.Contains("starts from a frame of this take", error.Message);

        var generator = new ComfyH3Video(null!, null!, f.Assets, f.Assets, f.Shots, new ProductionMediaTools());
        var run = new VideoRun { Snapshot = Snapshot(f.Project.Id, next) };
        var directory = Path.Combine(_root, "run");
        await generator.PrepareAsync(run, directory, _ct);
        var input = Assert.Single(run.Inputs);
        Assert.Equal(("start-frame.png", VideoInputKind.StartFrame), (input.FileName, input.EffectiveKind));
        await using var frame = (await f.Shots.OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, take.FrameCount - 1, ct: _ct))!;
        using var expected = new MemoryStream(); await frame.Content.CopyToAsync(expected, _ct);
        Assert.Equal(expected.ToArray(), await File.ReadAllBytesAsync(Path.Combine(directory, "inputs", "start-frame.png"), _ct));
        var opening = await ProductionInputs.StartFrameAsync(f.Project.Id, next, f.Shots, _ct);
        Assert.Equal(new CompositionInput(take.Id, Convert.ToHexString(SHA256.HashData(expected.ToArray()))), opening!.Value.Identity);

        // A frame of another shape would be stretched, so it is refused before anything is generated.
        var wide = new VideoRun { Snapshot = Snapshot(f.Project.Id, next) with { Width = 64 } };
        Assert.Contains("different shape", (await Assert.ThrowsAsync<WorkspaceStoreException>(() => generator.PrepareAsync(wide, Path.Combine(_root, "wide"), _ct))).Message);
        next.StartFrame = new(Guid.NewGuid(), 0);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => generator.ValidateInputsAsync(Snapshot(f.Project.Id, next), _ct));
    }

    [Fact]
    public void StartingFromAFrameNeedsTheAddGuideNode()
    {
        var root = PresetCatalog(); var settings = new H3Settings(); var shot = Starting(Ready());
        H3Configuration Check() => ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings);
        Assert.True(Check().Ready(Ready()));
        Assert.False(Check().Ready(shot)); Assert.Contains("MiniMaxH3AddGuide", Check().Issue(shot));
        root["MiniMaxH3AddGuide"] = JsonNode.Parse("""
            {"input":{"required":{"positive":["CONDITIONING"],"latent":["LATENT"],"frame_idx":["INT",{"default":0}]},"optional":{"vae":["VAE"],"image":["IMAGE"],"audio":["AUDIO"]}},"output":["CONDITIONING"]}
            """);
        Assert.Null(Check().StartFrameIssue); Assert.True(Check().Ready(shot), Check().Issue(shot));
    }

    [Fact]
    public void TheComposerSeesTheOpeningFrameLastAndContinuesFromIt()
    {
        var shot = Starting(Ready()); var frame = new byte[] { 1, 2, 3 }; var hash = Convert.ToHexString(SHA256.HashData(frame));
        var request = new PromptCompositionRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "context", "source", shot, "Scene", ["Previous shot — Sip: she grimaces."], [], [], [],
            "", "", "", new(AiBackend.OpenRouter, "vision", "Vision")) { OpeningFrame = new(shot.StartFrame!.TakeId, hash) };
        var messages = PromptComposer.BuildMessages(request, [], openingFrame: frame);
        Assert.Equal(frame, Assert.Single(messages[1].Contents.OfType<DataContent>()).Data.ToArray());
        Assert.Contains("OPENING FRAME", messages[0].Text); Assert.Contains("final attached image", messages[0].Text);
        Assert.DoesNotContain("design a clearly distinct opening image", messages[0].Text);
        using (var data = JsonDocument.Parse(messages[1].Text))
        {
            var cut = data.RootElement.GetProperty("cutContinuity");
            Assert.Equal("continuous_from_opening_frame", cut.GetProperty("transition").GetString());
            Assert.False(cut.GetProperty("mustOpenDifferentlyFromPrevious").GetBoolean());
        }
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.BuildMessages(request, []));
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.BuildMessages(request, [], openingFrame: [9]));

        // In two steps, the brief describes the frame and the composition reads it from there.
        var brief = PromptComposer.BuildBriefMessages(request, [], [], frame);
        Assert.Contains("<Opening frame>", brief[0].Text); Assert.Single(brief[1].Contents.OfType<DataContent>());
        Assert.Equal(1, PromptComposer.BriefEntries(request, []));
        var second = PromptComposer.BuildMessages(request, [], [], visualBrief: true);
        Assert.Empty(second[1].Contents.OfType<DataContent>()); Assert.Contains("under <Opening frame>", second[0].Text);
        Assert.Equal("<Opening frame> She grimaces.\nShe grimaces.", PromptComposer.ReadBrief("<Opening frame> She grimaces.\nShe grimaces.\nShe grimaces."));

        var cutIn = request with { OpeningFrame = null, Shot = Ready() };
        var plain = PromptComposer.BuildMessages(cutIn, []);
        Assert.Contains("design a clearly distinct opening image", plain[0].Text); Assert.DoesNotContain("OPENING FRAME", plain[0].Text);
        Assert.Equal(0, PromptComposer.BriefEntries(cutIn, []));
    }
}
