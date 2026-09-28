using Bunit;
using lumibelle.Components.Shots;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ShotPickerTests : BunitContext
{
    [Fact]
    public void SelectingFilteredResultsPreservesHiddenSelectionAndSkipsUnavailableShots()
    {
        var first = Item(1, "Kitchen entrance");
        var second = Item(2, "Kitchen close-up");
        var blocked = Item(3, "Kitchen wide") with { UnavailableReason = "Video job active" };
        var outside = Item(4, "Garden") with { SceneId = Guid.NewGuid(), SceneTitle = "EXT. GARDEN — DAY" };
        HashSet<Guid> selected = [outside.Id];
        var picker = Render<ShotPicker>(p => p.Add(c => c.Items, [first, second, blocked, outside])
            .Add(c => c.SelectedIds, selected).Add(c => c.SelectedIdsChanged, value => selected = value));

        picker.Find("input[type=search]").Input("Kitchen");
        Assert.Equal(3, picker.FindAll("input[type=checkbox]").Count);
        Assert.Contains("1 outside this view", picker.Markup);
        Button(picker, "Select all shown").Click();
        Assert.Equal(3, selected.Count);
        Assert.Contains(outside.Id, selected);
        Assert.Contains(first.Id, selected);
        Assert.Contains(second.Id, selected);
        Assert.DoesNotContain(blocked.Id, selected);

        picker.Render(p => p.Add(c => c.SelectedIds, selected));
        Button(picker, "Clear selection").Click();
        Assert.Empty(selected);
    }

    [Fact]
    public void SceneFilterUsesSceneIdentityAndSearchingDoesNotChangeSelection()
    {
        var first = Item(1, "Entrance");
        var second = Item(2, "Return") with { SceneId = Guid.NewGuid() };
        var changes = 0;
        var picker = Render<ShotPicker>(p => p.Add(c => c.Items, [first, second])
            .Add(c => c.SelectedIds, new HashSet<Guid> { first.Id, second.Id })
            .Add(c => c.SelectedIdsChanged, _ => changes++));
        picker.Find("select").Change(second.SceneId.ToString());
        Assert.Single(picker.FindAll("input[type=checkbox]"));
        Assert.Contains("Return", picker.Find(".picker-title").TextContent);
        picker.Find("input[type=search]").Input("no match");
        Assert.Contains("No matching shots", picker.Markup);
        Assert.Contains("2 outside this view", picker.Markup);
        Button(picker, "Reset filters").Click();
        Assert.Equal(2, picker.FindAll("input[type=checkbox]").Count);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void LockedPickerCannotChangeSelection()
    {
        var item = Item(1, "Entrance");
        var changes = 0;
        var picker = Render<ShotPicker>(p => p.Add(c => c.Items, [item])
            .Add(c => c.SelectedIds, new HashSet<Guid> { item.Id }).Add(c => c.Disabled, true)
            .Add(c => c.SelectedIdsChanged, _ => changes++));
        Assert.True(picker.Find("input[type=checkbox]").HasAttribute("disabled"));
        Assert.True(Button(picker, "Clear selection").HasAttribute("disabled"));
        picker.Find("input[type=checkbox]").Change(false);
        Button(picker, "Clear selection").Click();
        Assert.Equal(0, changes);
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<ShotPicker> picker, string text) =>
        picker.FindAll("button").Single(b => b.TextContent == text);

    private static ShotPickerItem Item(int number, string title) =>
        new(Guid.NewGuid(), number, title, Guid.Empty, "INT. KITCHEN — DAY", "8 s", 0, null);
}
