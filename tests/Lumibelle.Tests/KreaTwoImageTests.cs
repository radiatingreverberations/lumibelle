using System.Net;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using SixLabors.ImageSharp;

namespace Lumibelle.Tests;

public sealed class KreaTwoImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ReferenceEditRequest Request(Guid asset, Guid image) => new()
    { Workflow = ImageWorkflow.Krea2, SourceAssetId = asset, SourceImageId = image, Prompt = "Place this person by the desk.", AspectRatio = "16:9", Seed = 22 };
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    private static ScriptedHttpHandler Handler(string? omit = null) => new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
    {
        "/object_info" => Json(AssetAiTests.EditCatalog(omitTwoImageInput: omit)),
        "/prompt" => Json("{\"prompt_id\":\"83bb2e2a-3642-47d3-8edc-26707e74d8ca\"}"),
        "/history/83bb2e2a-3642-47d3-8edc-26707e74d8ca" => Json("""{"83bb2e2a-3642-47d3-8edc-26707e74d8ca":{"status":{"completed":true,"status_str":"success"},"outputs":{"13":{"images":[{"filename":"edit.png","subfolder":"","type":"temp"}]}}}}"""),
        "/view" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(AssetStoreTests.Png(3, 2)) },
        _ => new(HttpStatusCode.NotFound)
    }));
    private static ComfyImageService Service(ScriptedHttpHandler handler, FakeAiSettingsStore settings)
    {
        var clients = new TestHttpFactory(handler); var monitor = TestComfy.Monitor();
        return new(settings, new(clients, settings, TimeProvider.System, monitor),
            new(clients, settings, TimeProvider.System, monitor), new(clients, TimeProvider.System, monitor), new FakeProjectAiPreferencesStore(), new FakeAssetStore(Guid.NewGuid()));
    }

    [Fact]
    public async Task BatchExtensionKeepsCapturedSettingsEvenWhenGlobalConfigurationChanges()
    {
        var handler = Handler(); var settings = new FakeAiSettingsStore();
        var captured = settings.Value;
        var service = Service(handler, settings);
        settings.Value = settings.Value with { ComfyImageModel = "unavailable.safetensors", DefaultImageWorkflow = ImageWorkflow.Flux2Klein9bKv };
        var asset = Guid.NewGuid(); var image = Guid.NewGuid();
        using var source = new MemoryStream(AssetStoreTests.Png(10, 10));
        var updates = new List<ReferenceGenerationUpdate>();
        await foreach (var update in service.EditAsync(Request(asset, image) with { SettingsSnapshot = captured }, source, Ct))
            if (update.Image is not null) updates.Add(update);
        var result = Assert.Single(updates);
        Assert.Equal(captured.ComfyImageModel, result.Metadata!.DiffusionModel);
        Assert.Equal(ImageWorkflow.Krea2, result.Metadata.Workflow);
        Assert.Single(handler.Requests, r => r.Path == "/object_info");
    }

    [Theory]
    [InlineData("model")]
    [InlineData("text encoder")]
    [InlineData("VAE")]
    [InlineData("identity-edit LoRA")]
    public async Task DiscoveryNamesOnlyTheUnavailableFile(string field)
    {
        var handler = Handler();
        var settings = new FakeAiSettingsStore();
        var initial = settings.Value;
        var files = new Dictionary<string, string>
        {
            ["model"] = initial.ComfyImageModel, ["text encoder"] = initial.ComfyImageTextEncoder,
            ["VAE"] = initial.ComfyImageVae, ["identity-edit LoRA"] = initial.ComfyImageEditLora
        };
        var missing = "missing/" + files[field];
        settings.Value = field switch
        {
            "model" => initial with { ComfyImageModel = missing },
            "text encoder" => initial with { ComfyImageTextEncoder = missing },
            "VAE" => initial with { ComfyImageVae = missing },
            _ => initial with { ComfyImageEditLora = missing }
        };
        var editor = new ComfyReferenceImageEditor(new TestHttpFactory(handler), settings, TimeProvider.System, TestComfy.Monitor());
        var result = await editor.CheckAsync(cancellationToken: Ct);
        Assert.False(result.Success);
        Assert.Contains("unavailable", result.Message);
        Assert.Contains($"{field} '{missing}'", result.Message);
        foreach (var other in files.Keys.Where(key => key != field))
            Assert.DoesNotContain($"{other} '{files[other]}'", result.Message);
        Assert.All(handler.Requests, request => Assert.Equal("/object_info", request.Path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void GraphGroundsBothEncodersAndUsesDistinctFidelityInputs(int count)
    {
        var request = Request(Guid.NewGuid(), Guid.NewGuid()) with { BaseReferenceBoost = 1.75f, ReferenceBoost = 5.5f };
        var graph = JsonSerializer.SerializeToElement(ComfyReferenceImageEditor.BuildWorkflow(new(), request,
            count == 1 ? ["BASE"] : ["BASE", "SUBJECT"], 22, 1344, 768)).GetProperty("prompt");
        var patch = graph.GetProperty("8").GetProperty("inputs");
        Assert.Equal(5.5f, patch.GetProperty("ref_boost").GetSingle());
        Assert.Equal("7", patch.GetProperty("target_latent")[0].GetString());
        Assert.Equal("6", patch.GetProperty("source_latent")[0].GetString());
        Assert.Equal("5", patch.GetProperty("source_image")[0].GetString());
        foreach (var node in new[] { "9", "10" })
        {
            var encoder = graph.GetProperty(node).GetProperty("inputs");
            Assert.Equal("5", encoder.GetProperty("image")[0].GetString());
            Assert.Equal(count == 2, encoder.TryGetProperty("image_b", out var second));
            if (count == 2) Assert.Equal("14", second[0].GetString());
        }
        Assert.Equal("", graph.GetProperty("10").GetProperty("inputs").GetProperty("prompt").GetString());
        Assert.Equal(count == 2, patch.TryGetProperty("ref_boost_a", out var boost));
        Assert.Equal(count == 2, patch.TryGetProperty("source_latent_b", out _));
        Assert.Equal(count == 2, patch.TryGetProperty("source_image_b", out _));
        if (count == 2)
        {
            Assert.Equal(1.75f, boost.GetSingle());
            Assert.Equal("15", patch.GetProperty("source_latent_b")[0].GetString());
            Assert.Equal("14", patch.GetProperty("source_image_b")[0].GetString());
            Assert.Equal("14", graph.GetProperty("15").GetProperty("inputs").GetProperty("pixels")[0].GetString());
            Assert.Equal("SUBJECT", graph.GetProperty("14").GetProperty("inputs").GetProperty("image").GetString());
        }
    }

    [Theory]
    [InlineData("source_latent_b")]
    [InlineData("source_image_b")]
    [InlineData("ref_boost_a")]
    [InlineData("image_b")]
    public async Task OlderNodesRetainSingleImageButRejectTwoImagesBeforeSubmission(string missing)
    {
        var handler = Handler(missing); var service = Service(handler, new());
        var check = await ((IReferenceImageEditor)service).CheckAsync(cancellationToken: Ct);
        Assert.True(check.Success); Assert.Equal(1, check.MaximumReferences); Assert.Contains("Update", check.ReferenceCapabilityMessage);
        var asset = Guid.NewGuid(); var image = Guid.NewGuid();
        using var first = new MemoryStream(AssetStoreTests.Png(10, 10));
        using var second = new MemoryStream(AssetStoreTests.Png(8, 12));
        var error = await Assert.ThrowsAsync<AiGenerationException>(async () =>
        { await foreach (var _ in service.EditAsync(Request(asset, image), [new(asset, image, first), new(asset, Guid.NewGuid(), second)], Ct)) { } });
        Assert.Contains("Update", error.Message); Assert.DoesNotContain(handler.Requests, r => r.Path == "/prompt");
        first.Position = 0;
        var saved = new List<ReferenceGenerationUpdate>();
        await foreach (var update in service.EditAsync(Request(asset, image), first, Ct)) if (update.Image is not null) saved.Add(update);
        Assert.Single(saved);
    }

    [Fact]
    public async Task TwoImageRunCapturesCropsSettingsAndOrderedLineageAcrossCandidates()
    {
        var handler = Handler(); var settings = new FakeAiSettingsStore(); var service = Service(handler, settings);
        var originalModel = settings.Value.ComfyImageModel;
        var asset = Guid.NewGuid(); var image = Guid.NewGuid(); var other = new AssetImageReference(Guid.NewGuid(), Guid.NewGuid());
        using var first = new MemoryStream(AssetStoreTests.Png(40, 60));
        using var second = new MemoryStream(AssetStoreTests.Jpeg(80, 40));
        var crops = new List<AssetReferenceCrop> { new(other, new() { X = .5, Width = .5, Height = 1 }) };
        var sources = new List<ReferenceImageSource> { new(asset, image, first), new(other.AssetId, other.ImageId, second) };
        var request = Request(asset, image) with { Count = 2, BaseReferenceBoost = 1.5f, ReferenceBoost = 6,
            SourceCrop = new() { Y = .5, Height = .5 }, ReferenceCrops = crops };
        var saved = new List<ReferenceGenerationUpdate>();
        await foreach (var update in service.EditAsync(request, sources, Ct))
        {
            // Mutation after the very first yielded progress must not affect preparation or later candidates.
            sources.Clear(); crops.Clear(); settings.Value = settings.Value with { ComfyImageModel = "changed", ComfyUrl = "http://changed.invalid" };
            if (update.Image is not null) saved.Add(update);
        }
        Assert.Equal(2, saved.Count);
        Assert.Equal(new long[] { 22, 23 }, saved.Select(s => s.Metadata!.Seed));
        foreach (var update in saved)
        {
            var meta = update.Metadata!; var edit = meta.Edit!;
            Assert.Equal(originalModel, meta.DiffusionModel); Assert.Equal(ImageWorkflow.Krea2, meta.Workflow);
            Assert.Equal(new[] { new AssetImageReference(asset, image), other }, edit.References);
            Assert.Equal(1.5f, edit.BaseReferenceBoost); Assert.Equal(6, edit.ReferenceBoost);
            Assert.Equal(.5, Assert.Single(edit.ReferenceCrops).Crop.X);
        }
        Assert.Single(handler.Requests, r => r.Path == "/object_info");
        foreach (var body in handler.Requests.Where(r => r.Path == "/prompt"))
        {
            var graph = JsonDocument.Parse(body.Body).RootElement.GetProperty("prompt");
            using var baseImage = Image.Load(Convert.FromBase64String(graph.GetProperty("5").GetProperty("inputs").GetProperty("image").GetString()!));
            using var reference = Image.Load(Convert.FromBase64String(graph.GetProperty("14").GetProperty("inputs").GetProperty("image").GetString()!));
            Assert.Equal((40, 30), (baseImage.Width, baseImage.Height)); Assert.Equal((40, 40), (reference.Width, reference.Height));
        }
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("base")]
    [InlineData("duplicate")]
    [InlineData("bounds")]
    [InlineData("null")]
    public async Task InvalidAdditionalCropsNeverReachProvider(string kind)
    {
        var handler = Handler(); var service = Service(handler, new());
        var asset = Guid.NewGuid(); var image = Guid.NewGuid(); var other = new AssetImageReference(asset, Guid.NewGuid());
        var item = new AssetReferenceCrop(kind == "foreign" ? new(Guid.NewGuid(), Guid.NewGuid()) : kind == "base" ? new(asset, image) : other,
            kind == "bounds" ? new() { X = double.NaN } : new() { Width = .5 });
        var request = Request(asset, image) with { ReferenceCrops = kind == "null" ? null! : kind == "duplicate" ? [item, item] : [item] };
        using var first = new MemoryStream(AssetStoreTests.Png(4, 4)); using var second = new MemoryStream(AssetStoreTests.Png(4, 4));
        await Assert.ThrowsAsync<AiGenerationException>(async () =>
        { await foreach (var _ in service.EditAsync(request, [new(asset, image, first), new(asset, other.ImageId, second)], Ct)) { } });
        Assert.Empty(handler.Requests);
    }
}
