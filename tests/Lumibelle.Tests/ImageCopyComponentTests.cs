using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Models;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ImageCopyComponentTests : BunitContext
{
    public ImageCopyComponentTests() { Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose; }

    [Fact]
    public async Task FailedSaveRetainsCapturedInputsAndDoubleClickCannotCreateAnotherCopy()
    {
        var library = new AssetLibrary { ProjectId = Guid.NewGuid() };
        List<DerivedImageRequest> calls = [];
        var completion = new TaskCompletionSource<SavedAssetImage>();
        var cut = Render<ImageCopyPanel>(p => p.Add(c => c.ProjectId, library.ProjectId).Add(c => c.Library, library)
            .Add(c => c.TakeId, Guid.NewGuid()).Add(c => c.FrameIndex, 8).Add(c => c.InitialName, "Arrival")
            .Add(c => c.Save, r => { calls.Add(r); return completion.Task; }));
        var saving = cut.FindAll("button").Single(b => b.TextContent == "Save").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Single(calls));
        Assert.True(cut.Find("fieldset").HasAttribute("disabled"));
        await cut.FindAll("button").Single(b => b.TextContent == "Save and edit").ClickAsync(new());
        Assert.Single(calls);
        completion.SetException(new WorkspaceConflictException()); await saving;
        cut.WaitForElement("[role=alert]");
        var captured = calls[0];
        await cut.InvokeAsync(() => cut.Render(p => p.Add(c => c.FrameIndex, 18).Add(c => c.InitialName, "Another frame")));
        Assert.Contains("Frame 9 ·", cut.Markup);
        Assert.Contains("Retry keeps frame 9", cut.Markup);
        completion = new();
        var retry = cut.FindAll("button").Single(b => b.TextContent == "Retry save").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Equal(2, calls.Count)); Assert.Same(captured, calls[1]);
        completion.SetResult(new(library, captured.Destination.AssetId, captured.ImageId, true)); await retry;
    }

    [Fact]
    public async Task SeekingUpdatesSuggestedNamesAndSaveFrameWithoutReplacingUserEdits()
    {
        var library = new AssetLibrary { ProjectId = Guid.NewGuid() };
        DerivedImageRequest? saved = null;
        var cut = Render<ImageCopyPanel>(p => p.Add(c => c.ProjectId, library.ProjectId).Add(c => c.Library, library)
            .Add(c => c.TakeId, Guid.NewGuid()).Add(c => c.FrameIndex, 0).Add(c => c.InitialName, "Shot · frame 1")
            .Add(c => c.Save, r => { saved = r; return Task.FromResult(new SavedAssetImage(library, r.Destination.AssetId, r.ImageId, true)); }));
        await cut.InvokeAsync(() => cut.Render(p => p.Add(c => c.FrameIndex, 5).Add(c => c.InitialName, "Shot · frame 6")));
        Assert.All(cut.FindAll("input"), input => Assert.Equal("Shot · frame 6", input.GetAttribute("value")));
        await cut.FindAll("input")[0].InputAsync(new() { Value = "My asset" });
        await cut.FindAll("input")[1].InputAsync(new() { Value = "My image" });
        await cut.Find("textarea").InputAsync(new() { Value = "Keep my notes" });
        await cut.InvokeAsync(() => cut.Render(p => p.Add(c => c.FrameIndex, 9).Add(c => c.InitialName, "Shot · frame 10")));
        await cut.FindAll("button").Single(b => b.TextContent == "Save").ClickAsync(new());
        Assert.NotNull(saved);
        Assert.Equal(9, saved.FrameIndex);
        Assert.Equal("My asset", saved.Destination.NewAssetName);
        Assert.Equal("My image", saved.Name);
        Assert.Equal("Keep my notes", saved.Notes);
    }

    [Fact]
    public async Task FrameSavesShowTheCropEditorOnlyWithASourcePictureAndSendNoCropForTheFullFrame()
    {
        var library = new AssetLibrary { ProjectId = Guid.NewGuid() };
        DerivedImageRequest? saved = null;
        IRenderedComponent<ImageCopyPanel> Frame(Action<ComponentParameterCollectionBuilder<ImageCopyPanel>>? extra = null) => Render<ImageCopyPanel>(p =>
        {
            p.Add(c => c.ProjectId, library.ProjectId).Add(c => c.Library, library).Add(c => c.TakeId, Guid.NewGuid()).Add(c => c.FrameIndex, 3)
                .Add(c => c.InitialName, "Frame").Add(c => c.Save, r => { saved = r; return Task.FromResult(new SavedAssetImage(library, r.Destination.AssetId, r.ImageId, true)); });
            extra?.Invoke(p);
        });
        void Picture(ComponentParameterCollectionBuilder<ImageCopyPanel> p) => p.Add(c => c.SourceUrl, "/frame.png").Add(c => c.SourceWidth, 64).Add(c => c.SourceHeight, 32);

        Assert.Empty(Frame().FindAll(".image-copy-crop"));

        // The whole frame, as it opens, is saved without a crop.
        var whole = Frame(Picture);
        Assert.Single(whole.FindAll(".image-copy-crop"));
        await whole.FindAll("button").Single(b => b.TextContent == "Save").ClickAsync(new());
        Assert.Null(saved!.Crop);

        // Zooming the original shape to 2 keeps the centred half of each side.
        var zoomed = Frame(Picture);
        await zoomed.Find("[aria-label='Crop zoom']").ChangeAsync(new() { Value = "2" });
        await zoomed.FindAll("button").Single(b => b.TextContent == "Save").ClickAsync(new());
        Assert.Equal(new ImageCropRegion { X = .25, Y = .25, Width = .5, Height = .5 }, saved!.Crop);

        // A pick's crop is shown and saved as it is, without touching the editor.
        var pick = new ImageCropRegion { X = .1, Y = .2, Width = .3, Height = .4 };
        var picked = Frame(p => { Picture(p); p.Add(c => c.InitialCrop, pick); });
        Assert.Equal("0.1", picked.Find(".crop-selection").GetAttribute("data-x"));
        await picked.FindAll("button").Single(b => b.TextContent == "Save").ClickAsync(new());
        Assert.Equal(pick, saved!.Crop);
    }

    [Fact]
    public async Task EditFirstOffersOneFilledSaveAndEditActionAndOpensTheEditor()
    {
        var library = new AssetLibrary { ProjectId = Guid.NewGuid() };
        var reel = Guid.NewGuid();
        SavedAssetImage? editing = null;
        var cut = Render<ImageCopyPanel>(p => p.Add(c => c.ProjectId, library.ProjectId).Add(c => c.Library, library).Add(c => c.EditFirst, true)
            .Add(c => c.ReelFrame, new ReelFrameExport(reel, new(Guid.NewGuid(), new('A', 64), 4, .2))).Add(c => c.InitialName, "Bedroom · 0.2 s")
            .Add(c => c.Save, r => Task.FromResult(new SavedAssetImage(library, r.Destination.AssetId, r.ImageId, true)))
            .Add(c => c.Editing, r => editing = r));
        var actions = cut.FindAll(".image-copy-footer button").Select(b => b.TextContent).Where(t => t != "Cancel").ToArray();
        Assert.Equal(["Save and edit"], actions);
        Assert.Contains("mud-button-filled", cut.FindAll(".image-copy-footer button").Single(b => b.TextContent == "Save and edit").ClassName);
        await cut.FindAll("button").Single(b => b.TextContent == "Save and edit").ClickAsync(new());
        Assert.NotNull(editing);
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Assert.EndsWith($"imageId={editing.ImageId}&edit=true", navigation.Uri);
    }
}
