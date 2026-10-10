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
}
