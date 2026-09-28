using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Models;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ImageMoveComponentTests : BunitContext
{
    public ImageMoveComponentTests() { Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose; }

    [Fact]
    public async Task MoveFailureKeepsDestinationAndSelectionForRetryAndBlocksDoubleClick()
    {
        var source = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Frames", Category = AssetCategory.Reference,
            Images = [new() { Id = Guid.NewGuid(), FileName = "frame.png", ContentType = "image/png", Width = 16, Height = 16 }] };
        var target = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Juniper", Category = AssetCategory.Character,
            Looks = [new() { Id = Guid.NewGuid(), Name = "Gala" }, new() { Id = Guid.NewGuid(), Name = "Old", Archived = true }] };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [source, target] };
        List<ImageDestination> calls = [];
        var completion = new TaskCompletionSource<SavedAssetImage>(); var applied = 0;
        var cut = Render<ImageMovePanel>(p => p.Add(c => c.Library, library).Add(c => c.Source, source).Add(c => c.Images, source.Images)
            .Add(c => c.Save, d => { calls.Add(d); return completion.Task; }).Add(c => c.Moved, _ => applied++));
        Assert.True(cut.FindAll("button").Single(b => b.TextContent == "Move").HasAttribute("disabled"));
        cut.Find("select").Change(target.Id.ToString());
        Assert.DoesNotContain(target.Looks[1].Id.ToString(), cut.Markup);
        cut.FindAll("select")[1].Change(target.Looks[0].Id.ToString());
        var saving = cut.FindAll("button").Single(b => b.TextContent == "Move").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Single(calls)); Assert.True(cut.Find("fieldset").HasAttribute("disabled"));
        await cut.FindAll("button").Single(b => b.TextContent == "Move and edit").ClickAsync(new()); Assert.Single(calls);
        completion.SetException(new WorkspaceConflictException()); await saving;
        Assert.Equal(0, applied); cut.WaitForElement("[role=alert]");
        var captured = calls[0]; Assert.Equal(target.Looks[0].Id, captured.LookId);
        completion = new(); var retry = cut.FindAll("button").Single(b => b.TextContent == "Retry move").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Equal(2, calls.Count)); Assert.Same(captured, calls[1]);
        completion.SetResult(new(library, target.Id, source.Images[0].Id, true)); await retry;
        Assert.Equal(1, applied);
    }
}
