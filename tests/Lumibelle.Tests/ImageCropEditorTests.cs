using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Models;
using lumibelle.Services.Assets;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ImageCropEditorTests : BunitContext
{
    public ImageCropEditorTests() { JSInterop.Mode = JSRuntimeMode.Loose; }

    private IRenderedComponent<ImageCropEditor> Editor(List<ImageCropRegion> changes, ImageCropRegion? initial = null) =>
        Render<ImageCropEditor>(p => p.Add(c => c.Url, "/image.png").Add(c => c.Width, 1600).Add(c => c.Height, 900)
            .Add(c => c.InitialCrop, initial).Add(c => c.CropChanged, (ImageCropRegion r) => changes.Add(r)));

    [Fact]
    public void AFreeSelectionTakesAnyRectangleInPixelsAndKeepsItsCorners()
    {
        var changes = new List<ImageCropRegion>();
        var ui = Editor(changes);
        Assert.Empty(ui.FindAll("[data-crop-handle=top-left]"));

        ui.Find("select").Change("Free");
        Assert.Equal(8, ui.FindAll("[data-crop-handle]").Count);
        Assert.Equal("true", ui.Find(".crop-stage").GetAttribute("data-free"));
        Assert.Empty(ui.FindAll("input[aria-label='Crop zoom']"));
        ui.Find("input[aria-label='Crop width']").Change("1000");
        ui.Find("input[aria-label='Crop height']").Change("300");
        ui.Find("input[aria-label='Crop left']").Change("200");
        ui.Find("input[aria-label='Crop top']").Change("550");

        // The pixels entered are the pixels used, here without the image's proportions.
        Assert.Equal(new SixLabors.ImageSharp.Rectangle(200, 550, 1000, 300), ImageGeometry.CropPixels(1600, 900, changes[^1]));
        Assert.Contains("1000 × 300 pixels", ui.Find(".crop-size").TextContent);
        // A side can shrink only as far as the zoom limit lets a fixed shape.
        ui.Find("input[aria-label='Crop height']").Change("10");
        Assert.Equal(225, ImageGeometry.CropPixels(1600, 900, changes[^1]).Height);
    }

    [Fact]
    public async Task DraggingAFreeSelectionAppliesItsRectangleAndAFixedShapeIgnoresIt()
    {
        var changes = new List<ImageCropRegion>();
        var ui = Editor(changes);
        await ui.InvokeAsync(() => ui.Instance.ApplyFreeCrop(.1, .2, .3, .4));
        Assert.Empty(changes);

        ui.Find("select").Change("Free");
        await ui.InvokeAsync(() => ui.Instance.ApplyFreeCrop(.1, .2, .3, .4));
        Assert.Equal((.1, .2, .3, .4), (changes[^1].X, changes[^1].Y, changes[^1].Width, changes[^1].Height));
        // Out of range values stay inside the image.
        await ui.InvokeAsync(() => ui.Instance.ApplyFreeCrop(.9, -1, .5, 2));
        Assert.Equal((.5, 0d, .5, 1d), (changes[^1].X, changes[^1].Y, changes[^1].Width, changes[^1].Height));
    }

    [Fact]
    public void ChangingShapeKeepsTheSelectionInPlace()
    {
        var changes = new List<ImageCropRegion>();
        var ui = Editor(changes, new() { X = .5, Y = .25, Width = .25, Height = .5 });
        // An earlier crop that matches no shape reopens free, exactly as it was.
        Assert.Equal("Free", ui.Find("select").GetAttribute("value"));
        Assert.Equal("0.5", ui.Find(".crop-selection").GetAttribute("data-x"));

        // 1:1 becomes the largest square inside it, around the same centre.
        ui.Find("select").Change("1:1");
        var square = changes[^1];
        Assert.Equal(1600 * square.Width, 900 * square.Height, 6);
        Assert.Equal(.25, square.Width, 6);
        Assert.Equal(.625, square.X + square.Width / 2, 6); Assert.Equal(.5, square.Y + square.Height / 2, 6);
        Assert.NotEmpty(ui.FindAll("input[aria-label='Crop zoom']"));

        // Back to free starts from the square, and Full image keeps the free shape.
        ui.Find("select").Change("Free");
        Assert.Equal(square, changes[^1]);
        ui.Find(".crop-size button").Click();
        Assert.Equal(("Free", 1d, 1d), (ui.Find("select").GetAttribute("value"), changes[^1].Width, changes[^1].Height));
    }

    [Fact]
    public void ACropMadeWithAShapeReopensInThatShape()
    {
        var changes = new List<ImageCropRegion>();
        var ui = Editor(changes, new() { X = .3, Y = .1, Width = .5625 / 2, Height = .5 });
        Assert.Equal("1:1", ui.Find("select").GetAttribute("value"));
        Assert.Equal(2, double.Parse(ui.Find("input[aria-label='Crop zoom']").GetAttribute("value")!, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(.3, double.Parse(ui.Find(".crop-selection").GetAttribute("data-x")!, System.Globalization.CultureInfo.InvariantCulture), 6);

        var original = Editor(changes, new() { X = .25, Y = .25, Width = .5, Height = .5 });
        Assert.Equal("Original", original.Find("select").GetAttribute("value"));
    }
}
