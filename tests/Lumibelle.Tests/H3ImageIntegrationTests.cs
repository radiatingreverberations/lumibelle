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
    [Fact]
    public async Task H3QueueRetainsCapturedFilesAndPublishesEditedCandidates()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.MiniMaxH3, true);
        var ct = TestContext.Current.CancellationToken;
        var submission = await f.Capture(2); var context = await f.Claim(submission);
        var captured = submission.Snapshot.Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)!;
        f.Settings.Value = f.Settings.Value with { H3 = f.Settings.Value.H3 with { Model = "changed.safetensors" } };
        var outcome = await f.Worker.ExecuteAsync(context, submission.Snapshot, ct);
        Assert.Equal(2, outcome.CompletedCandidates); Assert.Equal(1, f.Posts); Assert.Equal(2, f.Views);
        var images = (await f.Assets.LoadAsync(f.Project.Id, ct)).Assets[0].Images.Where(i => i.Generation?.AiJobId == context.Job.Id).ToArray();
        Assert.Equal(2, images.Length);
        Assert.All(images, image =>
        {
            Assert.Equal(AssetImageOrigin.Edited, image.Origin);
            Assert.Equal(ImageWorkflow.MiniMaxH3, image.Generation!.Workflow);
            Assert.Equal(captured.Settings.H3.Model, image.Generation.DiffusionModel);
            Assert.Equal(20, image.Generation.Steps);
            Assert.Equal(H3ImagePolicy.StandardPreset, image.Generation.H3Image!.Preset);
            Assert.Equal(H3ImagePolicy.FitMode, image.Generation.Edit!.FitMode);
            Assert.Equal(f.References, image.Generation.Edit.References);
            Assert.DoesNotContain("<Picture", image.Generation.Prompt);
            Assert.False(H3ImagePolicy.InvalidMetadata(image.Generation, image.Width, image.Height));
        });
        Assert.All(f.Graphs, graph => Assert.DoesNotContain("changed.safetensors", graph.GetRawText()));
    }

    [Fact]
    public async Task H3CannotBeQueuedToCreateAnImage()
    {
        using var f = await ImageJobFixture.Create(this, ImageWorkflow.MiniMaxH3, false);
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => f.Capture());
        Assert.Equal(H3ImagePolicy.EditOnlyMessage, error.Message);
    }
}

public sealed partial class AiSettingsComponentTests
{
    [Fact]
    public async Task H3SettingsPointToVideoModelsAndNeverReplaceImageDrafts()
    {
        var initial = _settings.Value;
        _imageEditor.Result = _ => new(true, "MiniMax H3 editing is ready.", []);
        var page = Render<AiSettingsPage>();
        await page.Find("#ai-tab-images").ClickAsync(); await page.Find("#ai-image-MiniMaxH3").ClickAsync();
        Assert.Contains(initial.H3.Model, page.Find("#image-workflow-form").TextContent);
        Assert.Empty(page.FindAll("#comfy-image-model"));
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Save MiniMax H3"));
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Refresh image models").ClickAsync();
        page.WaitForAssertion(() => Assert.Contains("MiniMax H3 editing is ready.", page.Find("#image-availability-status").TextContent));
        Assert.Equal(initial, _settings.Value);
        await page.Find("#ai-image-Krea2").ClickAsync();
        Assert.Equal(initial.ComfyImageModel, page.Find("#comfy-image-model").GetAttribute("value"));
    }
}
