using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

/// <summary>Mean and peak level in dBFS, and the stretches quieter than <see cref="AudioLevels.SilenceDb"/> for at least <see cref="AudioLevels.SilenceSeconds"/>.</summary>
public sealed record AudioLevels(double? MeanDb, double? PeakDb, IReadOnlyList<(double Start, double End)> Silences)
{
    public const int SilenceDb = -50;
    public const double SilenceSeconds = 0.5;

    // FFmpeg's volumedetect and silencedetect report on the log, one value per line.
    public static AudioLevels Parse(string log, double seconds)
    {
        static double? Value(string text, string pattern) => Regex.Match(text, pattern) is { Success: true } m &&
            double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        var silences = new List<(double, double)>(); double? open = null;
        foreach (var line in log.Split('\n'))
        {
            if (Value(line, @"silence_start:\s*(-?[0-9.]+)") is { } start) open = Math.Max(0, start);
            else if (Value(line, @"silence_end:\s*(-?[0-9.]+)") is { } end && open is { } from) { silences.Add((from, end)); open = null; }
        }
        // Silence that runs to the end of the file is opened and never closed.
        if (open is { } tail && seconds > tail) silences.Add((tail, seconds));
        // A digitally silent track reports -91 dB; "-inf" does not parse and is left null.
        return new(Value(log, @"mean_volume:\s*(-?[0-9.]+) dB"), Value(log, @"max_volume:\s*(-?[0-9.]+) dB"), silences);
    }
}

public sealed partial class ProductionMediaTools
{
    public async Task<AudioLevels> AudioLevelsAsync(string path, double seconds, H3Settings settings, CancellationToken ct)
    {
        var info = new ProcessStartInfo(settings.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-nostats", "-nostdin", "-i", path, "-map", "0:a:0", "-vn",
            "-af", $"volumedetect,silencedetect=noise={AudioLevels.SilenceDb}dB:d={AudioLevels.SilenceSeconds.ToString(CultureInfo.InvariantCulture)}", "-f", "null", "-" }) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token); await stdout; var log = await stderr;
            if (process.ExitCode != 0) throw new WorkspaceStoreException("The soundtrack could not be read. Check the file and FFmpeg setup in Video models.");
            return AudioLevels.Parse(log, seconds);
        }
        catch (System.ComponentModel.Win32Exception e) { throw new WorkspaceStoreException("FFmpeg could not be started. Set its executable path in AI settings → Video models.", e); }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }
}
