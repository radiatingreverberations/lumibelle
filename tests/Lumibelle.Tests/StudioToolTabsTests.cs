using Bunit;
using lumibelle.Components.Layout;
using lumibelle.Models;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class StudioToolTabsTests : BunitContext
{
    [Fact]
    public void ATabMarksItemsNotSeenYet()
    {
        var ui = Render<StudioToolTabs>(p => p.Add(c => c.Items, [new StudioTab("Shot", "Shot"), new StudioTab("Takes", "Takes", 4, New: 1)]));

        var takes = ui.Find("[data-workspace-tab=Takes]");
        Assert.Equal("1 new", takes.QuerySelector(".tab-new")!.TextContent);
        Assert.Equal("4 items. 1 new. ", takes.GetAttribute("aria-description"));
        Assert.Null(ui.Find("[data-workspace-tab=Shot]").QuerySelector(".tab-new"));
    }
}
