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
        completion = new();
        var retry = cut.FindAll("button").Single(b => b.TextContent == "Retry save").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Equal(2, calls.Count)); Assert.Same(captured, calls[1]);
        completion.SetResult(new(library, captured.Destination.AssetId, captured.ImageId, true)); await retry;
    }
}
