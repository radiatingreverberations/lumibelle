using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class TextImageTokensTests
{
    [Fact]
    public void HostedModelsScaleLargeImagesBeforeCounting()
    {
        Assert.Equal(1334, TextImageTokens.Hosted(1000, 1000));
        // 4000 × 3000 is scaled to about 1.15 megapixels first, so it costs about as much as any large image.
        Assert.InRange(TextImageTokens.Hosted(4000, 3000), 1500, 1540);
        Assert.Equal(TextImageTokens.Hosted(400, 300), (int)Math.Ceiling(400 * 300 / 750d));
    }

    [Fact]
    public void ComfyUIImagesShareTheBatchCanvasOfTheModelsImageSize()
    {
        var model = new TextModelReference(AiBackend.ComfyUI, "qwen3_vl.safetensors", "Qwen") { ComfyUrl = "http://localhost:8188" };
        var settings = ComfyTextSettings.WithBatchImageSide(model, new AiSettings(), 512);
        var one = TextImageTokens.Estimate(model, settings, [new ImageSize(640, 320)]);
        Assert.Equal(ComfyTextCapacity.ImageTokens(model.Model, 640, 320), one);
        var two = TextImageTokens.Estimate(model, settings, [new ImageSize(640, 320), new ImageSize(320, 640)]);
        Assert.Equal(2 * ComfyTextCapacity.ImageTokens(model.Model, 512, 512), two);
        Assert.Equal(0, TextImageTokens.Estimate(model, settings, Array.Empty<ImageSize>()));
        // Hosted models count each image at its own size.
        var hosted = model with { Backend = AiBackend.OpenRouter };
        Assert.Equal(TextImageTokens.Hosted(640, 320) + TextImageTokens.Hosted(320, 640), TextImageTokens.Estimate(hosted, settings, [new ImageSize(640, 320), new ImageSize(320, 640)]));
    }

    [Fact]
    public void TheReferencesRowSaysWhereItsImagesComeFrom()
    {
        ImageSize[] Images(int n) => [.. Enumerable.Repeat(new ImageSize(64, 64), n)];
        Assert.Equal("9 pictures + 14 reel frames", new CompositionContextSize(0, 0, 0) { ReferenceImages = Images(23), ReelFrames = 14 }.DescribeImages());
        Assert.Equal("1 picture + opening frame", new CompositionContextSize(0, 0, 0) { ReferenceImages = Images(2), OpeningFrame = true }.DescribeImages());
        Assert.Equal("1 reel frame", new CompositionContextSize(0, 0, 0) { ReferenceImages = Images(1), ReelFrames = 1 }.DescribeImages());
    }
}

