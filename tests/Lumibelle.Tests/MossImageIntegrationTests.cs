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
    [InlineData(false)] [InlineData(true)]
    public async Task MossQueueRetainsCapturedSettingsAndPublishesVerifiedCandidates(bool editing)
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.MossImage, editing);
        var ct = TestContext.Current.CancellationToken;
        f.Settings.Value = f.Settings.Value with { MossImage = MossImageTests.Settings.MossImage };
        var submission = await f.Capture(2); var context = await f.Claim(submission);
        var captured = submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        f.Settings.Value = f.Settings.Value with { MossImage = new() { Model = "changed.safetensors", Guidance = 8 } };
        var outcome = await f.Worker.ExecuteAsync(context, submission.Snapshot, ct);
        Assert.Equal(2, outcome.CompletedCandidates); Assert.Equal(1, f.Posts); Assert.Equal(2, f.Views);
        var images = (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Where(i => i.Generation?.AiJobId == context.Job.Id).ToArray();
        Assert.Equal(2, images.Length);
        Assert.All(images, image =>
        {
            Assert.Equal(ImageWorkflow.MossImage, image.Generation!.Workflow);
            Assert.Equal(captured.Settings.MossImage, image.Generation.MossImage!.Settings);
            Assert.Equal(editing ? 30 : 50, image.Generation.Steps);
            Assert.False(MossImagePolicy.InvalidMetadata(image.Generation, image.Width, image.Height));
            if (editing) Assert.Equal(f.References, image.Generation.Edit!.References);
        });
        Assert.All(f.Graphs, graph => Assert.DoesNotContain("changed.safetensors", graph.GetRawText()));
    }
}

public sealed partial class AiSettingsStoreTests
{
    [Fact]
    public async Task MossSettingsPersistIndependentlyAndNormalizeExactFileChoices()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = await Store.SaveAsync(new(), cancellationToken: ct);
        var saved = await Store.SaveAsync(initial with { MossImage = new() { Model = " folder/moss.safetensors ", EditLora = " edit/adapter_model.safetensors ", EditFormat = "ai-toolkit", Steps = 60, Guidance = 4.5 } }, cancellationToken: ct);
        var reopened = await Store.LoadAsync(ct);
        Assert.Equal("folder/moss.safetensors", reopened.MossImage!.Model);
        Assert.Equal("edit/adapter_model.safetensors", reopened.MossImage.EditLora);
        Assert.Equal("ai-toolkit", reopened.MossImage.EditFormat); Assert.Equal(60, reopened.MossImage.Steps); Assert.Equal(4.5, reopened.MossImage.Guidance);
        Assert.Equal(initial.ComfyImageModel, reopened.ComfyImageModel); Assert.Equal(initial.DefaultImageWorkflow, reopened.DefaultImageWorkflow);
        Assert.Equal(saved.MossImage, reopened.MossImage);
    }
}

public sealed partial class AiSettingsComponentTests
{
    [Fact]
    public async Task MossSettingsHaveIndependentDraftsAndRefreshNeverReplacesMissingChoices()
    {
        var initial = _settings.Value;
        var page = Render<AiSettingsPage>();
        await page.Find("#ai-tab-images").ClickAsync(); await page.Find("#ai-image-MossImage").ClickAsync();
        Assert.Empty(page.FindAll("#comfy-image-edit-lora"));
        await page.Find("#moss-steps").ChangeAsync(new() { Value = "64" });
        await page.Find("#moss-edit-format").ChangeAsync(new() { Value = "ai-toolkit" });
        _imageGenerator.DiffusionModels = [new("other.safetensors", "Other")];
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Refresh image models").ClickAsync();
        page.WaitForAssertion(() => Assert.Equal(new MossImageSettings().Model, page.Find("#comfy-image-model").GetAttribute("value")));
        await page.Find("#image-workflow-form").SubmitAsync();
        page.WaitForAssertion(() => Assert.Equal(64, _settings.Value.MossImage!.Steps));
        Assert.Equal("ai-toolkit", _settings.Value.MossImage!.EditFormat);
        Assert.Equal(initial.DefaultImageWorkflow, _settings.Value.DefaultImageWorkflow);
        Assert.Equal(initial.ComfyImageModel, _settings.Value.ComfyImageModel);
        await page.Find("#ai-image-Krea2").ClickAsync();
        Assert.Equal(initial.ComfyImageEditLora, page.Find("#comfy-image-edit-lora").GetAttribute("value"));
    }
}
