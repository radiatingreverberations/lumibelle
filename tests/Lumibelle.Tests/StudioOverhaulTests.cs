using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Components.Pages;
using lumibelle.Models;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    private async Task AddReferenceImage(IRenderedComponent<AssetsStudio> page, AssetImageReference reference)
    {
        await page.InvokeAsync(() => page.Find(".manage-image-inputs").ClickAsync(new()));
        _dialogs.WaitForElement(".project-image-picker");
        await _dialogs.InvokeAsync(() => _dialogs.Find($"[data-reference='{reference.AssetId}/{reference.ImageId}']").ClickAsync(new()));
        await FinishInputs("Apply changes");
    }

    [Fact]
    public async Task ApplyingImageGuidanceUsesCurrentDetailsInsteadOfTheRenderThatOpenedAssistance()
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 400, Height = 600, Name = "Original" };
        var asset = Asset("Mira") with { Images = [image] };
        _assets.Library = _assets.Library with { Assets = [asset] };
        var page = Page(); page.WaitForElement(".reference-image");
        page.Find(".media-preview[aria-label^=\"Preview image\"]").Click();
        _dialogs.WaitForElement(".review-metadata-editor input");
        var guidance = _dialogs.FindComponents<GuidanceSuggestion>().Single(g => g.Instance.Context?.Target.Scope == GuidanceScope.Image);
        var capturedApply = guidance.Instance.Apply;
        _dialogs.Find(".review-metadata-editor input").Input("Name edited while assistance was open");
        await guidance.InvokeAsync(() => capturedApply.InvokeAsync("Keep the blue eyes."));
        await ClickReview("Save details");
        Assert.Equal("Name edited while assistance was open", _assets.Library.Assets[0].Images[0].Name);
        Assert.Equal("Keep the blue eyes.", _assets.Library.Assets[0].Images[0].PreservationGuidance);
    }
    [Fact]
    public async Task ImageDetailsStayLocalAndFailedApplyRetainsTheDraft()
    {
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "source.png", ContentType = "image/png", Width = 400, Height = 600, Name = "Original" };
        var asset = Asset("Mira") with { Images = [image] };
        _assets.Library = _assets.Library with { Assets = [asset] };
        var page = Page(); page.WaitForElement(".reference-image");
        page.Find(".media-preview[aria-label^=\"Preview image\"]").Click();
        _dialogs.WaitForElement(".review-metadata-editor input");
        _dialogs.Find(".review-metadata-editor input").Input("Revised name");
        Assert.Equal("Original", _assets.Library.Assets[0].Images[0].Name);
        await _dialogs.InvokeAsync(() => _dialogs.Find(".image-review-dialog [data-dialog-escape]").ClickAsync(new()));
        _dialogs.WaitForElement(".review-dirty-prompt");
        await ClickReview("Keep editing");
        Assert.Equal("Revised name", _dialogs.Find(".review-metadata-editor input").GetAttribute("value"));
        _assets.SaveError = new lumibelle.Services.Story.WorkspaceStoreException("Disk unavailable");
        await ClickReview("Save details");
        _dialogs.WaitForElement(".review-metadata-editor [role=alert]");
        Assert.Equal("Revised name", _dialogs.Find(".review-metadata-editor input").GetAttribute("value"));
        _assets.SaveError = null;
        await ClickReview("Save details");
        Assert.Equal("Revised name", _assets.Library.Assets[0].Images[0].Name);
        Assert.Equal("Revised name", _dialogs.Find(".review-metadata-editor input").GetAttribute("value"));
        _dialogs.Find(".review-metadata-editor input").Input("Unwanted");
        await ClickReview("Reset details");
        Assert.Equal("Revised name", _assets.Library.Assets[0].Images[0].Name);
        Assert.Equal("Revised name", _dialogs.Find(".review-metadata-editor input").GetAttribute("value"));
        await _dialogs.InvokeAsync(() => _dialogs.Find(".image-review-dialog [data-dialog-escape]").ClickAsync(new()));
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".image-review-dialog")));
    }
}

public sealed class ImageMetadataEditTests
{
    private static readonly AssetImage Image = new() { Id = Guid.NewGuid(), FileName = "original.png", ContentType = "image/png", Width = 10, Height = 20, Name = "Original", Tags = ["face"], PreservationGuidance = "Keep eyes", Origin = AssetImageOrigin.VideoFrame };
    private static readonly ReferenceAsset Asset = new() { Id = Guid.NewGuid(), Name = "Mira", Category = AssetCategory.Character, Images = [Image] };
    private static ImageMetadataEdit Edit(ImageMetadataValues value) => new(Guid.NewGuid(), new(Asset.Id, Image.Id), ImageMetadataValues.From(Image), value);
    [Fact] public void ChangingNamePreservesUnrelatedFieldsAndImmutableProvenance()
    {
        var current = Image with { Tags = ["new tag"], IsReference = true, PreservationGuidance = "Updated elsewhere" };
        var result = Edit(ImageMetadataValues.From(Image) with { Name = "Updated" }).Apply(Asset with { Images = [current] });
        Assert.Equal(current with { Name = "Updated" }, result);
    }
    [Fact] public void SameFieldConflictIsExplicit()
    {
        var edit = Edit(ImageMetadataValues.From(Image) with { Name = "My edit" });
        Assert.Throws<InvalidOperationException>(() => edit.Apply(Asset with { Images = [Image with { Name = "Their edit" }] }));
    }
    [Fact] public void RetryOfPublishedValuesIsIdempotent()
    {
        var edit = Edit(ImageMetadataValues.From(Image) with { Name = "My edit" });
        Assert.Equal("My edit", edit.Apply(Asset with { Images = [Image with { Name = "My edit" }] }).Name);
    }
    [Fact] public void MissingOrMovedImageCannotBeRecreated()
    {
        var edit = Edit(ImageMetadataValues.From(Image) with { Name = "My edit" });
        Assert.Throws<InvalidOperationException>(() => edit.Apply(Asset with { Images = [] }));
        Assert.Throws<InvalidOperationException>(() => edit.Apply(Asset with { Id = Guid.NewGuid() }));
    }
    [Fact] public void LookMustBelongToAssetAndBeActive()
    {
        var edit = Edit(ImageMetadataValues.From(Image) with { LookId = Guid.NewGuid() });
        Assert.Throws<InvalidOperationException>(() => edit.Apply(Asset));
    }
}
