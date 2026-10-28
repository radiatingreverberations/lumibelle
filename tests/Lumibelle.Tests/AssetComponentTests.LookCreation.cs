using Bunit;
using lumibelle.Models;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task GroupedLooksCombineNotesActionsAndImagesAndRespectTheGalleryFilters()
    {
        var first = new CharacterLook { Name = "Raincoat", Description = "A navy raincoat." };
        var empty = new CharacterLook { Name = "Evening", Description = "A green jacket." };
        var portrait = new AssetImage { Id = Guid.NewGuid(), FileName = "portrait.png", Name = "Portrait", ContentType = "image/png", Width = 8, Height = 8, IsReference = true };
        var image = portrait with { Id = Guid.NewGuid(), Name = "Coat reference", LookId = first.Id, IsReference = false };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Looks = [first, empty], Images = [portrait, image] }] };
        var page = Page(); page.WaitForElement(".look-card");
        Assert.Empty(page.FindAll(".character-look-overview"));
        Assert.Equal(2, page.FindAll(".look-card").Count);
        Assert.Contains("General", page.Find(".asset-gallery-group > summary").TextContent);
        var group = page.Find($".asset-gallery-group[data-look-id='{first.Id}']");
        Assert.Contains(first.Description, group.TextContent);
        Assert.Single(group.QuerySelectorAll($"[data-media-id='{image.Id}']"));
        Assert.Single(page.FindAll($"[aria-label='Create images for {first.Name}']"));
        Assert.Single(page.FindAll($"[aria-label='Manage {first.Name} look']"));
        Assert.Contains("No images for this look yet", page.Find($"[data-look-id='{empty.Id}']").TextContent);
        await page.InvokeAsync(() => page.Find("[aria-label='Gallery look']").Change(empty.Id.ToString()));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".look-card")));
        Assert.Empty(page.FindAll(".asset-media-card"));
        Assert.Empty(page.FindAll(".reference-empty"));
        await page.InvokeAsync(() => page.Find("[aria-label='Search references']").Input("No matching reference"));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".look-card")));
        Assert.Contains("No references match", page.Find(".reference-empty").TextContent);
        await page.ClickCurrent(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Clear filters"));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".look-card").Count));
        await page.ClickCurrent(() => page.Find(".asset-group-toggle"));
        page.WaitForElement(".character-look-overview");
        Assert.Equal(2, page.FindAll(".look-card").Count);
        Assert.Equal(2, page.FindAll(".asset-media-card").Count);
        Assert.Single(page.FindAll($"[aria-label='Create images for {first.Name}']"));
    }

    [Fact]
    public async Task LookCardsExposeEmptyLooksAndPrepareAnEditWithTheApprovedIdentity()
    {
        var look = new CharacterLook { Name = "Raincoat", Description = "A navy raincoat with silver buttons.", PreservationGuidance = "Keep the silver buttons." };
        var archived = new CharacterLook { Name = "Retired outfit", Archived = true };
        var portrait = new AssetImage { Id = Guid.NewGuid(), FileName = "portrait.png", ContentType = "image/png", Width = 10, Height = 12, IsReference = true };
        var unapproved = portrait with { Id = Guid.NewGuid(), IsReference = false, CreatedUtc = DateTimeOffset.UtcNow };
        var owner = Asset("Mira", "Auburn hair and grey eyes.") with { Looks = [look, archived], Images = [unapproved, portrait] };
        _assets.Library = _assets.Library with { Assets = [owner] };
        var page = Page(); page.WaitForElement(".look-card");
        Assert.Single(page.FindAll(".look-card"));
        Assert.Contains(look.Description, page.Find(".look-card").TextContent);
        Assert.Contains("0 images", page.Find(".look-card").TextContent);
        await page.InvokeAsync(() => { page.Find("#image-prompt").Input("Unsubmitted general reference direction."); page.Find("#aspect").Change("9:16"); });
        await page.ClickCurrent(() => page.Find($"[aria-label='Create images for {look.Name}']"));
        page.WaitForAssertion(() =>
        {
            Assert.Contains(look.Description, page.Find("[aria-label='Selected look notes']").TextContent);
            Assert.Contains(look.Description, page.Find("#image-prompt").GetAttribute("value"));
            Assert.Contains("Auburn hair", page.Find("#image-prompt").GetAttribute("value"));
            Assert.Contains("source outfit is not a requirement", page.Find("#image-prompt").GetAttribute("value"));
            Assert.Contains(portrait.Id.ToString()[..6], page.Find(".compact-image-input").TextContent);
            Assert.Equal("9:16", page.Find("#aspect").GetAttribute("value"));
        });
        Assert.Equal(0, _editor.Calls);
        Assert.Equal(0, _generator.Calls);
        await page.ClickCurrent(() => page.FindAll("button").Single(button => button.TextContent.Trim() == "Generate images"));
        await _editor.Called();
        page.WaitForAssertion(() => Assert.Equal("Unsubmitted general reference direction.", page.Find("#image-prompt").GetAttribute("value")));
        Assert.Equal(portrait.Id, _editor.LastRequest!.SourceImageId);
        Assert.Equal(look.Id, _editor.LastRequest.Look!.LookId);
        await _assets.Until(() => Assert.Contains(_assets.Library.Assets[0].Images, image => image.LookId == look.Id));
        Assert.Null(_assets.Library.Assets[0].Images.Single(image => image.Id == portrait.Id).LookId);
        Assert.True(_assets.Library.Assets[0].Images.Single(image => image.Id == portrait.Id).IsReference);
        Assert.False(_assets.Library.Assets[0].Images.Single(image => image.LookId == look.Id).IsReference);
    }

    [Fact]
    public async Task LookCreationPreservesEachAuthoredPromptAndUsesThePreferredApprovedIdentity()
    {
        var first = new CharacterLook { Name = "Raincoat", Description = "Navy coat." };
        var second = new CharacterLook { Name = "Evening", Description = "A green evening jacket." };
        var portrait = new AssetImage { Id = Guid.NewGuid(), FileName = "portrait.png", Width = 4, Height = 6, ContentType = "image/png", IsReference = true };
        var preferred = portrait with { Id = Guid.NewGuid(), LookId = second.Id };
        var owner = Asset("Mira") with { Looks = [first, second], Images = [portrait, preferred],
            PreferredIdentityReferences = [new(preferred.Id, second.Id, "Preferred portrait")] };
        _assets.Library = _assets.Library with { Assets = [owner] };
        var page = Page(); page.WaitForElement(".look-card");
        await page.InvokeAsync(() => page.Find("#image-prompt").Input("My general draft"));
        await Choose(first);
        Assert.Contains(preferred.Id.ToString()[..6], page.Find(".compact-image-input").TextContent);
        await page.InvokeAsync(() => page.Find("#image-prompt").Input("My edited raincoat prompt"));
        await Choose(second);
        await page.InvokeAsync(() => page.Find("#image-prompt").Input("My edited evening prompt"));
        await Choose(first);
        Assert.Equal("My edited raincoat prompt", page.Find("#image-prompt").GetAttribute("value"));
        await Choose(first);
        Assert.Equal("My edited raincoat prompt", page.Find("#image-prompt").GetAttribute("value"));
        await Choose(second);
        Assert.Equal("My edited evening prompt", page.Find("#image-prompt").GetAttribute("value"));
        Assert.Equal(0, _editor.Calls);
        Task Choose(CharacterLook look) => page.ClickCurrent(() => page.Find($"[aria-label='Create images for {look.Name}']"));
    }

    [Fact]
    public async Task LookWithoutApprovedIdentityPreparesNotesAndKeepsManagementAccessible()
    {
        var look = new CharacterLook { Name = "Raincoat", Description = "A navy coat." };
        _assets.Library = _assets.Library with { Assets = [Asset("Mira") with { Looks = [look] }] };
        var page = Page(); page.WaitForElement(".look-card");
        await page.ClickCurrent(() => page.Find("[aria-label='Create images for Raincoat']"));
        page.WaitForAssertion(() => Assert.Contains("starts from character notes", page.Find("[aria-label='Selected look notes']").TextContent));
        Assert.Empty(page.FindAll(".compact-image-input"));
        Assert.Contains(look.Description, page.Find("#image-prompt").GetAttribute("value"));
        await page.ClickCurrent(() => page.Find("[aria-label='Manage Raincoat look']"));
        _dialogs.WaitForElement("#character-look-filter");
        Assert.Equal(look.Id.ToString(), _dialogs.Find("#character-look-filter").GetAttribute("value"));
        Assert.Equal(0, _generator.Calls);
    }
}
