using Bunit;
using lumibelle.Models;
using lumibelle.Components.Pages;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Theory]
    [InlineData(ImageWorkflow.Krea2)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv)]
    public async Task ReferenceCropAndFidelitySurviveSaveTrashRestoreAndReopening(ImageWorkflow workflow)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var (project, store) = CreateStore();
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [Asset("Scene"), Asset("Subject")] }, 0, ct);
        foreach (var asset in library.Assets.ToArray())
        {
            using var bytes = new MemoryStream(Png(20, 30));
            library = await store.AddImageAsync(project.Id, asset.Id, bytes, new("source.png", [], AssetImageOrigin.Imported), library.Revision, ct);
        }
        var baseRef = new AssetImageReference(library.Assets[0].Id, library.Assets[0].Images[0].Id);
        var otherRef = new AssetImageReference(library.Assets[1].Id, library.Assets[1].Images[0].Id);
        var crop = new ImageCropRegion { X = .2, Width = .6, Height = .8 };
        var metadata = new AssetGenerationMetadata { Workflow = workflow, Edit = new()
        {
            SourceAssetId = baseRef.AssetId, SourceImageId = baseRef.ImageId, References = [baseRef, otherRef],
            ReferenceCrops = [new(otherRef, crop)], SourceCrop = new() { Width = .5 },
            BaseReferenceBoost = workflow == ImageWorkflow.Krea2 ? 1.75f : null,
            ReferenceBoost = workflow == ImageWorkflow.Krea2 ? 5.5f : 0,
            GroundingPixels = workflow == ImageWorkflow.Krea2 ? 768 : 0,
            Lora = workflow == ImageWorkflow.Krea2 ? "identity.safetensors" : "", LoraStrength = workflow == ImageWorkflow.Krea2 ? 1 : 0,
            FitMode = workflow == ImageWorkflow.Krea2 ? "fit" : "reference-latent"
        } };
        using var result = new MemoryStream(Png(30, 20));
        library = await store.AddImageAsync(project.Id, baseRef.AssetId, result, new("take.png", [], AssetImageOrigin.Edited, metadata), library.Revision, ct);
        var take = library.Assets[0].Images[^1];
        library = await store.DeleteAssetAsync(project.Id, otherRef.AssetId, library.Revision, ct);
        var deleted = Assert.Single(library.Trash);
        library = await store.RestoreImagesAsync(project.Id, [deleted.Id], library.Revision, ct);
        var receipt = await store.DeleteImagesAsync(project.Id, baseRef.AssetId, [take.Id], library.Revision, ct);
        library = await store.RestoreImagesAsync(project.Id, receipt.TrashIds, receipt.Library.Revision, ct);
        var reopened = await CreateFreshStore().LoadAsync(project.Id, ct);
        var saved = reopened.Assets.Single(a => a.Id == baseRef.AssetId).Images.Single(i => i.Id == take.Id).Generation!.Edit!;
        Assert.Equal(metadata.Edit.BaseReferenceBoost, saved.BaseReferenceBoost);
        Assert.Equal(new[] { baseRef, otherRef }, saved.References);
        Assert.Equal(crop, Assert.Single(saved.ReferenceCrops).Crop);
        Assert.Equal(otherRef.ImageId, Assert.Single(reopened.Assets.Single(a => a.Id == otherRef.AssetId).Images).Id);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("duplicate")]
    [InlineData("base")]
    [InlineData("strength")]
    [InlineData("too-many")]
    public async Task InvalidKreaReferenceMetadataCannotPublish(string invalid)
    {
        var ct = Xunit.TestContext.Current.CancellationToken; var (project, store) = CreateStore();
        var library = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [Asset("Scene")] }, 0, ct);
        var first = new AssetImageReference(library.Assets[0].Id, Guid.NewGuid());
        var second = new AssetImageReference(Guid.NewGuid(), Guid.NewGuid());
        var item = new AssetReferenceCrop(invalid == "foreign" ? new(Guid.NewGuid(), Guid.NewGuid()) : invalid == "base" ? first : second, new() { Width = .5 });
        var metadata = new AssetGenerationMetadata { Edit = new()
        {
            SourceAssetId = first.AssetId, SourceImageId = first.ImageId,
            References = invalid == "too-many" ? [first, second, new(Guid.NewGuid(), Guid.NewGuid())] : [first, second],
            Lora = "identity.safetensors", BaseReferenceBoost = invalid == "strength" ? 11 : 1,
            ReferenceCrops = invalid == "duplicate" ? [item, item] : [item]
        } };
        using var bytes = new MemoryStream(Png(10, 10));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.AddImageAsync(project.Id, first.AssetId, bytes,
            new("take.png", [], AssetImageOrigin.Edited, metadata), library.Revision, ct));
        var reopened = await store.LoadAsync(project.Id, ct);
        Assert.Equal(library.Revision, reopened.Revision); Assert.Empty(reopened.Assets[0].Images);
    }
}

public sealed partial class AssetComponentTests
{
    private async Task OpenInputCrop(IRenderedComponent<AssetsStudio> page, int index)
    {
        await page.InvokeAsync(() => page.Find(".manage-image-inputs").ClickAsync(new()));
        _dialogs.WaitForElement(".reference-crop-button");
        await _dialogs.InvokeAsync(() => _dialogs.FindAll(".reference-crop-button")[index].ClickAsync(new()));
    }
    private async Task FinishInputs(string action)
    {
        if (_dialogs.FindAll(".image-input-crop-dialog").Count > 0)
        {
            var cropAction = action == "Apply changes" ? "Apply crop" : "Cancel";
            await _dialogs.InvokeAsync(() => _dialogs.Find(".image-input-crop-dialog").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == cropAction).ClickAsync(new()));
            _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".image-input-crop-dialog")));
        }
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == action).ClickAsync(new()));
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".image-input-manager-dialog")));
    }
    private async Task RemoveInput(IRenderedComponent<AssetsStudio> page, int index)
    {
        await page.InvokeAsync(() => page.Find(".manage-image-inputs").ClickAsync(new()));
        _dialogs.WaitForElement(".reference-action-buttons");
        await _dialogs.InvokeAsync(() => _dialogs.Find($"[aria-label='Remove Image {index + 1}']").ClickAsync(new()));
        await FinishInputs("Apply changes");
    }

    [Fact]
    public async Task CancellingOnlyTheCropDialogLeavesTheReferenceDraftUnchanged()
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 400, Height = 600 };
        _assets.Library = _assets.Library with { Assets = [Asset("Character") with { Images = [image] }] };
        var page = Page();
        await page.WaitForElement(".media-select").ClickAsync(new());
        await OpenInputCrop(page, 0);
        _dialogs.Find("[aria-label='Crop zoom']").Change("2");
        await _dialogs.Find(".image-input-crop-dialog").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync(new());
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".image-input-crop-dialog")));
        Assert.Contains("Full image", _dialogs.Find(".reference-summary").TextContent);
        await FinishInputs("Apply changes");
        Assert.Contains("Full image", page.Find(".compact-image-input").TextContent);
    }

    [Fact]
    public async Task IndependentCropsCancelAndResetWithoutChangingOtherImagesOrOutputAspect()
    {
        var first = new AssetImage { Id = Guid.NewGuid(), FileName = "base.png", ContentType = "image/png", Width = 400, Height = 600 };
        var second = first with { Id = Guid.NewGuid(), Width = 800, Height = 400 };
        var asset = Asset("Scene") with { Images = [first] }; var donor = Asset("Subject") with { Images = [second] };
        _assets.Library = _assets.Library with { Assets = [asset, donor] };
        var page = Page(); page.WaitForElement(".reference-actions button");
        await page.InvokeAsync(() => page.Find(".media-select").ClickAsync(new()));
        page.Find("#aspect").Change("2:3");
        await AddReferenceImage(page, new(donor.Id, second.Id));
        await OpenInputCrop(page, 0); _dialogs.Find("[aria-label='Crop zoom']").Change("2");
        _dialogs.Find(".image-copy-crop select").Change("1:1");
        await FinishInputs("Apply changes");
        await OpenInputCrop(page, 1); _dialogs.Find("[aria-label='Crop zoom']").Change("4");
        await FinishInputs("Cancel");
        Assert.Contains("Full image", page.FindAll(".compact-image-input")[1].TextContent);
        await OpenInputCrop(page, 1); _dialogs.Find("[aria-label='Crop zoom']").Change("2");
        await FinishInputs("Apply changes");
        Assert.Contains("Cropped", page.FindAll(".compact-image-input")[1].TextContent);
        page.Find("#asset-image-workflow").Change("Flux2Klein9bKv");
        Assert.Contains("Cropped", page.FindAll(".compact-image-input")[1].TextContent);
        page.Find("#asset-image-workflow").Change("Krea2");
        await OpenInputCrop(page, 1);
        _dialogs.FindAll("button").Single(b => b.TextContent == "Full image").Click(); await FinishInputs("Apply changes");
        Assert.Contains("Full image", page.FindAll(".compact-image-input")[1].TextContent);
        Assert.Contains("Cropped", page.Find(".compact-image-input").TextContent);
        page.Find("#image-prompt").Input("Place the person in the scene.");
        page.Find("#base-reference-boost").Change("1.75"); page.Find("#reference-boost").Change("5.5");
        await Generate(page, awaitHandler: false); await _editor.Called();
        Assert.Equal(.5, _editor.LastRequest!.SourceCrop!.Width); Assert.Empty(_editor.LastRequest.ReferenceCrops);
        Assert.Equal(1.75f, _editor.LastRequest.BaseReferenceBoost); Assert.Equal(5.5f, _editor.LastRequest.ReferenceBoost);
        Assert.Equal("2:3", _editor.LastRequest.AspectRatio);
    }

    [Fact]
    public async Task TooManyOrUnavailableReferencesBlockKreaWithoutDroppingInputs()
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "base.png", ContentType = "image/png", Width = 100, Height = 100 };
        var second = image with { Id = Guid.NewGuid() }; var third = image with { Id = Guid.NewGuid() };
        var asset = Asset("Juniper") with { Images = [image, second, third] };
        _assets.Library = _assets.Library with { Assets = [asset] };
        var page = Page(); page.WaitForElement("#asset-image-workflow").Change("Flux2Klein9bKv");
        await page.InvokeAsync(() => page.Find($"[data-media-id='{image.Id}'] .media-select").ClickAsync(new()));
        foreach (var other in new[] { second, third })
        { await AddReferenceImage(page, new(asset.Id, other.Id)); }
        page.Find("#image-prompt").Input("Edit this"); page.Find("#asset-image-workflow").Change("Krea2");
        Assert.Equal(2, page.FindAll(".compact-image-input").Skip(1).Count()); Assert.True(RunButton(page).HasAttribute("disabled"));
        await RemoveInput(page, 2); Assert.False(RunButton(page).HasAttribute("disabled"));
        await ImageActionAsync(page, "Move to Trash", second.Id);
        Assert.Single(page.FindAll(".compact-image-input").Skip(1)); Assert.True(RunButton(page).HasAttribute("disabled"));
        Assert.Contains("unavailable", page.Find(".image-reference-fields").TextContent);
    }

    [Fact]
    public async Task SingleImageOnlyInstallationExplainsItsLimitAndRetainsSelectedReference()
    {
        _editor.KreaMaximumReferences = 1;
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "base.png", ContentType = "image/png", Width = 100, Height = 100 };
        var other = image with { Id = Guid.NewGuid() };
        var asset = Asset("Juniper") with { Images = [image] }; var donor = Asset("Coat") with { Images = [other] };
        _assets.Library = _assets.Library with { Assets = [asset, donor] };
        var page = Page(); page.WaitForElement(".reference-actions button");
        await page.InvokeAsync(() => page.Find(".media-select").ClickAsync(new()));
        page.Find("#image-prompt").Input("Change the coat");
        Assert.False(page.Find(".manage-image-inputs").HasAttribute("disabled")); Assert.False(RunButton(page).HasAttribute("disabled"));
        Assert.Contains("Manage references", page.Find(".image-reference-fields").TextContent);
        page.Find("#asset-image-workflow").Change("Flux2Klein9bKv");
        await AddReferenceImage(page, new(donor.Id, other.Id));
        page.Find("#asset-image-workflow").Change("Krea2");
        Assert.Single(page.FindAll(".compact-image-input").Skip(1)); Assert.True(RunButton(page).HasAttribute("disabled"));
        await RemoveInput(page, 1); Assert.False(RunButton(page).HasAttribute("disabled"));
    }
}
