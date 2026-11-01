using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace Lumibelle.Tests;

public sealed partial class RegionalImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static async Task<byte[]> Png(int w, int h, Rgba32 color, bool orient = false)
    {
        using var image = new Image<Rgba32>(w, h, color);
        if (orient) { image.Metadata.ExifProfile = new(); image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6); }
        using var stream = new MemoryStream(); await image.SaveAsPngAsync(stream, Ct); return stream.ToArray();
    }
    internal static async Task<RegionalImageSelection> Selection(byte[] bytes, AssetImageReference? reference = null, RegionalEditMode mode = RegionalEditMode.Protect)
    {
        using var stream = new MemoryStream(bytes); var original = await RegionalImageEdits.OriginalAsync(stream, Ct); var info = ImageInspector.Inspect(original);
        return new() { Source = reference ?? new(Guid.NewGuid(), Guid.NewGuid()), SourceHash = RegionalImageEdits.Hash(original), Width = info.Width, Height = info.Height, Mode = mode };
    }
    internal static MaskStroke Rect(bool protect, double left, double top, double right, double bottom, bool erase = false) => new(protect, erase, true, .1, [new(left, top), new(right, bottom)]);
    [Theory]
    [InlineData(1024, 896, 1184)]
    [InlineData(2048, 1760, 2368)]
    public async Task SourceAspectUsesTheSelectedContextAndKeepsRestorationGeometry(int resolution, int width, int height)
    {
        var bytes = await Png(192, 128, new(31, 67, 121, 255));
        var selection = (await Selection(bytes)) with { Context = new() { Width = .5, Height = 1 } };
        using var source = new MemoryStream(bytes);
        var prepared = await RegionalImageEdits.PrepareAsync(source, selection, ImageAspectPolicy.FromImage1, Ct, resolution);
        var info = ImageInspector.Inspect(prepared.Png);
        Assert.Equal((width, height), (info.Width, info.Height));
        var result = await RegionalImageEdits.CombineAsync(prepared.Capture, prepared.Png, 0, Ct);
        Assert.Equal((192, 128), (ImageInspector.Inspect(result).Width, ImageInspector.Inspect(result).Height));
    }
    [Theory]
    [InlineData(RegionalEditMode.Protect, 0)] [InlineData(RegionalEditMode.Protect, 8)]
    [InlineData(RegionalEditMode.Edit, 0)] [InlineData(RegionalEditMode.Edit, 32)]
    public async Task MasksPreserveEveryUntouchedPixelAndProtectionWins(RegionalEditMode mode, int blend)
    {
        var bytes = await Png(80, 60, new(31, 67, 121, 84));
        var selection = await Selection(bytes, mode: mode);
        selection = selection with { Context = new() { X = .1, Y = .1, Width = .8, Height = .8 },
            Strokes = [Rect(false, .2, .2, .8, .8), Rect(true, .3, .3, .5, .5), Rect(true, .4, .4, .6, .6), Rect(true, .45, .45, .5, .5, true)] };
        using var source = new MemoryStream(bytes);
        var prepared = await RegionalImageEdits.PrepareAsync(source, selection, "1:1", Ct);
        var output = await Png(512, 512, new(240, 100, 10, 255));
        var combined = await RegionalImageEdits.CombineAsync(prepared.Capture, output, blend, Ct);
        using var original = Image.Load<Rgba32>(prepared.Capture.OriginalPng); using var result = Image.Load<Rgba32>(combined);
        var masks = RegionalImageEdits.Masks(selection); var changed = 0;
        for (var y = 0; y < 60; y++) for (var x = 0; x < 80; x++)
        {
            if (!masks.Edit[y * 80 + x]) Assert.Equal(original[x, y], result[x, y]);
            else { Assert.NotEqual(original[x, y], result[x, y]); changed++; }
        }
        Assert.True(changed > 0); Assert.Equal(new Rgba32(31, 67, 121, 84), result[28, 22]);
        Assert.Equal((80, 60), (result.Width, result.Height));
    }
    [Fact]
    public async Task ProtectionIsOpaqueBeforePaddingAndResizingAndAssistUsesIdenticalBytes()
    {
        var bytes = await Png(40, 80, new(220, 20, 0, 60));
        var selection = (await Selection(bytes)) with { Strokes = [Rect(true, 0, 0, 1, .5)] };
        using var source = new MemoryStream(bytes);
        var prepared = await RegionalImageEdits.PrepareAsync(source, selection, "1:1", Ct);
        using var input = Image.Load<Rgba32>(prepared.Png);
        Assert.Equal(RegionalImageEdits.Cover, input[512, 200]);
        Assert.Equal(RegionalImageEdits.Cover, input[10, 700]);
        Assert.NotEqual(RegionalImageEdits.Cover, input[512, 800]);
        Assert.Equal(new RegionalCanvas(256, 0, 512, 1024, 1024, 1024), prepared.Capture.Canvas);
        using var assistSource = new MemoryStream(bytes);
        Assert.Equal(prepared.Png, await RegionalImageEdits.InputAsync(assistSource, selection, null, "1:1", Ct));
    }
    [Fact]
    public async Task OrientationAndExactFixedPlacementRespectContextAndPadding()
    {
        var bytes = await Png(20, 40, new(4, 5, 6), orient: true);
        var selection = (await Selection(bytes)) with { Context = new() { X = .25, Y = 0, Width = .5, Height = 1 }, Strokes = [Rect(true, .25, 0, .5, 1)] };
        Assert.Equal((40, 20), (selection.Width, selection.Height));
        using var source = new MemoryStream(bytes); var prepared = await RegionalImageEdits.PrepareAsync(source, selection, "16:9", Ct);
        using var output = Image.Load<Rgba32>(prepared.Png);
        output.Mutate(c => c.BackgroundColor(Color.Green));
        for (var y = 0; y < output.Height; y++) for (var x = 0; x < output.Width; x++) output[x, y] = new(10, 20, 30);
        using var stream = new MemoryStream(); await output.SaveAsPngAsync(stream, Ct);
        using var result = Image.Load<Rgba32>(await RegionalImageEdits.CombineAsync(prepared.Capture, stream.ToArray(), ct: Ct));
        Assert.Equal(new Rgba32(4, 5, 6), result[5, 5]); Assert.Equal(new Rgba32(4, 5, 6), result[15, 5]);
        Assert.Equal(new Rgba32(10, 20, 30), result[25, 5]); Assert.Equal(new Rgba32(4, 5, 6), result[35, 5]);
    }
    [Fact]
    public async Task InvalidIdentityCanvasAndEmptyEditsFailWithoutChangingSource()
    {
        var bytes = await Png(50, 50, new(23, 45, 67)); var selection = await Selection(bytes);
        using var source = new MemoryStream(bytes); var prepared = await RegionalImageEdits.PrepareAsync(source, selection, "1:1", Ct);
        await Assert.ThrowsAsync<AiGenerationException>(() => RegionalImageEdits.CombineAsync(prepared.Capture, PngBlocking(80, 40), ct: Ct));
        using var changed = new MemoryStream(await Png(50, 50, new(99, 99, 99)));
        await Assert.ThrowsAsync<AiGenerationException>(() => RegionalImageEdits.PrepareAsync(changed, selection, "1:1", Ct));
        await Assert.ThrowsAsync<AiGenerationException>(() => RegionalImageEdits.CombineAsync(prepared.Capture with { Selection = selection with { Mode = RegionalEditMode.Edit } }, prepared.Png, ct: Ct));
        Assert.Throws<AiGenerationException>(() => RegionalImageEdits.Validate(selection with { Strokes = [new(true, false, false, double.NaN, [new(0, 0)])] }));
        Assert.Equal(bytes, source.ToArray());
    }
    private static byte[] PngBlocking(int w, int h) { using var image = new Image<Rgba32>(w, h); using var stream = new MemoryStream(); image.SaveAsPng(stream); return stream.ToArray(); }
    [Fact]
    public async Task CropShortcutFitsAuthoredSelectionAndKeepsProtectionInsideThePreparedCrop()
    {
        var bytes = await Png(80, 100, new(220, 60, 40));
        var selection = (await Selection(bytes, mode: RegionalEditMode.Edit)) with {
            Context = new() { X = 0, Y = 0, Width = .2, Height = .2 },
            Strokes = [Rect(false, .1, .2, .8, .6), Rect(false, .75, .2, .8, .6, true), Rect(true, .1, .2, .4, .6)] };
        var crop = RegionalImageEdits.CropToSelection(selection);
        Assert.Equal(new Rectangle(8, 20, 52, 40), ImageGeometry.CropPixels(80, 100, crop));
        using var stream = new MemoryStream(bytes);
        var prepared = await RegionalImageEdits.PrepareAsync(stream, selection with { Context = crop }, "1:1", Ct);
        Assert.Equal(crop, prepared.Capture.Selection.Context);
        using var input = Image.Load<Rgba32>(prepared.Png);
        Assert.Equal(RegionalImageEdits.Cover, input[100, 512]);
        Assert.Equal(new Rgba32(220, 60, 40), input[900, 512]);
        Assert.Throws<AiGenerationException>(() => RegionalImageEdits.CropToSelection(selection with { Strokes = [] }));
        Assert.Throws<AiGenerationException>(() => RegionalImageEdits.CropToSelection(selection with { Strokes = [Rect(false, 0, 0, 1, 1), Rect(false, 0, 0, 1, 1, true)] }));
    }
    [Fact]
    public async Task BrushErasingAndModeChangesKeepProtectionAndEditLayersSeparate()
    {
        var selection = (await Selection(await Png(100, 100, new(20, 30, 40)), mode: RegionalEditMode.Edit)) with {
            Strokes = [Rect(false, .1, .1, .9, .9), new(true, false, false, .2, [new(.25, .5), new(.75, .5)]),
                new(true, true, false, .1, [new(.5, .5)]), Rect(false, .1, .1, .2, .2, true)] };
        var masks = RegionalImageEdits.Masks(selection);
        Assert.False(masks.Edit[50 * 100 + 25]); Assert.True(masks.Protect[50 * 100 + 25]);
        Assert.True(masks.Edit[50 * 100 + 50]); Assert.False(masks.Protect[50 * 100 + 50]);
        Assert.True(masks.Edit[65 * 100 + 50]); Assert.False(masks.Edit[5 * 100 + 50]);
        Assert.False(masks.Edit[15 * 100 + 15]);
        Assert.True(RegionalImageEdits.Masks(selection with { Mode = RegionalEditMode.Protect }).Edit[15 * 100 + 15]);
    }
    [Fact]
    public async Task InverseMappingRemovesScaledPaddingBeforePasting()
    {
        var bytes = await Png(80, 40, new(30, 60, 90));
        var selection = (await Selection(bytes)) with { Strokes = [Rect(true, 0, 0, .25, 1)] };
        using var source = new MemoryStream(bytes); var prepared = await RegionalImageEdits.PrepareAsync(source, selection, "1:1", Ct);
        using var output = new Image<Rgba32>(512, 512, new(255, 0, 255));
        for (var y = 128; y < 384; y++) for (var x = 0; x < 512; x++) output[x, y] = new(0, 200, 0);
        using var stream = new MemoryStream(); await output.SaveAsPngAsync(stream, Ct);
        using var result = Image.Load<Rgba32>(await RegionalImageEdits.CombineAsync(prepared.Capture, stream.ToArray(), ct: Ct));
        for (var y = 0; y < 40; y++) for (var x = 0; x < 80; x++) Assert.Equal(x < 20 ? new Rgba32(30, 60, 90) : new Rgba32(0, 200, 0), result[x, y]);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RegionalImageEdits.CombineAsync(prepared.Capture, stream.ToArray(), ct: cancelled.Token));
    }
}

public sealed partial class AssetStoreTests
{
    [Theory]
    [InlineData(ImageWorkflow.Krea2)] [InlineData(ImageWorkflow.Flux2Klein9bKv)]
    [InlineData(ImageWorkflow.QwenImage21)]
    public async Task AdditionalProtectionUsesPreparedAssistInputsAndReviewWithoutPastingReferencePixels(ImageWorkflow workflow)
    {
        using var f = await ImageJobFixture.Create(this, workflow, true); var ct = TestContext.Current.CancellationToken;
        await using var media = await f.Assets.OpenImageAsync(f.Project.Id, f.AssetId, f.References[1].ImageId, ct);
        using var original = new MemoryStream(); await media!.Content.CopyToAsync(original, ct);
        var region = (await RegionalImageTests.Selection(original.ToArray(), f.References[1])) with { Strokes = [RegionalImageTests.Rect(true, 0, 0, 1, .5)] };
        var submission = await f.Capture(regions: [region]); var request = submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(f.References[0], request.Regional!.Selection.Source); Assert.Equal(2, request.Edit!.Regions!.Count);
        Assert.Equal(.25, request.Regional.Selection.Context.X);
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var assist = new AiTextJobCapture(f.Settings, projects, f.Assets, new PromptEnhancer(null!, f.Settings, f.Assets), null!);
        var context = new PromptEnhancementContext { ProjectId = f.Project.Id, AssetId = f.AssetId, Prompt = "Change the background", Workflow = workflow,
            IsEdit = true, MaximumReferences = 8, AspectRatio = "1:1", TargetLook = request.Look,
            References = request.Inputs.Select((i, index) => new PromptReference(i.Reference, index == 0 ? "Base" : "Reference", "", i.Crop) { Look = i.Context, Region = index == 1 ? region : null }).ToArray() };
        var text = (await assist.EnhanceAsync(Guid.NewGuid(), Guid.NewGuid(), new(context, new(AiBackend.OpenRouter, "test-model", "Test model"), true), ct)).Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var images = text.Messages.SelectMany(m => m.Parts).Where(p => p.Image is not null).ToArray();
        Assert.Equal(2, images.Length);
        for (var i = 0; i < images.Length; i++) Assert.Equal(request.Inputs[i].Png, images[i].Image);
        var job = await f.Claim(submission); await f.Worker.ExecuteAsync(job, submission.Snapshot, ct);
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, ct)).ImagePublications);
        var review = new RegionalImageReview(f.Jobs, f.Assets); var candidate = Assert.Single((await review.LoadAsync(f.Project.Id, submission.Id, ct)).Candidates);
        var saved = await review.SaveAsync(f.Project.Id, submission.Id, candidate.Image.CandidateId, 0, ct);
        var image = saved.Assets[0].Images.Single(i => i.Id == candidate.Image.CandidateId);
        Assert.Equal((80, 40), (image.Width, image.Height)); Assert.Equal(f.References[0].ImageId, image.Generation!.Edit!.SourceImageId);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RegionalCodexAndAssistSeePreparedPixelsAndKeepNativeOutput(bool incompatible)
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.CodexImages, true); var ct = TestContext.Current.CancellationToken;
        var mock = new Lumibelle.Testing.MockCodexTransport { ImageWidth = 80, ImageHeight = incompatible ? 40 : 80 };
        await using var client = new CodexClient(mock, TimeProvider.System);
        f.Settings.Value = f.Settings.Value with { Codex = new() { Enabled = true, ImageModel = "mock-codex" } };
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var capture = new AiImageJobCapture(f.Settings, projects, f.Assets, client);
        await using var media = await f.Assets.OpenImageAsync(f.Project.Id, f.AssetId, f.References[0].ImageId, ct);
        using var stream = new MemoryStream(); await media!.Content.CopyToAsync(stream, ct);
        var region = (await RegionalImageTests.Selection(stream.ToArray(), f.References[0])) with { Strokes = [RegionalImageTests.Rect(true, 0, 0, .5, 1)] };
        var submission = await capture.EditAsync(Guid.NewGuid(), Guid.NewGuid(), f.AssetId, new() { ProjectId = f.Project.Id, Workflow = ImageWorkflow.CodexImages,
            SourceAssetId = f.AssetId, SourceImageId = f.References[0].ImageId, Prompt = "Change the background", AspectRatio = "1:1", Regions = [region] }, [f.References[0]], ct);
        var request = submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        var assist = new AiTextJobCapture(f.Settings, projects, f.Assets, new PromptEnhancer(null!, f.Settings, f.Assets), null!);
        var textRequest = await assist.EnhanceAsync(Guid.NewGuid(), Guid.NewGuid(), new(new() { ProjectId = f.Project.Id, AssetId = f.AssetId, Prompt = "Change the background",
            Workflow = ImageWorkflow.CodexImages, IsEdit = true, MaximumReferences = 8, AspectRatio = "1:1", TargetLook = request.Look,
            References = [new(f.References[0], "Base", "", null) { Region = region, Look = request.Inputs[0].Context }] }, new(AiBackend.OpenRouter, "test-model", "Test model"), true), ct);
        var text = textRequest.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(request.Inputs[0].Png, Assert.Single(text.Messages.SelectMany(m => m.Parts), p => p.Image is not null).Image);
        await f.Jobs.EnqueueAsync(submission, ct); var job = (await f.Jobs.ClaimNextAsync(AiBackend.Codex, 1, ct))!;
        var context = f.Context(job, false); var worker = new AiImageJobHandler(f.Assets, null!, null!, null!, TimeProvider.System, client);
        Assert.Equal(1, (await worker.ExecuteAsync(context, submission.Snapshot, ct)).CompletedCandidates);
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, ct)).ImagePublications);
        var review = new RegionalImageReview(f.Jobs, f.Assets); var batch = await review.LoadAsync(f.Project.Id, submission.Id, ct); var candidate = Assert.Single(batch.Candidates);
        if (incompatible)
        {
            await Assert.ThrowsAsync<AiGenerationException>(() => review.SaveAsync(f.Project.Id, submission.Id, candidate.Image.CandidateId, 0, ct));
            Assert.Equal(40, ImageInspector.Inspect((await review.LoadAsync(f.Project.Id, submission.Id, ct)).Candidates[0].Image.Bytes).Height);
        }
        else Assert.Single((await review.SaveAsync(f.Project.Id, submission.Id, candidate.Image.CandidateId, 0, ct)).ImagePublications);
        await worker.RecoverAsync(await f.Recover(context), submission.Snapshot, ct); Assert.Equal(1, mock.Turns);
    }

    [Theory]
    [InlineData(ImageWorkflow.Krea2)] [InlineData(ImageWorkflow.Flux2Klein9bKv)]
    [InlineData(ImageWorkflow.QwenImage21)]
    public async Task RegionalQueuedEditsStageBeforeReviewAndRecoverWithoutGenerating(ImageWorkflow workflow)
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = await ImageJobFixture.Create(this, workflow, true);
        await using var media = await f.Assets.OpenImageAsync(f.Project.Id, f.AssetId, f.References[0].ImageId, ct);
        using var source = new MemoryStream(); await media!.Content.CopyToAsync(source, ct);
        var selection = (await RegionalImageTests.Selection(source.ToArray(), f.References[0])) with { Strokes = [RegionalImageTests.Rect(true, 0, 0, .5, 1)] };
        var submission = await f.Capture(2, regions: [selection]);
        var request = submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        Assert.NotNull(request.Regional);
        var context = await f.Claim(submission);
        var outcome = await f.Worker.ExecuteAsync(context, submission.Snapshot, ct);
        Assert.Equal(2, outcome.CompletedCandidates);
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, ct)).ImagePublications);
        Assert.All(f.Graphs, graph => Assert.Contains(Convert.ToBase64String(request.Inputs[0].Png), graph.GetRawText()));
        var codexParts = AiImageJobHandler.CodexMessages(request).SelectMany(m => m.Parts).Where(p => p.Image is not null).ToArray();
        Assert.Equal(request.Inputs[0].Png, codexParts[0].Image);
        Assert.DoesNotContain(codexParts, p => p.Image!.SequenceEqual(request.Regional!.OriginalPng));
        f.NoNetwork = true;
        await f.Worker.RecoverAsync(await f.Recover(context), submission.Snapshot, ct);
        // Recovery reconciles the one grouped prompt without submitting again.
        Assert.Equal(1, f.Posts);
        var review = new RegionalImageReview(f.Jobs, f.Assets);
        var batch = await review.LoadAsync(f.Project.Id, submission.Id, ct);
        Assert.Equal(2, batch.Candidates.Count);
        var first = batch.Candidates[0].Image.CandidateId;
        var colour = new RegionalColourSettings { Brightness = 3, Warmth = 7, Tint = -4 };
        await review.SaveDraftAsync(f.Project.Id, submission.Id, first, 8, colour, ct);
        await review.SaveDraftAsync(f.Project.Id, submission.Id, batch.Candidates[1].Image.CandidateId, 0, new() { Brightness = -10 }, ct);
        var drafts = await new RegionalImageReview(new FileAiJobStore(f.Jobs.DirectoryFor(submission.Id) + "/..", TimeProvider.System), f.Assets).LoadAsync(f.Project.Id, submission.Id, ct);
        Assert.Equal(colour, drafts.Candidates[0].State.DraftColour);
        Assert.Equal(-10, drafts.Candidates[1].State.DraftColour!.Brightness);
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, ct)).ImagePublications);
        var assetsPath = Path.Combine(_directory, "projects", f.Project.Id.ToString("D"), "assets.json");
        if (OperatingSystem.IsWindows())
        {
            using var locked = TestProjectDatabase.HoldReadOnly(Path.GetDirectoryName(assetsPath)!);
            await Assert.ThrowsAnyAsync<Exception>(() => review.SaveAsync(f.Project.Id, submission.Id, first, 8, colour, ct));
            Assert.Equal(colour, (await review.LoadAsync(f.Project.Id, submission.Id, ct)).Candidates[0].State.SaveColour);
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => review.SaveDraftAsync(f.Project.Id, submission.Id, first, 0, new(), ct));
        }
        var saved = await review.SaveAsync(f.Project.Id, submission.Id, first, 8, OperatingSystem.IsWindows() ? new() { Brightness = -20 } : colour, ct);
        Assert.Single(saved.ImagePublications);
        var image = saved.Assets[0].Images.Single(i => i.Id == first);
        Assert.False(image.IsReference); Assert.False(image.IsCover); Assert.Equal(8, image.Generation!.Edit!.Regional!.EdgeBlend);
        Assert.Equal(colour, image.Generation.Edit.Regional.Colour);
        var expected = await RegionalImageEdits.CompositeAsync(request.Regional!, batch.Candidates[0].Image.Bytes, 8, colour, ct);
        await using (var published = await f.Assets.OpenImageAsync(f.Project.Id, f.AssetId, first, ct))
        {
            using var png = new MemoryStream(); await published!.Content.CopyToAsync(png, ct);
            using var actual = Image.Load<Rgba32>(png.ToArray()); using var wanted = Image.Load<Rgba32>(expected.Png);
            for (var y = 0; y < actual.Height; y++) for (var x = 0; x < actual.Width; x++) Assert.Equal(wanted[x, y], actual[x, y]);
        }
        await Task.WhenAll(review.SaveAsync(f.Project.Id, submission.Id, first, 0, ct), review.SaveAsync(f.Project.Id, submission.Id, first, 32, ct));
        Assert.Single((await f.Assets.LoadAsync(f.Project.Id, ct)).ImagePublications);
        await review.DiscardAsync(f.Project.Id, submission.Id, batch.Candidates[1].Image.CandidateId, ct);
        Assert.True((await review.LoadAsync(f.Project.Id, submission.Id, ct)).Candidates[1].State.Discarded);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => review.SaveAsync(f.Project.Id, submission.Id, batch.Candidates[1].Image.CandidateId, 0, ct));
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        await f.Assets.DeleteImageAsync(f.Project.Id, f.AssetId, f.References[0].ImageId, library.Revision, ct);
        await using var independent = await f.Assets.OpenImageAsync(f.Project.Id, f.AssetId, first, ct);
        Assert.NotNull(independent);
        var reopened = await new RegionalImageReview(new FileAiJobStore(f.Jobs.DirectoryFor(submission.Id) + "/..", TimeProvider.System), f.Assets).LoadAsync(f.Project.Id, submission.Id, ct);
        Assert.True(reopened.Candidates[0].State.Saved);
    }
}
