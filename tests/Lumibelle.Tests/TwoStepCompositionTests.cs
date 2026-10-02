using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed class TwoStepCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.TwoStep", Guid.NewGuid().ToString("N"));
    private static readonly TextModelReference Model = new(AiBackend.ComfyUI, "qwen-vl.safetensors", "Qwen", "http://127.0.0.1:8188");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static byte[] Png(byte shade)
    {
        using var image = new Image<Rgba32>(64, 32, new Rgba32(shade, shade, shade, 255));
        using var output = new MemoryStream(); image.SaveAsPng(output); return output.ToArray();
    }

    /// <summary>A shot with one keyframe picture, as composed with a vision model.</summary>
    private static (PromptCompositionRequest Request, byte[][] Images) Composition()
    {
        var (_, _, _, shot) = CharacterVoiceTests.Fixture();
        shot.ApprovedScriptId = Guid.NewGuid(); shot.SceneId = Guid.NewGuid();
        var reel = shot.Videos[0]; reel.Visuals = ReelVisuals.Keyframes;
        reel.Keyframes = new() { Frames = [new() { Frame = new(reel.Media.Id, reel.Media.Sha256, 0, 0), Notes = "Front view" }] };
        var pictures = ResolvedReferences.For(shot).Pictures;
        var request = new PromptCompositionRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "context", "source", shot, "INT. LAB - NIGHT\nRiley types.", ["Previous shot — Lab"], [], [],
            pictures.Select(p => new CompositionInput(p.BindingId, "hash")).ToArray(), "Stay close.", "", "", Model);
        return (request, pictures.Select((_, i) => Png((byte)(40 + i))).ToArray());
    }

    private static AiSettings Settings => new()
    {
        ComfyTextModels = new() { [TextModelPolicy.Key(Model)] = new(2048, .7f) { VisionInput = ComfyVisionInput.ImageBatch } }
    };

    private static AiTextJobRequest TwoStep(PromptCompositionRequest composition, byte[][] images, string? cachedBrief = null)
    {
        var brief = PromptComposer.BuildBriefMessages(composition, images, []).Select(AiTextMessage.Capture).ToArray();
        return new AiTextJobRequest(2, AiJobKind.PromptComposition, Model, false, Settings, ProductionPolicy.Profile, .7f, 7,
            JsonSerializer.SerializeToElement(composition, AtomicJsonFile.Options),
            PromptComposer.BuildMessages(composition, [], [], visualBrief: true).Select(AiTextMessage.Capture).ToArray())
        {
            BriefMessages = cachedBrief is null ? brief : null, VisualBrief = cachedBrief, BriefKey = VisualBriefCache.Key(Model, 1024, brief)
        };
    }

    private static AiJobHeader Header(AiTextJobRequest request)
    {
        var composition = request.Payload<PromptCompositionRequest>();
        return new() { Id = Guid.NewGuid(), Kind = AiJobKind.PromptComposition, Backend = AiBackend.ComfyUI,
            Target = new(composition.ProjectId, ShotId: composition.Shot.Id, CompositionId: composition.CompositionId),
            ProjectName = "Project", TargetName = "Shot", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test" };
    }

    [Fact]
    public void BriefStepSeesTheImagesAndReferencesButNotTheShotOrDirections()
    {
        var (composition, images) = Composition();
        var messages = PromptComposer.BuildBriefMessages(composition, images, []);
        Assert.Contains("visual brief", messages[0].Text);
        Assert.Equal(images.Length, messages[1].Contents.OfType<DataContent>().Count());
        Assert.Contains("Front view", messages[1].Text);
        // Shot text and directions change between revisions; leaving them out lets revisions reuse the brief.
        Assert.DoesNotContain("Stay close.", messages[1].Text);
        Assert.DoesNotContain("Riley types.", messages[1].Text);
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.BuildBriefMessages(composition, [], []));
    }

    [Fact]
    public void CompositionStepIsTextOnlyAndTreatsTheBriefAsItsVisualEvidence()
    {
        var (composition, images) = Composition();
        var withImages = PromptComposer.BuildMessages(composition, images);
        var briefMode = PromptComposer.BuildMessages(composition, [], [], visualBrief: true);
        Assert.Empty(briefMode.SelectMany(m => m.Contents).OfType<DataContent>());
        Assert.Contains("Inspect these images directly", withImages[0].Text);
        Assert.DoesNotContain("Inspect these images directly", briefMode[0].Text);
        Assert.Contains("REFERENCE INPUT MODE: VISUAL BRIEF", briefMode[0].Text);
        Assert.Contains("not missing input", briefMode[0].Text);
        Assert.Contains("Stay close.", briefMode[1].Text);
        Assert.Throws<WorkspaceStoreException>(() => PromptComposer.BuildMessages(composition, images, [], visualBrief: true));

        var final = PromptComposer.WithBrief(briefMode.Select(AiTextMessage.Capture).ToArray(), "  <Picture 1> Riley: short dark hair.  ");
        var brief = final[^1].Parts[^2].Text!;
        Assert.EndsWith("<Picture 1> Riley: short dark hair.", brief);
        Assert.StartsWith("Visual brief", brief);
        // The request for the JSON answer comes last, after the brief.
        Assert.StartsWith("Now return only the JSON object", final[^1].Parts[^1].Text);
        Assert.Null(PromptComposer.ReadBrief("   "));
        Assert.Null(PromptComposer.ReadBrief(new string('x', PromptComposer.MaximumBriefCharacters + 1)));
    }

    [Fact]
    public void RevisionsNameTheValidReferenceLabels()
    {
        var (composition, images) = Composition();
        Assert.DoesNotContain("selected references are", PromptComposer.BuildMessages(composition, images)[0].Text);
        // A revision's current prompt may still cite a reference that was removed from the shot.
        var revision = PromptComposer.BuildMessages(composition with { CurrentPrompt = "Uses <Picture 3> for the face." }, [], [], visualBrief: true)[0].Text;
        Assert.Contains("selected references are <Picture 1>", revision);
        Assert.DoesNotContain("<Picture 3>", revision[revision.IndexOf("selected references are", StringComparison.Ordinal)..]);
        Assert.Contains("never carry over", revision);
    }

    [Fact]
    public void SavedTwoStepRequestsAreValidatedWhenRead()
    {
        var (composition, images) = Composition();
        var request = TwoStep(composition, images);
        var read = AiTextJobHandler.Read(Header(request), JsonSerializer.SerializeToElement(request, AtomicJsonFile.Options));
        Assert.True(read.TwoStep); Assert.True(read.InspectsImages);
        var cached = TwoStep(composition, images, "<Picture 1> Riley.");
        Assert.False(AiTextJobHandler.Read(Header(cached), JsonSerializer.SerializeToElement(cached, AtomicJsonFile.Options)).InspectsImages);

        void Rejects(AiTextJobRequest invalid) => Assert.Throws<WorkspaceStoreException>(() =>
            AiTextJobHandler.Read(Header(invalid), JsonSerializer.SerializeToElement(invalid, AtomicJsonFile.Options)));
        Rejects(request with { VisualBrief = "Both a brief and its inspection." });
        Rejects(request with { BriefKey = "not-a-key" });
        Rejects(request with { BriefMessages = null });
        Rejects(request with { Messages = PromptComposer.BuildMessages(composition, images).Select(AiTextMessage.Capture).ToArray() });
        Rejects(request with { Model = new(AiBackend.OpenRouter, "vision", "Vision") });
        Rejects(request with { BriefKey = null });
        Rejects(request with { BriefTokens = 100_000 });
        Assert.True(AiTextJobHandler.Read(Header(request with { BriefTokens = 1936 }), JsonSerializer.SerializeToElement(request with { BriefTokens = 1936 }, AtomicJsonFile.Options)).TwoStep);
    }

    [Fact]
    public void TheBriefsBudgetGrowsWithItsReferences()
    {
        // Up to six references keep the original budget; fourteen get room for every one, within a cap.
        Assert.Equal(PromptComposer.BriefTokens, PromptComposer.BriefTokensFor(1));
        Assert.Equal(PromptComposer.BriefTokens, PromptComposer.BriefTokensFor(6));
        Assert.Equal(256 + 14 * 120, PromptComposer.BriefTokensFor(14));
        Assert.Equal(PromptComposer.MaximumBriefTokens, PromptComposer.BriefTokensFor(40));
        var (composition, images) = Composition();
        Assert.Contains("Stay under 600 words", PromptComposer.BuildBriefMessages(composition, images, [])[0].Text);
        // Older snapshots without a captured limit keep the fixed one, and the estimate counts the captured one.
        var request = TwoStep(composition, images);
        Assert.Equal(PromptComposer.BriefTokens, PromptComposer.BriefTokensOf(request));
        Assert.Equal(1936, ComfyTextCapacity.Assess(request with { BriefTokens = 1936 })![0].Size.ReplyTokens);
    }

    [Fact]
    public async Task BriefsAreCachedByModelImagesAndCanvasSize()
    {
        var (composition, images) = Composition();
        var messages = PromptComposer.BuildBriefMessages(composition, images, []).Select(AiTextMessage.Capture).ToArray();
        var key = VisualBriefCache.Key(Model, 1024, messages);
        var cache = new VisualBriefCache(new ApplicationPaths(_root));
        Assert.Null(await cache.ReadAsync(key, Ct));
        await cache.WriteAsync(key, "<Picture 1> Riley.", Ct);
        Assert.Equal("<Picture 1> Riley.", await cache.ReadAsync(key, Ct));

        Assert.Equal(key, VisualBriefCache.Key(Model, 1024, PromptComposer.BuildBriefMessages(composition with { DirectingNotes = "Wider." }, images, [])
            .Select(AiTextMessage.Capture).ToArray()));
        Assert.NotEqual(key, VisualBriefCache.Key(Model, 512, messages));
        Assert.NotEqual(key, VisualBriefCache.Key(Model with { Model = "other.safetensors" }, 1024, messages));
        var changed = images.Select((image, i) => i == 0 ? Png(200) : image).ToArray();
        Assert.NotEqual(key, VisualBriefCache.Key(Model, 1024, PromptComposer.BuildBriefMessages(composition, changed, []).Select(AiTextMessage.Capture).ToArray()));
        // A malformed key never reaches the file system; it simply has no brief.
        Assert.Null(await cache.ReadAsync("../escape", Ct));
    }

    [Fact]
    public void SizeEstimateCoversBothSteps()
    {
        var (composition, images) = Composition();
        var stages = ComfyTextCapacity.Assess(TwoStep(composition, images))!;
        Assert.Equal(["Step 1 (visual brief)", "Step 2 (composition)"], stages.Select(s => s.Step));
        Assert.Equal(images.Length, stages[0].Size.Images);
        Assert.Equal(0, stages[1].Size.Images);
        // A brief not yet written counts at its full limit.
        Assert.True(stages[1].Size.ContentTokens >= PromptComposer.BriefTokens);
        var cached = ComfyTextCapacity.Assess(TwoStep(composition, images, "<Picture 1> Riley."))!;
        Assert.Equal("Step 2 (composition)", Assert.Single(cached).Step);
    }
}
