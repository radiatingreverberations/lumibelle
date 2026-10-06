using Bunit;
using lumibelle.Models;
using lumibelle.Services.Assets;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task QwenRegionEditorUsesTheSelectedResolutionAndReturnsToTheSameDraft()
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "portrait.png", ContentType = "image/png", Width = 96, Height = 128 };
        var asset = Asset("Mira") with { Images = [image] };
        _assets.Library = _assets.Library with { Assets = [asset] };
        var page = Page();
        page.WaitForElement("#asset-image-workflow").Change(ImageWorkflow.QwenImage21.ToString());
        await page.Find(".media-select").ClickAsync(new());
        page.Find("#image-resolution").Change("2048");
        page.Find("#image-prompt").Input("Change the background");
        Assert.Empty(page.FindAll(".regional-edit-button"));
        await page.Find(".manage-image-inputs").ClickAsync(new());
        await _dialogs.Find(".reference-protect-button").ClickAsync(new());
        var manager = _dialogs.FindComponent<lumibelle.Components.Assets.ImageEditReferenceManager>();
        var editor = manager.FindComponent<lumibelle.Components.Assets.ImageRegionEditor>();
        Assert.Equal(new QwenImage21Options(2048, 25), editor.Instance.QwenImage21);
        Assert.Equal(ImageAspectPolicy.FromImage1, editor.Instance.Aspect);
        await page.InvokeAsync(() => editor.Instance.Applied.InvokeAsync(new RegionalImageSelection
        {
            Source = new(asset.Id, image.Id), SourceHash = new('A', 64), Width = 96, Height = 128,
            Strokes = [RegionalImageTests.Rect(true, 0, 0, .5, 1)]
        }));
        Assert.Contains("Protect active", _dialogs.Find(".reference-summary").TextContent);
        await _dialogs.FindAll(".reference-editor-footer button").Single(b => b.TextContent.Trim() == "Apply changes").ClickAsync(new());
        Assert.Contains("Protect active", page.Find(".compact-image-input").TextContent);
        Assert.DoesNotContain("Clear the regional", page.Markup);
        var context = page.FindComponent<lumibelle.Components.Assets.PromptEnhancementPanel>().Instance.Context;
        Assert.Equal(new QwenImage21Options(2048, 25), context.QwenImage21);
        Assert.NotNull(Assert.Single(context.References).Region);
        await page.Find("[aria-label='Clear selection']").ClickAsync(new());
        await page.Find(".media-select").ClickAsync(new());
        Assert.Contains("Protect active", page.Find(".compact-image-input").TextContent);
        Assert.Equal("2048", page.Find("#image-resolution").GetAttribute("value"));
        Assert.Equal("Change the background", page.Find("#image-prompt").GetAttribute("value"));
    }

    [Theory]
    [InlineData(ImageWorkflow.Krea2)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv)]
    [InlineData(ImageWorkflow.QwenImage21)]
    public async Task ResolutionIsCapturedForEditsAndRememberedWhenReturningToTheDraft(ImageWorkflow workflow)
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "portrait.png", ContentType = "image/png", Width = 96, Height = 128 };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Images = [image] }] };
        var page = Page();
        page.WaitForElement("#asset-image-workflow").Change(workflow.ToString());
        await page.Find(".media-select").ClickAsync(new());
        page.Find("#image-resolution").Change("2048");
        page.Find("#image-prompt").Input("A blue background");
        await page.Find("[aria-label='Clear selection']").ClickAsync(new());
        Assert.Equal("1024", page.Find("#image-resolution").GetAttribute("value"));
        await page.Find(".media-select").ClickAsync(new());
        Assert.Equal("2048", page.Find("#image-resolution").GetAttribute("value"));
        Assert.Contains("1760 × 2368", page.Find(".image-output-size").TextContent);
        Assert.NotNull(page.Find("#image-resolution").Closest(".workspace-pane-footer"));
        Assert.Empty(page.FindAll(".generator-config"));
        _editor.WaitUntilCancelled = true;
        await page.ClickCurrent(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Generate edited images"));
        await _editor.Called();
        Assert.Equal(2048, workflow == ImageWorkflow.QwenImage21 ? _editor.LastRequest!.QwenImage21!.Resolution : _editor.LastRequest!.Resolution);
    }

    [Theory]
    [InlineData(ImageWorkflow.Krea2)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv)]
    [InlineData(ImageWorkflow.CodexImages)]
    [InlineData(ImageWorkflow.QwenImage21)]
    public async Task EveryWorkflowOffersSourceAspectAndRemembersAnExplicitEditAspect(ImageWorkflow workflow)
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "portrait.png", ContentType = "image/png", Width = 96, Height = 128 };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Images = [image] }] };
        var page = Page();
        page.WaitForElement("#asset-image-workflow").Change(workflow.ToString());
        Assert.True(page.Find("#aspect option").HasAttribute("disabled"));
        Assert.Equal("1:1", page.Find("#aspect").GetAttribute("value"));
        await page.Find(".media-select").ClickAsync(new());
        Assert.False(page.Find("#aspect option").HasAttribute("disabled"));
        Assert.Equal(ImageAspectPolicy.FromImage1, page.Find("#aspect").GetAttribute("value"));
        Assert.False(page.Find("#aspect").HasAttribute("disabled"));
        page.Find("#aspect").Change("16:9");
        if (workflow == ImageWorkflow.QwenImage21)
            Assert.Contains("1376 × 768", page.Find(".image-output-size").TextContent);
        await page.Find("[aria-label='Clear selection']").ClickAsync(new());
        Assert.Equal("1:1", page.Find("#aspect").GetAttribute("value"));
        await page.Find(".media-select").ClickAsync(new());
        Assert.Equal("16:9", page.Find("#aspect").GetAttribute("value"));
        page.Find("#aspect").Change(ImageAspectPolicy.FromImage1);
        if (workflow == ImageWorkflow.QwenImage21)
            Assert.Contains("896 × 1184", page.Find(".image-output-size").TextContent);
    }
}
