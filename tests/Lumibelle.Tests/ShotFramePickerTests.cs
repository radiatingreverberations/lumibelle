using Bunit;
using lumibelle.Components.Shots;
using lumibelle.Models;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

public sealed class ShotFramePickerTests : BunitContext
{
    public ShotFramePickerTests() { Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose; }
    private static ShotTake Take(Shot shot, int order, int count = 49) => new() {
        ShotId = shot.Id, CreatedUtc = DateTimeOffset.UnixEpoch.AddSeconds(order),
        Snapshot = new(Guid.NewGuid(), 1, shot.Copy(), "", "", "", new(), 160, 96, count)
    };
    private static void Loaded(IRenderedComponent<ShotFramePicker> view) => view.Find("img").TriggerEvent("onload", EventArgs.Empty);

    [Fact] public async Task DefaultsToPreviousSameSceneAndSelectedTakeThenAllowsNonAdjacentShotWithoutAssets()
    {
        var scene = Guid.NewGuid();
        var earlier = new Shot { Title = "Establishing", SceneId = scene };
        var previous = new Shot { Title = "Reaction", SceneId = scene };
        var other = new Shot { Title = "Other scene", SceneId = Guid.NewGuid() };
        var target = new Shot { Title = "Return", SceneId = scene };
        var first = Take(earlier, 1); var selected = Take(previous, 2); var latest = Take(previous, 3); var unrelated = Take(other, 4);
        previous.SelectedTakeId = selected.Id; earlier.SelectedTakeId = first.Id;
        ShotContinuityFrame? picked = null;
        var view = Render<ShotFramePicker>(p => p.Add(c => c.CurrentShot, target)
            .Add(c => c.Shots, new[] { earlier, previous, other, target }).Add(c => c.Takes, new[] { first, selected, latest, unrelated })
            .Add(c => c.Picked, (ShotContinuityFrame value) => picked = value));
        Assert.Equal(previous.Id.ToString(), view.Find("select[aria-label='Source shot']").GetAttribute("value"));
        Assert.Equal(selected.Id.ToString(), view.Find("select[aria-label='Source take']").GetAttribute("value"));
        Assert.Contains($"/takes/{selected.Id}/frames/48", view.Find("img").GetAttribute("src"));
        Assert.True(view.Find(".mud-button-root").HasAttribute("disabled"));
        view.Find("select[aria-label='Source shot']").Change(earlier.Id.ToString());
        Loaded(view);
        await view.Find(".mud-button-root").ClickAsync(new());
        Assert.Equal(first.Id, picked!.TakeId); Assert.Equal(48, picked.Frame);
        Assert.Contains("Shot 1 · Take 1 · Last frame", picked.Name);
        Assert.Null(target.ContinuityFrame); Assert.Null(target.StartFrame); Assert.Empty(target.Images);
    }

    [Fact] public async Task PreviewDoesNotFollowLaterTakeSelectionAndReplacementKeepsBindingIdentityAtBudget()
    {
        var source = new Shot { Title = "Source" }; var target = new Shot();
        var first = Take(source, 1); var second = Take(source, 2, 73); source.SelectedTakeId = second.Id;
        var original = new ShotContinuityFrame(Guid.NewGuid(), first.Id, 48, "Saved frame");
        ShotContinuityFrame? picked = null;
        var view = Render<ShotFramePicker>(p => p.Add(c => c.CurrentShot, target).Add(c => c.Shots, new[] { source, target })
            .Add(c => c.Takes, new[] { first, second }).Add(c => c.Value, original).Add(c => c.PictureCount, 9)
            .Add(c => c.Picked, (ShotContinuityFrame value) => picked = value));
        Assert.Contains($"/takes/{first.Id}/frames/48", view.Find("img").GetAttribute("src"));
        Loaded(view);
        Assert.Contains("Already attached", view.Markup); Assert.Null(picked);
        await view.Find(".frame-take-shortcut").ClickAsync(new());
        Assert.Contains($"/takes/{second.Id}/frames/72", view.Find("img").GetAttribute("src"));
        Assert.True(view.Find(".mud-button-root").HasAttribute("disabled"));
        Loaded(view);
        await view.Find(".mud-button-root").ClickAsync(new());
        Assert.Equal(original.Id, picked!.Id); Assert.Equal(second.Id, picked.TakeId); Assert.Equal(72, picked.Frame);
        Assert.Equal(first.Id, original.TakeId);
    }

    [Fact] public async Task FirstAndCustomFramesAreExactAndOnlyAvailableLoadedPicturesCanBeAdded()
    {
        var source = new Shot(); var target = new Shot(); var take = Take(source, 1);
        ShotContinuityFrame? picked = null;
        var view = Render<ShotFramePicker>(p => p.Add(c => c.CurrentShot, target).Add(c => c.Shots, new[] { source, target })
            .Add(c => c.Takes, new[] { take }).Add(c => c.Picked, (ShotContinuityFrame value) => picked = value));
        view.Find("select[aria-label='Source frame position']").Change("first");
        Assert.EndsWith("/frames/0", view.Find("img").GetAttribute("src"));
        view.Find("img").TriggerEvent("onerror", EventArgs.Empty);
        Assert.Contains("This frame is unavailable", view.Markup);
        Assert.True(view.Find(".mud-button-root").HasAttribute("disabled"));
        view.Find("select[aria-label='Source frame position']").Change("custom");
        view.Find("input[aria-label='Source frame']").Change("23");
        Assert.EndsWith("/frames/23", view.Find("img").GetAttribute("src"));
        Loaded(view); await view.Find(".mud-button-root").ClickAsync(new());
        Assert.Equal(23, picked!.Frame); Assert.Contains("Frame 24", picked.Name);
    }

    [Fact] public void FullBudgetPreventsAddingButDoesNotHideThePreview()
    {
        var source = new Shot(); var take = Take(source, 1);
        var view = Render<ShotFramePicker>(p => p.Add(c => c.CurrentShot, new Shot()).Add(c => c.Shots, new[] { source })
            .Add(c => c.Takes, new[] { take }).Add(c => c.PictureCount, 9));
        Loaded(view);
        Assert.Contains("9/9 pictures. Remove a picture", view.Markup);
        Assert.True(view.Find(".mud-button-root").HasAttribute("disabled"));
    }

    [Fact] public void ShotsWithoutSavedTakesExplainTheEmptyState()
    {
        var view = Render<ShotFramePicker>(p => p.Add(c => c.CurrentShot, new Shot()));
        Assert.Contains("No saved takes yet", view.Markup); Assert.Empty(view.FindAll("select"));
    }
}
