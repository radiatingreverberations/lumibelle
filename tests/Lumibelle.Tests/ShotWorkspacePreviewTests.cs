using Bunit;
using lumibelle.Components.Shots;
using lumibelle.Models;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ShotWorkspacePreviewTests : BunitContext
{
    public ShotWorkspacePreviewTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void BrowsingDoesNotSelectATakeAndArrivingResultsDoNotInterruptPreview()
    {
        var shot = Guid.NewGuid();
        var first = Take(shot); var second = Take(shot); var third = Take(shot);
        ShotTake? reviewed = null;
        var preview = Render<ShotWorkspacePreview>(p => p.Add(c => c.ShotId, shot)
            .Add(c => c.SelectedTakeId, first.Id).Add(c => c.Takes, [first, second])
            .Add(c => c.Review, t => reviewed = t));
        Assert.Contains(first.Id.ToString(), preview.Find("video").GetAttribute("src"));
        preview.Find("[aria-label='Next take preview']").Click();
        Assert.Contains(second.Id.ToString(), preview.Find("video").GetAttribute("src"));
        Assert.Contains("Preview only", preview.Markup);
        Assert.Equal(first.Id, preview.Instance.SelectedTakeId);
        Assert.Null(reviewed);

        preview.Render(p => p.Add(c => c.Takes, [first, second, third]));
        Assert.Contains(second.Id.ToString(), preview.Find("video").GetAttribute("src"));
        preview.FindAll("button").Single(b => b.TextContent == "Review take").Click();
        Assert.Equal(second, reviewed);
    }

    [Fact]
    public void SwitchingShotClearsPreviewAndEmptyShotHasNoVideo()
    {
        var shot = Guid.NewGuid(); var first = Take(shot);
        var preview = Render<ShotWorkspacePreview>(p => p.Add(c => c.ShotId, shot).Add(c => c.Takes, [first]));
        Assert.Single(preview.FindAll("video"));
        preview.Render(p => p.Add(c => c.ShotId, Guid.NewGuid()).Add(c => c.Takes, []));
        Assert.Empty(preview.FindAll("video"));
        Assert.Contains("Your shot starts here", preview.Markup);
    }

    [Fact]
    public void RemovedPreviewFallsBackToAvailableSelectedTake()
    {
        var shot = Guid.NewGuid(); var first = Take(shot); var second = Take(shot);
        var preview = Render<ShotWorkspacePreview>(p => p.Add(c => c.ShotId, shot)
            .Add(c => c.SelectedTakeId, first.Id).Add(c => c.Takes, [first, second]));
        preview.Find("[aria-label='Next take preview']").Click();
        preview.Render(p => p.Add(c => c.Takes, [first]));
        Assert.Contains(first.Id.ToString(), preview.Find("video").GetAttribute("src"));
        Assert.Contains("Selected take", preview.Markup);
    }

    private static ShotTake Take(Guid shot) => new() {
        ShotId = shot, Width = 640, Height = 360,
        Snapshot = new(Guid.NewGuid(), 0, new Shot { Id = shot }, "", "", "http://localhost:8188", new(), 640, 360, 49)
    };
}
