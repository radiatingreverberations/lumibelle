using Bunit;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

public sealed class VideoPreparationTests : BunitContext
{
    [Fact]
    public void PreparationShowsUnknownWorkWithoutAPercentageAndCountsCompletedInputs()
    {
        var cut = Render<VideoPreparationStatus>(p => p
            .Add(c => c.Progress, new(VideoPreparationStage.Setup, "Checking setup"))
            .Add(c => c.Elapsed, TimeSpan.FromSeconds(65)));
        Assert.False(cut.Find("progress").HasAttribute("value"));
        Assert.Contains("1:05", cut.Markup);
        Assert.Equal("Check setup", cut.Find("[aria-current=step]").TextContent.TrimStart('1').Trim());
        cut.Render(p => p.Add(c => c.Progress, new(VideoPreparationStage.Capture, "Saving inputs", 2, 3, "video-01.mp4")));
        Assert.Equal("2", cut.Find("progress").GetAttribute("value"));
        Assert.Equal("3", cut.Find("progress").GetAttribute("max"));
        Assert.Contains("2 / 3 inputs", cut.Markup);
        Assert.Contains("video-01.mp4", cut.Markup);
        Assert.Equal(2, cut.FindAll("li.complete").Count);
    }

    [Theory]
    [InlineData("16:9", 608, 352, VideoResolution.Detail)]
    [InlineData("16:9", 1120, 640, VideoResolution.Detail)]
    [InlineData("16:9", 1344, 768, VideoResolution.Native)]
    [InlineData("16:9", 1920, 1088, VideoResolution.FullHd)]
    [InlineData("9:16", 640, 1120, VideoResolution.Detail)]
    [InlineData("1:1", 992, 992, VideoResolution.Native)]
    public void ReelPromotionDefaultsToDetailOrLargerWithoutDownsizing(string aspect, int width, int height, VideoResolution expected)
    {
        Assert.Equal(expected, MotionReferencePolicy.DefaultResolution(aspect, width, height, reel: true));
        var choices = MotionReferencePolicy.Resolutions(aspect, width, height);
        Assert.All(choices, r => {
            var size = VideoResolutions.Size(aspect, r);
            Assert.True(size.Width >= width && size.Height >= height);
        });
        Assert.Contains(VideoResolution.Qhd, choices);
    }

    [Fact]
    public void ShotPromotionKeepsNativeDefaultAndOversizeSourcesCannotBeSilentlyDownsized()
    {
        Assert.Equal(VideoResolution.Native, MotionReferencePolicy.DefaultResolution("16:9", 608, 352, reel: false));
        Assert.Empty(MotionReferencePolicy.Resolutions("16:9", 3840, 2160));
        Assert.Throws<lumibelle.Services.Story.WorkspaceStoreException>(() => MotionReferencePolicy.DefaultResolution("16:9", 3840, 2160, reel: true));
    }
}
