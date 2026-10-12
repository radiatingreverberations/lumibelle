using Bunit;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.Assets;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

public sealed class ReferenceCopyPanelTests : BunitContext
{
    public ReferenceCopyPanelTests() { Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose; }
    [Fact] public async Task DefaultPreviousShotOffersOptionalFirstOrLastContinuityAndCancelLeavesInputsUntouched()
    {
        var environment = new ReferenceAsset { Id = Guid.NewGuid(), Category = AssetCategory.Environment, Name = "Plaza" };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [environment] };
        var previous = new Shot { Title = "Previous", Duration = 5, EnvironmentAssetId = environment.Id };
        var later = new Shot { Title = "Later", Duration = 5 };
        var target = new Shot { Title = "Target", Duration = 5 };
        var take = Guid.NewGuid(); ReferenceSelection? applied = null; var cancelled = false;
        var panel = Render<ReferenceCopyPanel>(p => p.Add(c => c.Shot, target).Add(c => c.Library, library)
            .Add(c => c.Sources, new[] { previous, later }).Add(c => c.Previous, previous)
            .Add(c => c.Number, s => s.Id == previous.Id ? 1 : 3).Add(c => c.CurrentNumber, 2)
            .Add(c => c.FrameOf, (s, first) => new(Guid.NewGuid(), take, first ? 0 : 48, s.Title + (first ? " · first frame" : " · last frame")))
            .Add(c => c.Applied, (ReferenceSelection value) => applied = value).Add(c => c.Cancelled, () => cancelled = true));
        Assert.Equal(previous.Id.ToString(), panel.Find("select[aria-label='Copy from shot']").GetAttribute("value"));
        Assert.Equal(environment.Id.ToString(), panel.Find("select[aria-label='Environment library']").GetAttribute("value"));
        Assert.Contains($"/frames/48", panel.Find("img").GetAttribute("src"));
        panel.Find("select[aria-label='Continuity frame']").Change("first");
        Assert.Contains("/frames/0", panel.Find("img").GetAttribute("src"));
        panel.Find("input[aria-label='Include continuity picture']").Change(false);
        Assert.Empty(panel.FindAll("img")); Assert.Null(target.ContinuityFrame);
        await panel.FindAll("button").Single(b => b.TextContent == "Cancel").ClickAsync(new());
        Assert.True(cancelled); Assert.Null(applied); Assert.Null(target.EnvironmentAssetId);
        panel.Find("select[aria-label='Copy from shot']").Change(later.Id.ToString());
        Assert.Contains("No environment library selected", panel.Markup);
        Assert.Equal("first", panel.Find("select[aria-label='Continuity frame']").GetAttribute("value"));
        panel.Find("select[aria-label='Copy from shot']").Change(previous.Id.ToString());
        await panel.FindAll("button").Single(b => b.TextContent == "Copy references").ClickAsync(new());
        Assert.NotNull(applied); Assert.Equal(environment.Id, applied.Inputs.EnvironmentAssetId);
        Assert.Equal(48, applied.Inputs.ContinuityFrame!.Frame); Assert.Null(target.ContinuityFrame);
    }
    [Fact] public async Task LibraryCanBeChosenExplicitlyWhenTheSourceHasOnlyManualReferences()
    {
        var environment = new ReferenceAsset { Id = Guid.NewGuid(), Category = AssetCategory.Environment, Name = "Plaza" };
        var library = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [environment] };
        var source = new Shot { Duration = 5 }; var target = new Shot { Duration = 5 }; ReferenceSelection? applied = null;
        var panel = Render<ReferenceCopyPanel>(p => p.Add(c => c.Shot, target).Add(c => c.Library, library)
            .Add(c => c.Sources, new[] { source }).Add(c => c.Previous, source).Add(c => c.Number, _ => 1)
            .Add(c => c.CurrentNumber, 2).Add(c => c.FrameOf, (_, _) => null)
            .Add(c => c.Applied, (ReferenceSelection value) => applied = value));
        Assert.Contains("No environment library selected", panel.Markup);
        panel.Find("select[aria-label='Environment library']").Change(environment.Id.ToString());
        Assert.Null(target.EnvironmentAssetId); Assert.Null(source.EnvironmentAssetId);
        await panel.FindAll("button").Single(b => b.TextContent == "Copy references").ClickAsync(new());
        Assert.Equal(environment.Id, applied!.Inputs.EnvironmentAssetId);
        Assert.Empty(applied.Inputs.Images); Assert.Empty(applied.Inputs.Videos);
    }
    [Fact] public void FullPictureBudgetDisablesExtraContinuityWithoutDroppingSourcePictures()
    {
        var library = new AssetLibrary { ProjectId = Guid.NewGuid() };
        var previous = new Shot { Duration = 5, Images = Enumerable.Range(0, 9).Select(_ => new ShotImageBinding()).ToList() };
        var panel = Render<ReferenceCopyPanel>(p => p.Add(c => c.Shot, new Shot { Duration = 5 }).Add(c => c.Library, library)
            .Add(c => c.Sources, new[] { previous }).Add(c => c.Previous, previous).Add(c => c.Number, _ => 1)
            .Add(c => c.CurrentNumber, 2).Add(c => c.FrameOf, (_, _) => new(Guid.NewGuid(), Guid.NewGuid(), 48, "Last frame")));
        Assert.True(panel.Find("input[aria-label='Include continuity picture']").HasAttribute("disabled"));
        Assert.Contains("9/9 pictures", panel.Markup); Assert.Contains("fill all nine picture slots", panel.Markup);
        Assert.Equal(9, previous.Images.Count);
    }
}
