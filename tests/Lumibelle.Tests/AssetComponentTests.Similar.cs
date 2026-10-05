using Bunit;
using lumibelle.Models;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task CreateSimilarIsDisabledWithoutARecipe()
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "import.png", ContentType = "image/png", Width = 64, Height = 64 };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Images = [image] }] };
        var page = Page(); page.WaitForElement(".media-select");
        await page.Find(".media-select").ClickAsync(new());
        Assert.True(page.Find(".create-similar").HasAttribute("disabled"));
        Assert.Contains("No saved creation recipe", page.Find(".create-similar").GetAttribute("title"));
    }

    [Fact]
    public async Task CreateSimilarIsOfferedFromTheImageMenuOnlyWithARecipe()
    {
        var recipe = new AssetGenerationMetadata { Workflow = ImageWorkflow.QwenImage21, Prompt = "Narrow alley at dusk", Seed = 77, AspectRatio = "16:9" };
        var generated = new AssetImage { Id = Guid.NewGuid(), FileName = "generated.png", ContentType = "image/png", Width = 64, Height = 36, Generation = recipe };
        var imported = new AssetImage { Id = Guid.NewGuid(), FileName = "import.png", ContentType = "image/png", Width = 64, Height = 64 };
        _assets.Library = _assets.Library with { Assets = [Asset("Alley") with { Images = [generated, imported] }] };
        var page = Page(); page.WaitForElement(".media-select");
        var importedCard = page.FindAll(".reference-card").Single(c => c.InnerHtml.Contains($"/images/{imported.Id:D}"));
        await page.InvokeAsync(() => importedCard.QuerySelector(".reference-image-menu button")!.ClickAsync(new()));
        _popovers.WaitForElement("[role='menuitem']");
        Assert.DoesNotContain(_popovers.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Create similar");
        await page.InvokeAsync(() => importedCard.QuerySelector(".reference-image-menu button")!.ClickAsync(new()));

        await ImageActionAsync(page, "Create similar", generated.Id);
        page.WaitForAssertion(() => Assert.Contains("Create image", page.Find("#assets-tool-heading").TextContent));
        Assert.Equal(recipe.Prompt, page.Find("#image-prompt").GetAttribute("value"));
        Assert.Equal("77", page.Find("#fixed-seed").GetAttribute("value"));
        Assert.Equal(0, _generator.Calls);
    }

    [Fact]
    public async Task CreateSimilarFromImageDetailsAsksAboutUnsavedDetailsThenLoadsTheRecipe()
    {
        var recipe = new AssetGenerationMetadata { Workflow = ImageWorkflow.QwenImage21, Prompt = "Crates by the wall", Seed = 12, AspectRatio = "16:9" };
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "generated.png", ContentType = "image/png", Width = 64, Height = 36, Generation = recipe, Name = "Alley" };
        _assets.Library = _assets.Library with { Assets = [Asset("Alley") with { Images = [image] }] };
        var page = Page(); page.WaitForElement(".reference-image");
        page.Find(".media-preview[aria-label^=\"Preview image\"]").Click();
        _dialogs.WaitForElement(".review-metadata-editor input");
        _dialogs.Find(".review-metadata-editor input").Input("Noxian alley");
        await ClickReview("Create similar");
        _dialogs.WaitForElement(".review-dirty-prompt");
        await ClickReview("Save and continue");
        page.WaitForAssertion(() => Assert.Contains("Create image", page.Find("#assets-tool-heading").TextContent));
        Assert.Empty(_dialogs.FindAll(".review-metadata-editor"));
        Assert.Equal("Noxian alley", _assets.Library.Assets[0].Images[0].Name);
        Assert.Equal(recipe.Prompt, page.Find("#image-prompt").GetAttribute("value"));
    }

    [Fact]
    public async Task CreateSimilarLoadsCapturedImageFieldsAndKeepsEditDraftSeparate()
    {
        var recipe = new AssetGenerationMetadata { Workflow = ImageWorkflow.QwenImage21, Prompt = "Original camera and lighting", Seed = 4711,
            AspectRatio = "16:9", QwenImage21 = new(new(1440, 30), 1920, 1088, "auto", "default") };
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "generated.png", ContentType = "image/png", Width = 1920, Height = 1088,
            Generation = recipe, Tags = ["portrait"] };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira", "Keep asset notes") with { Images = [image] }] };
        var page = Page(); page.WaitForElement(".media-select");
        await page.Find(".media-select").ClickAsync(new()); page.Find("#image-prompt").Input("Separate edit instruction");
        await page.Find(".create-similar").ClickAsync(new());
        Assert.Contains("Create image", page.Find("#assets-tool-heading").TextContent);
        Assert.Empty(page.FindAll(".media-select[aria-pressed='true']"));
        Assert.Equal(recipe.Prompt, page.Find("#image-prompt").GetAttribute("value"));
        Assert.Equal("4711", page.Find("#fixed-seed").GetAttribute("value"));
        Assert.Equal("16:9", page.Find("#aspect").GetAttribute("value"));
        Assert.Equal("portrait", page.Find("#image-tags").GetAttribute("value"));
        Assert.Equal("1440", page.Find("#image-resolution").GetAttribute("value"));
        Assert.Equal("30", page.Find("#qwen-image-steps").GetAttribute("value"));
        await page.Find(".media-select").ClickAsync(new());
        Assert.Equal("Separate edit instruction", page.Find("#image-prompt").GetAttribute("value"));
        await page.Find("[aria-label='Clear selection']").ClickAsync(new());
        Assert.Equal(recipe.Prompt, page.Find("#image-prompt").GetAttribute("value"));
        Assert.Same(recipe, _assets.Library.Assets[0].Images[0].Generation);
        Assert.Equal("Keep asset notes", _assets.Library.Assets[0].Description);
        Assert.Equal(0, _generator.Calls); Assert.Equal(0, _editor.Calls);
    }

    [Fact]
    public async Task CreateSimilarEditedImageRetainsOriginalInputsAndCropsInCreate()
    {
        var source = new AssetImage { Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 768, Height = 768 };
        var sourceOwner = Asset("Original owner") with { Images = [source] };
        var crop = new ImageCropRegion { X = .1, Y = .1, Width = .8, Height = .8 };
        var image = source with { Id = Guid.NewGuid(), Generation = new() { Workflow = ImageWorkflow.Flux2Klein9bKv, Prompt = "Blue coat", Seed = 42,
            Edit = new() { SourceAssetId = sourceOwner.Id, SourceImageId = source.Id, SourceCrop = crop } } };
        var destination = Asset("Destination") with { Images = [image] };
        _assets.Library = _assets.Library with { Assets = [destination, sourceOwner] };
        var page = Page(); page.WaitForElement(".media-select");
        await page.Find(".media-select").ClickAsync(new());
        await page.Find(".create-similar").ClickAsync(new());
        Assert.Contains("Create image", page.Find("#assets-tool-heading").TextContent);
        Assert.Equal("Blue coat", page.Find("#image-prompt").GetAttribute("value"));
        Assert.Contains("Original owner", page.Find(".compact-image-input").TextContent);
        Assert.Equal(0, _editor.Calls);
        _editor.WaitUntilCancelled = true;
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Generate images").ClickAsync(new());
        page.WaitForAssertion(() => Assert.NotNull(_editor.LastRequest));
        Assert.Equal(sourceOwner.Id, _editor.LastRequest!.SourceAssetId);
        Assert.Equal(source.Id, _editor.LastRequest.SourceImageId);
        Assert.Equal(crop, _editor.LastRequest.SourceCrop);
        Assert.Equal(new[] { new AssetImageReference(sourceOwner.Id, source.Id) }, _editor.LastReferences);
    }

    [Fact]
    public async Task CreateSimilarCanRestoreATrashedSourceFromManageReferences()
    {
        var source = new AssetImage { Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 768, Height = 768 };
        var sourceOwner = Asset("Original owner");
        var image = source with { Id = Guid.NewGuid(), Generation = new() { Workflow = ImageWorkflow.Flux2Klein9bKv, Prompt = "Blue coat", Seed = 42,
            Edit = new() { SourceAssetId = sourceOwner.Id, SourceImageId = source.Id } } };
        var trash = new TrashedImage { Image = source, Asset = sourceOwner, DeletedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) };
        _assets.Library = _assets.Library with { Assets = [Asset("Destination") with { Images = [image] }, sourceOwner], Trash = [trash] };
        var page = Page(); page.WaitForElement(".media-select");
        await page.Find(".media-select").ClickAsync(new());
        await page.Find(".create-similar").ClickAsync(new());
        await page.Find(".manage-image-inputs").ClickAsync(new());
        Assert.Contains("In Trash · 30 days remaining", _dialogs.Find(".reference-trash-restore").TextContent);
        await _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Restore image").ClickAsync(new());
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".reference-trash-restore")));
        Assert.Empty(_assets.Library.Trash);
        Assert.Contains(_assets.Library.Assets.Single(a => a.Id == sourceOwner.Id).Images, i => i.Id == source.Id);
        await _dialogs.FindAll(".reference-editor-footer button").Single(b => b.TextContent.Trim() == "Apply changes").ClickAsync(new());
        _editor.WaitUntilCancelled = true;
        await page.FindAll("button").Single(b => b.TextContent.Trim() == "Generate images").ClickAsync(new());
        page.WaitForAssertion(() => Assert.NotNull(_editor.LastRequest));
        Assert.Equal(source.Id, _editor.LastRequest!.SourceImageId);
    }
}
