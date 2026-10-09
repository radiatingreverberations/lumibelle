using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using SixLabors.ImageSharp;

namespace Lumibelle.Tests;

// Unlike the standalone diagnostic, these tests invoke the actual production
// service. They are opt-in because FFmpeg/ffprobe are external dependencies.
public sealed class CutExportMediaTests : IDisposable
{
    public static bool MediaTestsEnabled => Environment.GetEnvironmentVariable("LUMIBELLE_TEST_FFMPEG") == "1";
    private const string OptIn = "Set LUMIBELLE_TEST_FFMPEG=1 and provide FFmpeg/ffprobe to run media integration tests.";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumibelle-cut-tests-" + Guid.NewGuid().ToString("N"));
    private readonly H3Settings _settings = new()
    {
        Ffmpeg = Environment.GetEnvironmentVariable("LUMIBELLE_FFMPEG") ?? "ffmpeg",
        Ffprobe = Environment.GetEnvironmentVariable("LUMIBELLE_FFPROBE") ?? "ffprobe"
    };
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));
    private readonly ProductionMediaTools _media = new();

    [Fact(Skip = OptIn, SkipUnless = nameof(MediaTestsEnabled))]
    public async Task MixedAudioExportKeepsDialogueAndInsertsSilenceAtTheRightTime()
    {
        var voiced = await Fixture("red", audioSeconds: 2);
        var silent = await Fixture("blue");
        var output = Path.Combine(_directory, "mixed.mp4");
        await _media.ExportCutAsync([new(voiced, 0, 24, 24), new(silent, 0, 24, 24), new(voiced, 24, 48, 24)], output, _settings, _timeout.Token);
        await AssertVideo(output, frames: 72, duration: 3, audio: true);
        var samples = await Audio(output);
        Assert.True(Rms(samples, .15, .75) > .02, "First voiced segment was lost.");
        Assert.True(Rms(samples, 1.15, 1.75) < .002, "The silent segment contains unexpected audio.");
        Assert.True(Rms(samples, 2.15, 2.75) > .02, "Last voiced segment was lost.");
        await AssertColor(output, 12, red: true);
        await AssertColor(output, 36, red: false);
        await AssertColor(output, 60, red: true);
    }

    [Fact(Skip = OptIn, SkipUnless = nameof(MediaTestsEnabled))]
    public async Task SilentExportHasAnExactDurationAndOneFrameTrimUsesAnExclusiveEnd()
    {
        var silent = await Fixture("blue");
        var output = Path.Combine(_directory, "silent.mp4");
        await _media.ExportCutAsync([new(silent, 0, 24, 24), new(silent, 24, 48, 24)], output, _settings, _timeout.Token);
        await AssertVideo(output, 48, 2, audio: false);
        var changing = Path.Combine(_directory, "changing.mp4");
        await Run(_settings.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i",
            "color=c=red:s=64x64:r=24:d=2", "-vf", "drawbox=x=0:y=0:w=iw:h=ih:color=blue:t=fill:enable='gte(t,1)'",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", changing]);
        output = Path.Combine(_directory, "one-frame.mp4");
        await _media.ExportCutAsync([new(changing, 24, 25, 24)], output, _settings, _timeout.Token);
        await AssertVideo(output, 1, 1d / 24, audio: false);
        await AssertColor(output, 0, red: false);
    }

    [Fact(Skip = OptIn, SkipUnless = nameof(MediaTestsEnabled))]
    public async Task MixedFrameRatesAndDimensionsUseTheFirstClipsOutputGrid()
    {
        var first = await Fixture("red", audioSeconds: 2);
        var second = await Fixture("blue", fps: 30, width: 96);
        var output = Path.Combine(_directory, "rates.mp4");
        await _media.ExportCutAsync([new(first, 0, 24, 24), new(second, 0, 30, 30)], output, _settings, _timeout.Token);
        await AssertVideo(output, 48, 2, audio: true);
        var info = await _media.VideoInfoAsync(output, _settings, _timeout.Token);
        Assert.Equal(64, info.Width); Assert.Equal(64, info.Height); Assert.InRange(info.Fps, 23.999, 24.001);
        await AssertColor(output, 36, red: false);
    }

    [Fact(Skip = OptIn, SkipUnless = nameof(MediaTestsEnabled))]
    public async Task TrimmingPastAnEarlyAudioEndPadsSilenceInsteadOfTruncatingVideo()
    {
        var shortAudio = await Fixture("red", audioSeconds: .25);
        var silent = await Fixture("blue");
        var output = Path.Combine(_directory, "padding.mp4");
        await _media.ExportCutAsync([new(shortAudio, 24, 48, 24), new(silent, 0, 24, 24)], output, _settings, _timeout.Token);
        await AssertVideo(output, 48, 2, audio: true);
        Assert.True(Rms(await Audio(output), .15, 1.75) < .002);
        var trimmed = Path.Combine(_directory, "trim-padding.mp4");
        await _media.TrimTakeAsync(shortAudio, null, trimmed, 24, 48, 24, _settings, _timeout.Token);
        await AssertVideo(trimmed, 24, 1, audio: true);
        Assert.True(Rms(await Audio(trimmed), .1, .9) < .002);
    }

    [Theory(Skip = OptIn, SkipUnless = nameof(MediaTestsEnabled))]
    [InlineData(false, 24, 25)] [InlineData(true, 3, 37)] [InlineData(false, 0, 30)]
    public async Task TakeTrimEncodesArbitraryFramesAndMatchingAudio(bool audio, int start, int end)
    {
        var source = await Fixture("red", audio ? 2 : null);
        var output = Path.Combine(_directory, "trimmed.mp4");
        await _media.TrimTakeAsync(source, null, output, start, end, 24, _settings, _timeout.Token);
        await AssertVideo(output, end - start, (end - start) / 24d, audio);
        await AssertColor(output, 0, true);
        if (audio) Assert.True(Rms(await Audio(output), .1, .9) > .02);
    }

    [Fact(Skip = OptIn, SkipUnless = nameof(MediaTestsEnabled))]
    public async Task TakeTrimUsesLosslessFrameSequenceInsteadOfCompressedSourcePixels()
    {
        var source = await Fixture("red", 2);
        for (var i = 0; i < 3; i++) {
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(64, 64, new(0, 0, 255));
            image.Save(Path.Combine(_directory, $"frame-{i:D4}.png"), new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        }
        var output = Path.Combine(_directory, "frames.mp4");
        await _media.TrimTakeAsync(source, Path.Combine(_directory, "frame-%04d.png"), output, 20, 23, 24, _settings, _timeout.Token);
        await AssertVideo(output, 3, 3d / 24, true);
        await AssertColor(output, 0, false); await AssertColor(output, 2, false);
    }

    [Fact(Skip = OptIn, SkipUnless = nameof(MediaTestsEnabled))]
    public async Task TakeTrimKeepsVideoEndpointsAndAudioOnTheSameTimeline()
    {
        var red = await Fixture("red", 2); var blue = await Fixture("blue");
        var source = Path.Combine(_directory, "source-timeline.mp4");
        await _media.ExportCutAsync([new(red, 0, 24, 24), new(blue, 0, 24, 24)], source, _settings, _timeout.Token);
        var output = Path.Combine(_directory, "trim-timeline.mp4");
        await _media.TrimTakeAsync(source, null, output, 18, 42, 24, _settings, _timeout.Token);
        await AssertVideo(output, 24, 1, true);
        await AssertColor(output, 0, true); await AssertColor(output, 5, true);
        await AssertColor(output, 6, false); await AssertColor(output, 23, false);
        var audio = await Audio(output);
        Assert.True(Rms(audio, .05, .2) > .02); Assert.True(Rms(audio, .4, .9) < .002);
        using var probe = JsonDocument.Parse(await Run(_settings.Ffprobe, ["-v", "error", "-show_entries", "stream=start_time", "-of", "json", output]));
        foreach (var stream in probe.RootElement.GetProperty("streams").EnumerateArray())
            Assert.Equal(0, double.Parse(stream.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture));
    }

    private async Task<string> Fixture(string color, double? audioSeconds = null, int fps = 24, int width = 64)
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".mp4");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i",
            $"color=c={color}:s={width}x64:r={fps}:d=2" };
        if (audioSeconds is { } seconds)
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={seconds.ToString(CultureInfo.InvariantCulture)}"]);
        args.AddRange(["-map", "0:v:0", "-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        if (audioSeconds is not null) args.AddRange(["-map", "1:a:0", "-c:a", "aac"]);
        args.Add(file);
        await Run(_settings.Ffmpeg, args);
        return file;
    }
    private async Task AssertVideo(string output, int frames, double duration, bool audio)
    {
        var info = await _media.VideoInfoAsync(output, _settings, _timeout.Token);
        Assert.Equal(frames, info.Frames); Assert.Equal(audio, info.HasAudio);
        var bytes = await Run(_settings.Ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "json", output]);
        using var json = JsonDocument.Parse(bytes);
        var measured = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Assert.InRange(measured, duration - .005, duration + .005);
    }
    private Task<byte[]> Audio(string output) => Run(_settings.Ffmpeg,
        ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", output, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]);
    private static double Rms(byte[] bytes, double start, double end)
    {
        var first = (int)(start * 48000); var last = (int)(end * 48000);
        Assert.True(bytes.Length >= last * 4, "Audio ended before the checked interval.");
        double squares = 0;
        for (var i = first; i < last; i++) { var sample = BitConverter.ToSingle(bytes, i * 4); squares += sample * sample; }
        return Math.Sqrt(squares / (last - first));
    }
    private async Task AssertColor(string output, int frame, bool red)
    {
        var rgb = await Run(_settings.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", output,
            "-vf", $"select=eq(n\\,{frame}),scale=1:1", "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
        Assert.Equal(3, rgb.Length);
        Assert.True(red ? rgb[0] > 180 && rgb[2] < 40 : rgb[2] > 150 && rgb[0] < 40,
            $"Unexpected output color at frame {frame}: {string.Join(",", rgb)}.");
    }
    private async Task<byte[]> Run(string executable, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var output = new MemoryStream();
        var error = process.StandardError.ReadToEndAsync();
        var read = process.StandardOutput.BaseStream.CopyToAsync(output, _timeout.Token);
        try
        {
            await Task.WhenAll(read, process.WaitForExitAsync(_timeout.Token));
            var diagnostic = await error;
            Assert.True(process.ExitCode == 0, $"{executable} exited with {process.ExitCode}: {diagnostic}");
            return output.ToArray();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            try { await read; } catch (OperationCanceledException) { }
            await error;
        }
    }
    public void Dispose()
    {
        _timeout.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
