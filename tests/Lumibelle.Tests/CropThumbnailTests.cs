using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Models;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class CropThumbnailTests : BunitContext
{
    [Fact]
    public void ThumbnailUsesOutwardRoundedPixelsAndFitsTheEntireCrop()
    {
        // The prepared image uses pixels [50, 10] through [76, 31].
        var view = Render<CropThumbnail>(p => p.Add(c => c.Url, "/source.png")
            .Add(c => c.Label, "Reference image 2").Add(c => c.Width, 101).Add(c => c.Height, 81)
            .Add(c => c.Crop, new ImageCropRegion { X = .5, Y = .125, Width = .25, Height = .25 })
            .Add(c => c.ThumbnailWidth, 48).Add(c => c.ThumbnailHeight, 54));

        Assert.Equal("width:48px;height:38.769231px", view.Find(".crop-thumbnail-region").GetAttribute("style"));
        var image = view.Find("img");
        Assert.Equal("left:-192.307692%;top:-47.619048%;width:388.461538%;height:385.714286%", image.GetAttribute("style"));
        Assert.Equal("Reference image 2", image.GetAttribute("alt"));
        Assert.Equal("/source.png", image.GetAttribute("src"));
    }

    [Fact]
    public void ChangingTheAppliedCropOrSourceUpdatesTheSameThumbnail()
    {
        var view = Render<CropThumbnail>(p => p.Add(c => c.Url, "/portrait.png")
            .Add(c => c.Label, "Source image for this edit").Add(c => c.Width, 400).Add(c => c.Height, 800)
            .Add(c => c.Crop, new ImageCropRegion { X = .5, Y = .5, Width = .5, Height = .5 }));
        Assert.Equal("left:-100%;top:-100%;width:200%;height:200%", view.Find("img").GetAttribute("style"));

        view.Render(p => p.Add(c => c.Crop, null));
        Assert.Equal("left:0%;top:0%;width:100%;height:100%", view.Find("img").GetAttribute("style"));
        Assert.Equal("width:29px;height:58px", view.Find(".crop-thumbnail-region").GetAttribute("style"));

        view.Render(p => p.Add(c => c.Url, "/landscape.png").Add(c => c.Width, 800).Add(c => c.Height, 400));
        Assert.Equal("/landscape.png", view.Find("img").GetAttribute("src"));
        Assert.Equal("width:58px;height:29px", view.Find(".crop-thumbnail-region").GetAttribute("style"));
    }
}
