using System.Diagnostics;
using lumibelle.Models;
using lumibelle.Services.Shots;

namespace Lumibelle.Tests;

// Runs the production level measurement on real files. Opt-in like the other media tests: FFmpeg is an external dependency.
public sealed class ReviewMediaTests : IDisposable
{
    private const string OptIn = "Set LUMIBELLE_TEST_FFMPEG=1 and provide FFmpeg/ffprobe to run media integration tests.";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumibelle-review-media-" + Guid.NewGuid().ToString("N"));
    private readonly H3Settings _settings = new()
    {
        Ffmpeg = Environment.GetEnvironmentVariable("LUMIBELLE_FFMPEG") ?? "ffmpeg",
        Ffprobe = Environment.GetEnvironmentVariable("LUMIBELLE_FFPROBE") ?? "ffprobe"
    };
    private readonly ProductionMediaTools _media = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = OptIn, SkipUnless = nameof(CutExportMediaTests.MediaTestsEnabled), SkipType = typeof(CutExportMediaTests))]
    public async Task MeasuresLevelAndFindsTheSilentStretchBeforeATone()
    {
        var file = await Fixture("quiet-then-tone.mp4", "-f", "lavfi", "-i", "anullsrc=r=32000:cl=stereo:d=1.5", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=32000:duration=1.5",
            "-filter_complex", "[1:a][2:a]concat=n=2:v=0:a=1[a]", "-map", "0:v", "-map", "[a]");

        var levels = await _media.AudioLevelsAsync(file, 3, _settings, Ct);

        Assert.InRange(levels.MeanDb!.Value, -40, -5);
        Assert.True(levels.PeakDb > levels.MeanDb);
        var silence = Assert.Single(levels.Silences);
        Assert.InRange(silence.Start, 0, .2);
        Assert.InRange(silence.End, 1.3, 1.7);
    }

    [Fact(Skip = OptIn, SkipUnless = nameof(CutExportMediaTests.MediaTestsEnabled), SkipType = typeof(CutExportMediaTests))]
    public async Task ASilentSoundtrackIsReportedAsSilentThroughout()
    {
        var file = await Fixture("silent.mp4", "-f", "lavfi", "-i", "anullsrc=r=32000:cl=stereo:d=3", "-map", "0:v", "-map", "1:a");

        var levels = await _media.AudioLevelsAsync(file, 3, _settings, Ct);

        Assert.True(levels.MeanDb is null or <= AudioLevels.DigitalSilenceDb, $"mean {levels.MeanDb}");
        var silence = Assert.Single(levels.Silences);
        Assert.InRange(silence.Start, 0, .1);
        Assert.InRange(silence.End, 2.8, 3.1);
        Assert.True((await _media.VideoInfoAsync(file, _settings, Ct)).HasAudio);
    }

    // Needs no FFmpeg: a stand-in that never finishes.
    [Fact]
    public async Task AMeasurementThatRunsTooLongIsAnErrorTheReviewCanShow()
    {
        Directory.CreateDirectory(_directory);
        string stand;
        if (OperatingSystem.IsWindows())
        {
            stand = Path.Combine(_directory, "stalled-ffmpeg.cmd");
            await File.WriteAllTextAsync(stand, "@ping -n 30 127.0.0.1 >nul\r\n", Ct);
        }
        else
        {
            stand = Path.Combine(_directory, "stalled-ffmpeg");
            await File.WriteAllTextAsync(stand, "#!/bin/sh\nsleep 30\n", Ct);
            File.SetUnixFileMode(stand, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var e = await Assert.ThrowsAsync<lumibelle.Services.Story.WorkspaceStoreException>(() =>
            _media.AudioLevelsAsync(Path.Combine(_directory, "take.mp4"), 3, new H3Settings { Ffmpeg = stand }, TimeSpan.FromSeconds(1), Ct));

        Assert.Contains("took longer than 1 seconds", e.Message);
    }

    private async Task<string> Fixture(string name, params string[] audio)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        var info = new ProcessStartInfo(_settings.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "color=size=64x64:rate=24:duration=3" }
            .Concat(audio).Concat(["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path])) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(Ct); _ = process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, await error);
        return path;
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
