using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class MiniMaxH3ImageTests
{
    internal static AiSettings Settings => new() { DefaultImageWorkflow = ImageWorkflow.MiniMaxH3Image, MiniMaxH3Image = new() };

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(16)]
    public void NativeGraphUsesImageVaeAndOrderedPictures(int count)
    {
        const string prompt = "The jacket in Image 1 is <Subject 1>. The target image has a sign reading 'ÖPPET'.";
        var graph = JsonSerializer.SerializeToElement(ComfyMiniMaxH3Images.BuildWorkflow(Settings, prompt, 42, 2048, 2048,
            Enumerable.Range(0, count).Select(i => "picture" + i).ToArray())).GetProperty("prompt");
        var conditioning = graph.GetProperty("4").GetProperty("inputs");
        Assert.Equal("MiniMaxH3ImageConditioning", graph.GetProperty("4").GetProperty("class_type").GetString());
        Assert.Equal("3", conditioning.GetProperty("vae")[0].GetString());
        Assert.Contains("<Subject 1>", conditioning.GetProperty("prompt").GetString());
        Assert.Contains("'ÖPPET'", conditioning.GetProperty("prompt").GetString());
        for (var i = 1; i <= count; i++) Assert.Equal($"reference_{i}", conditioning.GetProperty($"images.image_{i}")[0].GetString());
        var sampling = graph.GetProperty("7").GetProperty("inputs");
        Assert.Equal(6, sampling.GetProperty("steps").GetInt32());
        Assert.Equal(1, sampling.GetProperty("cfg").GetDouble());
        Assert.Equal("euler", sampling.GetProperty("sampler_name").GetString());
        Assert.Equal("simple", sampling.GetProperty("scheduler").GetString());
        Assert.Equal(1, sampling.GetProperty("latent_image")[1].GetInt32());
        Assert.DoesNotContain("audio_vae", graph.GetRawText());
        Assert.DoesNotContain("LatentCut", graph.GetRawText());
        Assert.Throws<AiGenerationException>(() => ComfyMiniMaxH3Images.BuildWorkflow(Settings, prompt, 42, 2048, 2048, Enumerable.Repeat("picture", 17).ToArray()));
    }

    [Theory]
    [InlineData("1:1", 4096, 4096)] [InlineData("4:3", 4736, 3552)]
    [InlineData("3:2", 5024, 3360)] [InlineData("16:9", 5472, 3072)] [InlineData("21:9", 6272, 2688)]
    public void NativeSizesMatchTheReleasedRecipe(string aspect, int width, int height)
    {
        Assert.Equal((width, height), MiniMaxH3ImagePolicy.Size(aspect, null, 4096));
        var small = MiniMaxH3ImagePolicy.Size(aspect, null, 2048);
        Assert.Equal(0, small.Width % 32); Assert.Equal(0, small.Height % 32);
        Assert.InRange((long)small.Width * small.Height, 4_000_000, 4_300_000);
    }

    [Fact]
    public void CatalogReportsMissingNodesAndFilesAndAcceptsPrefixReferenceInputs()
    {
        var graph = JsonSerializer.SerializeToElement(ComfyMiniMaxH3Images.BuildWorkflow(Settings, "A jacket", 1, 2048, 2048, ["picture"])).GetProperty("prompt");
        var nodes = new JsonObject();
        foreach (var node in graph.EnumerateObject())
        {
            var inputs = new JsonObject();
            foreach (var input in node.Value.GetProperty("inputs").EnumerateObject()) inputs[input.Name] = new JsonArray("ANY");
            nodes[node.Value.GetProperty("class_type").GetString()!] = new JsonObject { ["input"] = new JsonObject { ["required"] = inputs } };
        }
        JsonObject Required(string name) => nodes[name]!["input"]!["required"]!.AsObject();
        Required("UNETLoader")["unet_name"] = new JsonArray(new JsonArray(Settings.MiniMaxH3Image!.Model));
        Required("CLIPLoader")["clip_name"] = new JsonArray(new JsonArray(Settings.MiniMaxH3Image!.TextEncoder));
        Required("CLIPLoader")["type"] = new JsonArray(new JsonArray("minimax"));
        Required("VAELoader")["vae_name"] = new JsonArray(new JsonArray(Settings.MiniMaxH3Image!.Vae));
        Required("KSampler")["sampler_name"] = new JsonArray(new JsonArray("euler"));
        Required("KSampler")["scheduler"] = new JsonArray(new JsonArray("simple"));
        Required("MiniMaxH3ImageConditioning")["images"] = JsonNode.Parse("[\"AUTOGROW\",{\"template\":{\"prefix\":\"image_\",\"max\":100,\"input\":[\"IMAGE\"]}}]");
        nodes["MiniMaxH3ImageConditioning"]!["output"] = new JsonArray("CONDITIONING", "LATENT");
        ComfyImageConfiguration Check() => ComfyMiniMaxH3Images.CheckCatalog(JsonSerializer.SerializeToElement(nodes), Settings, true);
        Assert.True(Check().Success, Check().Message);
        Required("VAELoader")["vae_name"] = new JsonArray(new JsonArray("video_vae.safetensors"));
        Assert.Contains("image VAE", Check().Message);
        nodes.Remove("MiniMaxH3ImageConditioning");
        Assert.Contains("Update ComfyUI", Check().Message);
    }
}

public sealed partial class AssetStoreTests
{
    [Fact]
    public async Task QueuedH3EditCapturesSixteenPicturesAndRejectsSeventeen()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.MiniMaxH3Image, true);
        var ct = TestContext.Current.CancellationToken;
        var library = await f.Assets.LoadAsync(f.Project.Id, ct);
        while (f.References.Count < 16)
        {
            using var source = new MemoryStream(Png(32, 32));
            library = await f.Assets.AddImageAsync(f.Project.Id, f.AssetId, source, new("reference.png", [], AssetImageOrigin.Imported), library.Revision, ct);
            f.References.Add(new(f.AssetId, library.Assets[0].Images[^1].Id));
        }
        var submission = await f.Capture();
        var captured = submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(f.References, captured.Inputs.Select(i => i.Reference));
        f.References.Add(new(f.AssetId, Guid.NewGuid()));
        await Assert.ThrowsAsync<AiGenerationException>(() => f.Capture());
    }

    [Fact]
    public async Task NativeH3PngAboveTwentyFiveMegabytesPublishesAndCopiesBetweenProjects()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.MiniMaxH3Image, false);
        using var png = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(4096, 4096);
        using var output = new MemoryStream();
        png.Save(output, new SixLabors.ImageSharp.Formats.Png.PngEncoder
        { CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.NoCompression });
        f.OutputPng = output.ToArray();
        Assert.True(f.OutputPng.Length > FileAssetStore.MaximumImageBytes);
        var submission = await f.Capture(resolution: 4096); var context = await f.Claim(submission);
        await f.Worker.ExecuteAsync(context, submission.Snapshot, TestContext.Current.CancellationToken);
        var library = await f.Assets.LoadAsync(f.Project.Id, TestContext.Current.CancellationToken);
        var generated = library.Assets[0].Images.Single(i => i.Generation?.AiJobId == context.Job.Id);
        var source = new AssetReuseSelection(f.Project.Id, f.AssetId, AssetReuseKind.Image, generated.Id);
        var content = AssetReusePolicy.Capture(library, source);
        var (destination, _) = CreateStore("Large image copy");
        var copied = await f.Assets.ReuseAsync(new(Guid.NewGuid(), source, null, AssetReusePolicy.Hash(content), new(destination.Id, "Copied H3 image")), TestContext.Current.CancellationToken);
        Assert.Equal(4096, copied.Library.Assets.Single().Images.Single().Width);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DedicatedH3ImageQueueKeepsCapturedFilesAndRecoversOutput(bool editing)
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.MiniMaxH3Image, editing);
        f.Settings.Value = f.Settings.Value with { MiniMaxH3Image = new() };
        f.OutputPng = Png(2048, 2048);
        var submission = await f.Capture(); var context = await f.Claim(submission);
        f.Settings.Value = f.Settings.Value with { MiniMaxH3Image = new() { Model = "changed" } };
        f.FailDownloads = 1;
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, TestContext.Current.CancellationToken));
        await f.Worker.RecoverAsync(await f.Recover(context), submission.Snapshot, TestContext.Current.CancellationToken);
        var image = (await f.Assets.LoadAsync(f.Project.Id, TestContext.Current.CancellationToken)).Assets[0].Images.Single(i => i.Generation?.AiJobId == context.Job.Id);
        Assert.Equal(1, f.Posts);
        Assert.Equal(new MiniMaxH3ImageSettings(), image.Generation!.MiniMaxH3Image!.Settings);
        Assert.False(MiniMaxH3ImagePolicy.InvalidMetadata(image.Generation, image.Width, image.Height));
        if (editing) Assert.Equal(f.References, image.Generation.Edit!.References);
    }
}
