using System.Text.Json;
using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using TestContext = Xunit.TestContext;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Theory]
    [InlineData(false, Flux3ImageVariant.Distilled)] [InlineData(true, Flux3ImageVariant.Distilled)]
    [InlineData(false, Flux3ImageVariant.Base)] [InlineData(true, Flux3ImageVariant.Base)]
    public async Task Flux3QueuePublishesCapturedVariantAndRecoversWithoutRepeatingInference(bool editing, Flux3ImageVariant variant)
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.Flux3ImageKlein, editing);
        var ct = TestContext.Current.CancellationToken;
        f.Settings.Value = f.Settings.Value with { Flux3Image = Flux3ImageTests.Settings(variant).Flux3Image! with { ReferenceMegapixels = .5 } };
        var submission = await f.Capture(2); var context = await f.Claim(submission);
        var captured = submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        f.Settings.Value = f.Settings.Value with { Flux3Image = new() { Model = "changed.safetensors" } };
        f.FailDownloads = 1;
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, ct));
        Assert.Equal(AiJobRecovery.RetryOutput, error.Recovery);
        var outcome = await f.Worker.RecoverAsync(await f.Recover(context), submission.Snapshot, ct);
        Assert.Equal(2, outcome.CompletedCandidates); Assert.Equal(1, f.Posts); Assert.Equal(3, f.Views);
        var images = (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Where(i => i.Generation?.AiJobId == context.Job.Id).ToArray();
        Assert.Equal(2, images.Length);
        Assert.All(images, image =>
        {
            Assert.Equal(ImageWorkflow.Flux3ImageKlein, image.Generation!.Workflow);
            Assert.Equal(captured.Settings.Flux3Image, image.Generation.Flux3Image!.Settings);
            Assert.Equal(variant == Flux3ImageVariant.Distilled ? 16 : 50, image.Generation.Steps);
            Assert.False(Flux3ImagePolicy.InvalidMetadata(image.Generation, image.Width, image.Height));
            if (editing)
            {
                Assert.Equal(f.References, image.Generation.Edit!.References);
                Assert.Equal(captured.Edit!.SourceCrop, image.Generation.Edit.SourceCrop);
                Assert.Equal(captured.Edit.ReferenceCrops, image.Generation.Edit.ReferenceCrops);
                Assert.Equal("flux3-reference-latent", image.Generation.Edit.FitMode);
                Assert.Equal("", image.Generation.Edit.Lora);
            }
        });
        Assert.All(f.Graphs, graph => Assert.DoesNotContain("changed.safetensors", graph.GetRawText()));
    }
}

public sealed partial class AiSettingsStoreTests
{
    [Fact]
    public async Task Flux3SettingsKeepTheirVariantAndSamplingIndependentOfOtherModels()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await Store.SaveAsync(new(), cancellationToken: ct);
        var value = Flux3ImageTests.Settings(Flux3ImageVariant.Base).Flux3Image! with
        { Model = " models/flux3-image-klein-base-int8-convrot.safetensors ", Steps = 60, Guidance = 4, ReferenceMegapixels = .5 };
        await Store.SaveAsync(original with { Flux3Image = value }, cancellationToken: ct);
        var loaded = await Store.LoadAsync(ct);
        Assert.Equal(Flux3ImagePolicy.Normalize(value), loaded.Flux3Image);
        Assert.Equal(original.FluxKleinModel, loaded.FluxKleinModel);
        Assert.Equal(original.ComfyImageModel, loaded.ComfyImageModel);
        Assert.Equal(original.DefaultImageWorkflow, loaded.DefaultImageWorkflow);
    }
}

public sealed partial class AiSettingsComponentTests
{
    [Fact]
    public async Task Flux3VariantControlsAndExactFileChoicesSurviveRefreshAndDoNotChangeOtherWorkflows()
    {
        var original = _settings.Value;
        var page = Render<AiSettingsPage>();
        await page.Find("#ai-tab-images").ClickAsync(); await page.Find("#ai-image-Flux3ImageKlein").ClickAsync();
        Assert.Empty(page.FindAll("#comfy-image-edit-lora")); Assert.Empty(page.FindAll("#flux3-guidance"));
        await page.Find("#flux3-variant").ChangeAsync(new() { Value = "Base" });
        page.WaitForAssertion(() => Assert.Equal("50", page.Find("#flux3-steps").GetAttribute("value")));
        Assert.Equal("3.5", page.Find("#flux3-guidance").GetAttribute("value"));
        await page.Find("#comfy-image-model").ChangeAsync(new() { Value = "flux3-image-klein-base-int8-convrot.safetensors" });
        await page.Find("#flux3-reference-size").ChangeAsync(new() { Value = "0.5" });
        _imageGenerator.DiffusionModels = [new("other.safetensors", "Other")];
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Refresh image models").ClickAsync();
        page.WaitForAssertion(() => Assert.Equal("flux3-image-klein-base-int8-convrot.safetensors", page.Find("#comfy-image-model").GetAttribute("value")));
        await page.Find("#image-workflow-form").SubmitAsync();
        page.WaitForAssertion(() => Assert.Equal(Flux3ImageVariant.Base, _settings.Value.Flux3Image!.Variant));
        Assert.Equal(.5, _settings.Value.Flux3Image!.ReferenceMegapixels);
        Assert.Equal(original.DefaultImageWorkflow, _settings.Value.DefaultImageWorkflow);
        Assert.Equal(original.FluxKleinModel, _settings.Value.FluxKleinModel);
        Assert.Equal(original.ComfyImageModel, _settings.Value.ComfyImageModel);
    }
}
