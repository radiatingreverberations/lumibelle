using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Models;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class LoraPickerTests : BunitContext
{
    public LoraPickerTests() { Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose; Render<MudPopoverProvider>(); }

    [Fact]
    public void EditsBuildOnThePublishedListBeforeTheParentPassesItBack()
    {
        // Inside a MudDialog the parent's new list reaches the picker a round trip later.
        // The parent here never passes it back, so every edit must build on the last one.
        var mouse = LoraTests.Definition(ImageWorkflow.Krea2, "mouse.safetensors", "Mouse");
        var film = LoraTests.Definition(ImageWorkflow.Krea2, "styles/film.safetensors", "Film");
        var published = new List<IReadOnlyList<LoraSelection>>();
        var ui = Render<LoraPicker>(p => p.Add(c => c.Settings, new AiSettings { LoraLibrary = [mouse, film] })
            .Add(c => c.Workflow, mouse.Reference.Workflow)
            .Add(c => c.Selections, [new(mouse.Reference, .75f), new(film.Reference, 1.2f)])
            .Add(c => c.Changed, (IReadOnlyList<LoraSelection> s) => published.Add(s)));

        ui.Find("input[aria-label='Strength for Mouse']").Input("0.55");
        ui.Find("button[aria-label='Move Film up']").Click();
        Assert.Equal([new(film.Reference, 1.2f), new(mouse.Reference, .55f)], published[^1]);
        Assert.Equal("0.55", ui.Find("input[aria-label='Strength for Mouse']").GetAttribute("value"));

        ui.Find("input[type=checkbox]").Change(false);
        Assert.Equal([new(film.Reference, 1.2f, false), new(mouse.Reference, .55f)], published[^1]);
        ui.Find("button[aria-label='Remove Film']").Click();
        Assert.Equal([new LoraSelection(mouse.Reference, .55f)], published[^1]);

        // New parameters from the parent are authoritative again.
        ui.Render(p => p.Add(c => c.Selections, [new(mouse.Reference, .9f)]));
        Assert.Equal("0.9", ui.Find("input[aria-label='Strength for Mouse']").GetAttribute("value"));
    }
}
