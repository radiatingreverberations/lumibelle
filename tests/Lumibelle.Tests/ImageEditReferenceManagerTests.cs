using Bunit;
using Bunit.TestDoubles;
using lumibelle.Components.Assets;
using lumibelle.Models;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ImageEditReferenceManagerTests : BunitContext
{
    private readonly ReferenceAsset _asset = new() { Id = Guid.NewGuid(), Name = "Character", Images = [Image("Front"), Image("Side"), Image("Back")] };
    private ImageEditReferenceManager.ImageEditInputs? _saved;
    private bool _cancelled;

    public ImageEditReferenceManagerTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        ComponentFactories.AddStub<ImageRegionEditor>();
    }

    private static AssetImage Image(string name) => new() { Id = Guid.NewGuid(), Name = name, FileName = "test.png", ContentType = "image/png", Width = 96, Height = 128 };
    private AssetImageReference Ref(int index) => new(_asset.Id, _asset.Images[index].Id);
    private RegionalImageSelection Region(int index = 0, RegionalEditMode mode = RegionalEditMode.Protect) => new()
    {
        Source = Ref(index), SourceHash = new('a', 64), Width = 96, Height = 128, Mode = mode,
        Strokes = mode == RegionalEditMode.Edit
            ? [RegionalImageTests.Rect(false, 0, 0, 1, 1), RegionalImageTests.Rect(true, 0, 0, .25, 1)]
            : [RegionalImageTests.Rect(true, 0, 0, .25, 1)]
    };

    private IRenderedComponent<ImageEditReferenceManager> Manager(IReadOnlyList<RegionalImageSelection>? regions = null) =>
        Render<ImageEditReferenceManager>(p => p
            .Add(c => c.Library, new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [_asset] }).Add(c => c.AssetId, _asset.Id)
            .Add(c => c.Inputs, [Ref(0), Ref(1)]).Add(c => c.Maximum, 10).Add(c => c.Regions, regions ?? [])
            .Add(c => c.Aspect, "16:9").Add(c => c.Resolution, 2048).Add(c => c.QwenImage21, new QwenImage21Options(2048, 25))
            .Add(c => c.Applied, result => _saved = result).Add(c => c.Cancelled, () => _cancelled = true));

    private static async Task Click(IRenderedComponent<ImageEditReferenceManager> manager, string text) =>
        await manager.FindAll("button").Single(b => b.TextContent.Trim() == text).ClickAsync(new());

    [Fact]
    public async Task NestedEditsAreDraftedTogetherAndOuterCancelDoesNotPublish()
    {
        var original = Region();
        var manager = Manager([original]);
        await manager.FindAll(".reference-protect-button")[0].ClickAsync(new());
        var editor = manager.FindComponent<Stub<ImageRegionEditor>>().Instance.Parameters;
        Assert.Equal("16:9", editor.Get(c => c.Aspect));
        Assert.Equal(new QwenImage21Options(2048, 25), editor.Get(c => c.QwenImage21));
        Assert.False(editor.Get(c => c.ReferenceOnly));
        Assert.False(editor.Get(c => c.EditArea));
        var crop = new ImageCropRegion { X = .25, Y = 0, Width = .75, Height = 1 };
        await manager.InvokeAsync(() => editor.Get(c => c.Applied).InvokeAsync(original with { Context = crop }));
        Assert.Null(_saved);
        Assert.Equal(1, original.Context.Width);
        Assert.Contains("Cropped", manager.Find(".reference-summary").TextContent);
        await Click(manager, "Cancel");
        Assert.True(_cancelled);
        Assert.Null(_saved);
    }

    [Fact]
    public async Task ApplyingPublishesMasksAndCropsAndNestedCancelKeepsThePreviousDraft()
    {
        var manager = Manager();
        await manager.FindAll(".reference-protect-button")[1].ClickAsync(new());
        var editor = manager.FindComponent<Stub<ImageRegionEditor>>().Instance.Parameters;
        Assert.True(editor.Get(c => c.ReferenceOnly));
        Assert.False(editor.Get(c => c.EditArea));
        var region = Region(1) with { Context = new() { X = 0, Y = 0, Width = .5, Height = 1 } };
        await manager.InvokeAsync(() => editor.Get(c => c.Applied).InvokeAsync(region));
        await manager.FindAll(".reference-protect-button")[1].ClickAsync(new());
        editor = manager.FindComponent<Stub<ImageRegionEditor>>().Instance.Parameters;
        Assert.Equal(region.Context, editor.Get(c => c.Crop));
        Assert.NotSame(region, editor.Get(c => c.Initial));
        await manager.InvokeAsync(() => editor.Get(c => c.Cancelled).InvokeAsync());
        await Click(manager, "Apply changes");
        Assert.Equal(region.Context, Assert.Single(_saved!.Crops).Crop);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(region.Strokes), System.Text.Json.JsonSerializer.Serialize(Assert.Single(_saved.Regions).Strokes));
        Assert.Equal(Ref(1), _saved.Regions[0].Source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovingAnImageDropsItsMaskEvenWhenAnotherIsAdded(bool addAnother)
    {
        var manager = Manager([Region(1)]);
        var row = manager.FindAll(".manual-reference-list > li")[1];
        await row.QuerySelectorAll("button").Single(b => b.GetAttribute("aria-label") == "Remove Image 2").ClickAsync(new());
        if (addAnother) await manager.Find($"[data-reference='{_asset.Id}/{_asset.Images[2].Id}']").ClickAsync(new());
        await Click(manager, "Apply changes");
        Assert.Empty(_saved!.Regions);
        Assert.DoesNotContain(Ref(1), _saved.Images);
    }

    [Fact]
    public async Task MovingAnEditAreaOffTheSourceRequiresExplicitlyClearingIt()
    {
        var manager = Manager([Region(mode: RegionalEditMode.Edit)]);
        await manager.Find("[aria-label='Move Image 1 down']").ClickAsync(new());
        Assert.Contains("An edit area belongs to Image 1", manager.Find("[role=alert]").TextContent);
        await Click(manager, "Apply changes");
        Assert.Null(_saved);
        await manager.Find("[aria-label='Clear edit area for Image 2']").ClickAsync(new());
        await Click(manager, "Apply changes");
        var remaining = Assert.Single(_saved!.Regions);
        Assert.Equal(RegionalEditMode.Protect, remaining.Mode);
        Assert.Equal(Ref(0), remaining.Source);
        Assert.True(Assert.Single(remaining.Strokes).Protect);
        Assert.Equal(Ref(1), _saved.Images[0]);
    }

    [Fact]
    public async Task OnlyTheSourceOffersAnEditAreaWhileEveryImageOffersProtection()
    {
        var manager = Manager([Region(mode: RegionalEditMode.Edit)]);
        var edit = Assert.Single(manager.FindAll(".regional-edit-button:not([disabled])"));
        var disabledEdit = Assert.Single(manager.FindAll(".regional-edit-button[disabled]"));
        Assert.Equal("Image 2: Edit area", disabledEdit.GetAttribute("aria-label"));
        Assert.Equal("Edit area is only available for Image 1", disabledEdit.GetAttribute("title"));
        Assert.Equal("Image 1: Edit area", edit.GetAttribute("aria-label"));
        Assert.Contains("is-active", edit.ClassList);
        Assert.Equal(2, manager.FindAll(".reference-protect-button").Count);
        Assert.Contains("is-active", manager.Find(".reference-protect-button").ClassList);
        await edit.ClickAsync(new());
        var editor = manager.FindComponent<Stub<ImageRegionEditor>>().Instance.Parameters;
        Assert.True(editor.Get(c => c.EditArea));
        Assert.False(editor.Get(c => c.ReferenceOnly));
        Assert.Equal(Ref(0), editor.Get(c => c.Source));
        await manager.InvokeAsync(() => editor.Get(c => c.Cancelled).InvokeAsync());
        await manager.FindAll(".reference-protect-button")[1].ClickAsync(new());
        editor = manager.FindComponent<Stub<ImageRegionEditor>>().Instance.Parameters;
        Assert.False(editor.Get(c => c.EditArea));
        Assert.True(editor.Get(c => c.ReferenceOnly));
        Assert.Equal(Ref(1), editor.Get(c => c.Source));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ATrashedInputCanBeRestoredWithoutLosingOtherEdits(bool fails)
    {
        var trashed = _asset.Images[1];
        var trash = new TrashedImage { Image = trashed, Asset = _asset with { Images = [] }, DeletedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) };
        var before = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [_asset with { Images = [_asset.Images[0], _asset.Images[2]] }], Trash = [trash] };
        var after = before with { Assets = [_asset], Trash = [] };
        var requested = new List<Guid>();
        IRenderedComponent<ImageEditReferenceManager> manager = null!;
        manager = Render<ImageEditReferenceManager>(p => p.Add(c => c.Library, before).Add(c => c.AssetId, _asset.Id)
            .Add(c => c.Inputs, [Ref(0), Ref(1)]).Add(c => c.Maximum, 10).Add(c => c.Applied, result => _saved = result)
            .Add(c => c.RestoreTrashedImage, id => {
                requested.Add(id);
                if (fails) throw new lumibelle.Services.Story.WorkspaceStoreException("Save your changes before restoring.");
                manager.Render(q => q.Add(c => c.Library, after)); return Task.CompletedTask; }));
        Assert.Contains("Restore or replace the unavailable reference.", manager.Find(".reference-issue").TextContent);
        Assert.Contains("In Trash · 30 days remaining", manager.Find(".reference-trash-restore").TextContent);
        Assert.Contains("Restore it from Trash", manager.Find("[role=alert]").TextContent);
        Assert.True(manager.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").HasAttribute("disabled"));
        await Click(manager, "Restore image");
        Assert.Equal([trash.Id], requested);
        if (fails)
        {
            Assert.Contains("Save your changes before restoring.", manager.FindAll("[role=alert]").Last().TextContent);
            Assert.Single(manager.FindAll(".reference-trash-restore"));
            return;
        }
        Assert.Empty(manager.FindAll(".reference-issue")); Assert.Empty(manager.FindAll(".reference-trash-restore"));
        await Click(manager, "Apply changes");
        Assert.Equal([Ref(0), Ref(1)], _saved!.Images);
    }

    [Fact]
    public void AnImageMovedAwayBeforeTrashingIsNotOfferedForThisReference()
    {
        var image = _asset.Images[0]; var other = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Other" };
        var moved = image with { PreviousAssetIds = [_asset.Id] };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [_asset with { Images = [] }, other],
            Trash = [new() { Image = moved, Asset = other, DeletedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) }] };
        Assert.Null(AssetImageLocations.RecoverableTrash(library, Ref(0), DateTimeOffset.UtcNow));
        Assert.NotNull(AssetImageLocations.RecoverableTrash(library, new(other.Id, image.Id), DateTimeOffset.UtcNow));
        Assert.Null(AssetImageLocations.RecoverableTrash(library with { Assets = [_asset with { Images = [] }, other with { Images = [image] }] }, new(other.Id, image.Id), DateTimeOffset.UtcNow));
    }
}
