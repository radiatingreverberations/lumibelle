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
    [InlineData(ImageWorkflow.Fibo2, CaptionImageVariant.Base, false)]
    [InlineData(ImageWorkflow.Fibo2, CaptionImageVariant.Base, true)]
    [InlineData(ImageWorkflow.Fibo2, CaptionImageVariant.Turbo, true)]
    [InlineData(ImageWorkflow.ZImage, CaptionImageVariant.Base, false)]
    [InlineData(ImageWorkflow.ZImage, CaptionImageVariant.Turbo, false)]
    public async Task CaptionImageQueueRecoversExactCapturedVariantWithoutRepeatingInference(ImageWorkflow workflow, CaptionImageVariant variant, bool editing)
    {
        using var f = await ImageJobFixture.Create(this, workflow, editing); var ct = TestContext.Current.CancellationToken;
        var settings = CaptionImageTests.Settings(workflow, variant);
        f.Settings.Value = f.Settings.Value with { Fibo2 = settings.Fibo2, ZImage = settings.ZImage };
        var submission = await f.Capture(2); var context = await f.Claim(submission);
        var captured = submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        var expected = CaptionImagePolicy.Settings(captured.Settings, workflow);
        f.Settings.Value = f.Settings.Value with { Fibo2 = CaptionImagePolicy.Defaults(ImageWorkflow.Fibo2) with { Model = "changed.safetensors" },
            ZImage = CaptionImagePolicy.Defaults(ImageWorkflow.ZImage) with { Model = "changed.safetensors" } };
        f.FailDownloads = 1;
        var error = await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, ct));
        Assert.Equal(AiJobRecovery.RetryOutput, error.Recovery);
        Assert.Equal(2, (await f.Worker.RecoverAsync(await f.Recover(context), submission.Snapshot, ct)).CompletedCandidates);
        Assert.Equal(1, f.Posts);
        var images = (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Where(i => i.Generation?.AiJobId == context.Job.Id).ToArray();
        Assert.Equal(2, images.Length);
        Assert.All(images, image =>
        {
            Assert.Equal(expected, image.Generation!.CaptionImage!.Settings);
            Assert.False(CaptionImagePolicy.InvalidMetadata(image.Generation, image.Width, image.Height));
            Assert.Equal(CaptionImagePolicy.Steps(expected, workflow), image.Generation.Steps);
            if (editing) { Assert.Equal(f.References, image.Generation.Edit!.References); Assert.Equal(captured.Edit!.ReferenceCrops, image.Generation.Edit.ReferenceCrops); }
        });
        Assert.All(f.Graphs, graph => Assert.DoesNotContain("changed.safetensors", graph.GetRawText()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task QwenImage21TurboQueueCapturesEightStepsAndPreservesTheVariantAcrossRecovery(bool editing)
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.QwenImage21, editing); var ct = TestContext.Current.CancellationToken;
        f.Settings.Value = f.Settings.Value with { QwenImage21 = new() { Model = "qwen_image_2.1_turbo_int8_convrot.safetensors", Variant = QwenImage21Variant.Turbo } };
        var submission = await f.Capture(2, qwen: new(Steps: 25)); var context = await f.Claim(submission);
        var captured = submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(8, QwenImage21Policy.Options(captured).Steps);
        f.Settings.Value = f.Settings.Value with { QwenImage21 = new() };
        f.FailDownloads = 1;
        await Assert.ThrowsAsync<AiJobRecoveryException>(() => f.Worker.ExecuteAsync(context, submission.Snapshot, ct));
        Assert.Equal(2, (await f.Worker.RecoverAsync(await f.Recover(context), submission.Snapshot, ct)).CompletedCandidates);
        Assert.Equal(1, f.Posts);
        var images = (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Where(i => i.Generation?.AiJobId == context.Job.Id).ToArray();
        Assert.Equal(2, images.Length); Assert.All(images, image => { Assert.Equal(8, image.Generation!.Steps); Assert.Equal(QwenImage21Variant.Turbo, image.Generation.QwenImage21!.Variant); });
        Assert.Contains("SamplerCustomAdvanced", Assert.Single(f.Graphs).GetRawText());
    }
}

public sealed partial class AiSettingsStoreTests
{
    [Fact]
    public async Task NewCaptionModelSettingsAndQwenTurboPersistIndependently()
    {
        var ct = TestContext.Current.CancellationToken; var original = await Store.SaveAsync(new(), cancellationToken: ct);
        var saved = await Store.SaveAsync(original with
        {
            Fibo2 = CaptionImagePolicy.Defaults(ImageWorkflow.Fibo2) with { Model = " folder/fibo2_dev_int8_convrot.safetensors ", Steps = 60 },
            ZImage = CaptionImagePolicy.Defaults(ImageWorkflow.ZImage) with { Shift = 4 },
            QwenImage21 = new() { Variant = QwenImage21Variant.Turbo, Model = "qwen_image_2.1_turbo_int8_convrot.safetensors" }
        }, cancellationToken: ct);
        var loaded = await Store.LoadAsync(ct);
        Assert.Equal(saved.Fibo2, loaded.Fibo2); Assert.Equal(saved.ZImage, loaded.ZImage); Assert.Equal(saved.QwenImage21, loaded.QwenImage21);
        Assert.Equal("folder/fibo2_dev_int8_convrot.safetensors", loaded.Fibo2!.Model);
        Assert.Equal(original.ComfyImageModel, loaded.ComfyImageModel); Assert.Equal(original.DefaultImageWorkflow, loaded.DefaultImageWorkflow);
    }
}

public sealed partial class AiSettingsComponentTests
{
    [Theory]
    [InlineData(ImageWorkflow.Fibo2)] [InlineData(ImageWorkflow.ZImage)]
    public async Task CaptionSettingsHaveIndependentDraftsAndRefreshDoesNotSubstituteMissingFiles(ImageWorkflow workflow)
    {
        var original = _settings.Value; var page = Render<AiSettingsPage>();
        await page.Find("#ai-tab-images").ClickAsync(); await page.Find("#ai-image-" + workflow).ClickAsync();
        Assert.Empty(page.FindAll("#comfy-image-edit-lora"));
        await page.Find("#caption-image-steps").ChangeAsync(new() { Value = "40" });
        _imageGenerator.DiffusionModels = [new("other.safetensors", "Other")];
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Refresh image models").ClickAsync();
        page.WaitForAssertion(() => Assert.Equal(CaptionImagePolicy.Defaults(workflow).Model, page.Find("#comfy-image-model").GetAttribute("value")));
        await page.Find("#image-workflow-form").SubmitAsync();
        page.WaitForAssertion(() => Assert.Equal(40, CaptionImagePolicy.Settings(_settings.Value, workflow).Steps));
        Assert.Equal(original.ComfyImageModel, _settings.Value.ComfyImageModel); Assert.Equal(original.DefaultImageWorkflow, _settings.Value.DefaultImageWorkflow);
    }
}
