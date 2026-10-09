using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public void ContinuationSnappingUsesFullGenerationCoordinatesAndOnlyVisibleContext()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Ready()) with { FrameCount = 124, CaptureRefinementData = true };
        var original = new ShotTake { Id = Guid.NewGuid(), Snapshot = snapshot, Width = 32, Height = 32,
            RefinementPackage = new(Guid.NewGuid(), 100, new('A', 64), 32, 32, 124) };
        var first = original with { Id = Guid.NewGuid(), Trim = new(original.Id, 124, 7, 100, 7, 100, true) };
        var second = first with { Id = Guid.NewGuid(), Trim = new(first.Id, 93, 3, 90, 10, 97, true) };
        Assert.Equal(80, H3Motion.AlignedEnd(second, second.FrameCount));
        Assert.True(H3Motion.CanUseLatents(second, 80)); Assert.False(H3Motion.CanUseLatents(second, 87));
        Assert.Null(H3Motion.AlignedEnd(second, 1));
        var combined = original with { Composition = new([new(Guid.NewGuid(), original, 0, 22), new(Guid.NewGuid(), original, 90, 112)], 21) };
        Assert.Null(H3Motion.AlignedEnd(combined, 44)); Assert.False(H3Motion.CanUseLatents(combined, 44));
    }
    [Fact]
    public void MotionPreflightRefusesMissingOrChangedStockContractsAndUnreportedVersions()
    {
        // object_info shapes from the stock nodes, independent of the workflow builder.
        var nodes = JsonNode.Parse("""
            {
              "LoadLatent":{"input":{"required":{"latent":[["test.latent"]]}},"output":["LATENT"]},
              "SetLatentNoiseMask":{"input":{"required":{"samples":["LATENT"],"mask":["MASK"]}},"output":["LATENT"]},
              "LTXVConcatAVLatent":{"input":{"required":{"video_latent":["LATENT"],"audio_latent":["LATENT"]}},"output":["LATENT"]},
              "SolidMask":{"input":{"required":{"value":["FLOAT"],"width":["INT"],"height":["INT"]}},"output":["MASK"]},
              "MaskComposite":{"input":{"required":{"destination":["MASK"],"source":["MASK"],"x":["INT"],"y":["INT"],"operation":[["multiply","add"]]}},"output":["MASK"]},
              "MaskToImage":{"input":{"required":{"mask":["MASK"]}},"output":["IMAGE"]},
              "RepeatImageBatch":{"input":{"required":{"image":["IMAGE"],"amount":["INT"]}},"output":["IMAGE"]},
              "ImageBatch":{"input":{"required":{"image1":["IMAGE"],"image2":["IMAGE"]}},"output":["IMAGE"]},
              "ImageToMask":{"input":{"required":{"image":["IMAGE"],"channel":[["red","green","blue"]]}},"output":["MASK"]},
              "LoadImage":{"input":{"required":{"image":[["test.png"]]}},"output":["IMAGE","MASK"]},
              "LoadAudio":{"input":{"required":{"audio":[["test.wav"]]}},"output":["AUDIO"]},
              "MiniMaxH3AddGuide":{"input":{"required":{"positive":["CONDITIONING"],"latent":["LATENT"],"frame_idx":["INT"]},"optional":{"vae":["VAE"],"audio_vae":["VAE"],"image":["IMAGE"],"audio":["AUDIO"]}},"output":["CONDITIONING"]}
            }
            """)!;
        void Check(string? version, MotionContextRoute route = MotionContextRoute.SavedLatents) => ComfyH3Video.ValidateMotionContracts(JsonSerializer.SerializeToElement(nodes), version, route);
        Check("0.35.0"); Check("0.35.1-dev"); Check("0.35.0", MotionContextRoute.Frames); Check("0.35.0", MotionContextRoute.SingleFrame);
        foreach (var version in new[] { null, "unknown", "0.34.0" }) Assert.Contains("reported ComfyUI version", Assert.Throws<WorkspaceStoreException>(() => Check(version)).Message);
        nodes["SetLatentNoiseMask"]!["input"]!["required"]!["mask"]![0] = "IMAGE";
        Assert.Contains("SetLatentNoiseMask.mask", Assert.Throws<WorkspaceStoreException>(() => Check("0.35.0")).Message);
        nodes["SetLatentNoiseMask"]!["input"]!["required"]!["mask"]![0] = "MASK";
        nodes["LTXVConcatAVLatent"]!["output"]![0] = "IMAGE";
        Assert.Contains("LTXVConcatAVLatent LATENT output", Assert.Throws<WorkspaceStoreException>(() => Check("0.35.0")).Message);
        nodes["LTXVConcatAVLatent"]!["output"]![0] = "LATENT";
        nodes["MiniMaxH3AddGuide"]!["input"]!["optional"]!.AsObject().Remove("audio_vae");
        Assert.Contains("MiniMaxH3AddGuide.audio_vae", Assert.Throws<WorkspaceStoreException>(() => Check("0.35.0", MotionContextRoute.Frames)).Message);
    }

    [Theory]
    [InlineData(39, TakeRefinementMode.Refine)] [InlineData(22, TakeRefinementMode.Rework)]
    [InlineData(5, TakeRefinementMode.Refine)] [InlineData(1, TakeRefinementMode.Rework)]
    [InlineData(39, TakeRefinementMode.Refine, true)] [InlineData(22, TakeRefinementMode.Rework, true)]
    [InlineData(5, TakeRefinementMode.Refine, true)] [InlineData(1, TakeRefinementMode.Rework, true)]
    public void FrameGuidedRefinementHoldsTheFreshFullSourcesVideoAndAudioContext(int frames, TakeRefinementMode mode, bool leading = false)
    {
        var motion = new TakeMotionContext(Guid.NewGuid(), frames == 1 ? MotionContextRoute.SingleFrame : MotionContextRoute.Frames, 0, frames, frames, 175, []) { Direction = leading ? TakeExtensionDirection.Before : TakeExtensionDirection.After };
        var snapshot = Snapshot(Guid.NewGuid(), Ready()) with { FrameCount = 175, CaptureRefinementData = true, Profile = H3Motion.Profile, Motion = motion };
        var refinement = new TakeRefinement(Guid.NewGuid(), new(Guid.NewGuid(), 100, new('A', 64), 32, 32, 175), mode, 32, 32, "h3.safetensors", H3UpscalerImplementation.Lbh);
        var files = Enumerable.Range(0, frames).ToDictionary(i => $"motion-frame-{i:D2}.png", i => $"frame-{i}.png");
        files["motion-audio.wav"] = "context.wav";
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "motion-frames", [],
            refine: new(refinement, "full-video.latent", "full-audio.latent"), motion: new(motion, files))).GetProperty("prompt");
        JsonElement Input(string node, string field) => graph.GetProperty(node).GetProperty("inputs").GetProperty(field);
        Assert.Equal(H3Motion.VideoSteps(frames), Input("motion-held-batch", "amount").GetInt32());
        Assert.Equal(H3Motion.AudioBoundary(frames), Input("motion-audio-held", "width").GetInt32());
        Assert.Equal(H3Motion.AudioBoundary(175), Input("motion-audio-new", "width").GetInt32());
        Assert.Equal("multiply", Input("motion-audio-mask", "operation").GetString());
        Assert.Equal(leading ? 175 - frames : 0, Input("motion-guide", "frame_idx").GetInt32());
        Assert.Equal(leading ? H3Motion.AudioBoundary(175) - H3Motion.AudioBoundary(frames) : 0, Input("motion-audio-mask", "x").GetInt32());
        Assert.Equal(leading ? "motion-new-batch" : "motion-held-batch", Input("motion-mask-batch", "image1")[0].GetString());
        Assert.Equal(0, Input("motion-audio-held", "value").GetDouble());
        Assert.Equal("motion-video-mask", Input("refine-motion-video", "mask")[0].GetString());
        Assert.Equal("motion-audio-mask", Input("refine-motion-audio", "mask")[0].GetString());
        Assert.Equal("32", Input("refine-motion-video", "samples")[0].GetString());
        Assert.Equal("31", Input("refine-motion-audio", "samples")[0].GetString());
        Assert.Equal("motion-guide", Input("6", "conditioning")[0].GetString());
        Assert.Equal(mode == TakeRefinementMode.Refine ? "31" : "20", Input("22", "samples")[0].GetString());
    }
    [Theory]
    [InlineData("F32", 56)] [InlineData("F32", 73)] [InlineData("F32", 124)]
    [InlineData("F16", 73)] [InlineData("BF16", 73)]
    [InlineData("F32", 56, true)] [InlineData("F16", 73, true)] [InlineData("BF16", 124, true)]
    public async Task MotionInputsCopyExactTensorPlanesWithoutScalingOrChangingTheSource(string dtype, int end, bool leading = false)
    {
        Directory.CreateDirectory(_root);
        var sourceFrames = 124; var outputFrames = 175;
        var video = Path.Combine(_root, "original-video.latent"); var audio = Path.Combine(_root, "original-audio.latent");
        await MockRefinementPackage.WriteLatentAsync(video, RefinementPackages.VideoShape(sourceFrames, 64, 32), _ct, dtype);
        await MockRefinementPackage.WriteLatentAsync(audio, RefinementPackages.AudioShape(sourceFrames), _ct, dtype);
        async Task<(byte[] Data, long[] Shape)> Fill(string path)
        {
            var bytes = await File.ReadAllBytesAsync(path, _ct); var headerLength = (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            using var header = JsonDocument.Parse(bytes.AsMemory(8, headerLength));
            var shape = header.RootElement.GetProperty("latent_tensor").GetProperty("shape").EnumerateArray().Select(n => n.GetInt64()).ToArray();
            for (var i = 8 + headerLength; i < bytes.Length; i++) bytes[i] = (byte)(i % 251 + 1);
            await File.WriteAllBytesAsync(path, bytes, _ct);
            return (bytes[(8 + headerLength)..], shape);
        }
        var sourceVideo = await Fill(video); var sourceAudio = await Fill(audio);
        var package = Path.Combine(_root, "full.safetensors");
        await RefinementPackages.ComposeAsync(video, audio, package, Guid.NewGuid(), 64, 32, sourceFrames, _ct);
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(package, _ct));
        await RefinementPackages.PrepareMotionAsync(package, Path.Combine(_root, "motion"), end - 39, outputFrames, leading, _ct);
        foreach (var (name, source, temporalAxis, start, kept, targetTime) in new[] {
            ("video", sourceVideo, 2, (end - 39) / 17 * 5, 12, H3Motion.VideoSteps(outputFrames)),
            ("audio", sourceAudio, 3, H3Motion.AudioBoundary(end - 39), 65, H3Motion.AudioBoundary(outputFrames)) }) {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, "motion", "motion-" + name + ".latent"), _ct);
            var headerLength = (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            using var header = JsonDocument.Parse(bytes.AsMemory(8, headerLength));
            Assert.True(header.RootElement.TryGetProperty("latent_format_version_0", out _)); // LoadLatent must not apply legacy scaling.
            var tensor = header.RootElement.GetProperty("latent_tensor"); Assert.Equal(dtype, tensor.GetProperty("dtype").GetString());
            var shape = tensor.GetProperty("shape").EnumerateArray().Select(n => n.GetInt64()).ToArray();
            var expectedShape = source.Shape.ToArray(); expectedShape[temporalAxis] = targetTime; Assert.Equal(expectedShape, shape);
            var element = dtype == "F32" ? 4 : 2;
            var stride = temporalAxis == 2 ? (int)(shape[3] * shape[4] * element) : element;
            var planes = temporalAxis == 2 ? shape[0] * shape[1] : shape[0] * shape[1] * shape[2];
            for (var plane = 0; plane < planes; plane++) {
                var originalOffset = (int)((plane * source.Shape[temporalAxis] + start) * stride);
                var planeOffset = 8 + headerLength + plane * targetTime * stride;
                var outputOffset = planeOffset + (leading ? targetTime - kept : 0) * stride;
                Assert.Equal(source.Data.AsSpan(originalOffset, kept * stride).ToArray(), bytes.AsSpan(outputOffset, kept * stride).ToArray());
                Assert.All(bytes.AsSpan(leading ? planeOffset : outputOffset + kept * stride, (targetTime - kept) * stride).ToArray(), b => Assert.Equal(0, b));
            }
        }
        using var mask = await Image.LoadAsync<Rgb24>(Path.Combine(_root, "motion", "motion-audio-mask.png"), _ct);
        Assert.Equal(H3Motion.AudioBoundary(outputFrames), mask.Width); Assert.Equal(2, mask.Height);
        for (var x = 0; x < mask.Width; x++) for (var y = 0; y < mask.Height; y++) Assert.Equal((leading ? x >= mask.Width - 65 : x < 65) ? new Rgb24(0, 0, 0) : new Rgb24(255, 255, 255), mask[x, y]);
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(package, _ct)));
    }

    [Theory]
    [InlineData(null)] [InlineData(TakeRefinementMode.Refine)] [InlineData(TakeRefinementMode.Rework)]
    public void MotionGraphPreservesIndependentMasksAndRefinementAudioSemantics(TakeRefinementMode? mode)
    {
        var snapshot = Snapshot(Guid.NewGuid(), Ready()) with { FrameCount = 175, CaptureRefinementData = true, Profile = H3Motion.Profile };
        var motion = new TakeMotionContext(Guid.NewGuid(), MotionContextRoute.SavedLatents, 34, 73, 39, 175, []);
        snapshot = snapshot with { Motion = motion, Performance = H3Performance.Capture(new() { Attention = H3AttentionBackend.PyTorch, SolAttention = true }) };
        ComfyH3Video.RefineSource? refine = mode is { } m ? new(new(Guid.NewGuid(), new(Guid.NewGuid(), 100, new('A', 64), 32, 32, 175), m, 32, 32, "h3.safetensors", H3UpscalerImplementation.Lbh), "full-video.latent", "full-audio.latent") : null;
        var graph = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 42, "motion", [], refine: refine,
            motion: new(motion, new Dictionary<string, string> { ["motion-video.latent"] = "v.latent", ["motion-audio.latent"] = "a.latent", ["motion-audio-mask.png"] = "m.png" }))).GetProperty("prompt");
        JsonElement Input(string node, string field) => graph.GetProperty(node).GetProperty("inputs").GetProperty(field);
        Assert.Equal(12, Input("motion-held-batch", "amount").GetInt32()); Assert.Equal(40, Input("motion-new-batch", "amount").GetInt32());
        Assert.Equal(0, Input("motion-held", "value").GetDouble()); Assert.Equal(1, Input("motion-new", "value").GetDouble());
        Assert.Equal("motion-video-masked", Input("motion-av", "video_latent")[0].GetString());
        Assert.Equal("motion-audio-masked", Input("motion-av", "audio_latent")[0].GetString());
        Assert.Equal(mode is null ? "motion-av" : "33", Input("10", "latent_image")[0].GetString());
        Assert.Equal(mode is null ? "31" : "refine-attention-31", Input("6", "model")[0].GetString());
        if (mode is not null) {
            Assert.Equal("refine-motion-video", Input("33", "video_latent")[0].GetString());
            Assert.Equal("motion-video-mask", Input("refine-motion-video", "mask")[0].GetString());
            Assert.Equal("motion-audio-mask", Input("refine-motion-audio", "mask")[0].GetString());
        }
        Assert.Equal(mode == TakeRefinementMode.Refine ? "31" : "20", Input("22", "samples")[0].GetString());
        Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString()!.StartsWith("Lumibelle"));
    }
}
