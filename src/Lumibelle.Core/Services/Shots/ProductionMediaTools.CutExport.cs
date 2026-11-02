using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed partial class ProductionMediaTools
{
    public async Task ExportCutAsync(IReadOnlyList<CutExportSegment> segments, string target, H3Settings settings, CancellationToken ct)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sources = new Dictionary<string, VideoFileInfo>(comparer);
        foreach (var source in segments.Select(s => s.Source).Distinct(comparer))
            sources[source] = await VideoInfoAsync(source, settings, ct).ConfigureAwait(false);
        var plan = CutExportPlan.Create(segments, sources, target);
        await RunCutEncoderAsync(settings.Ffmpeg, plan.Arguments, plan.FilterGraph, ct).ConfigureAwait(false);
    }

    // Full cuts are not short preview utilities. Their lifetime is controlled by the
    // caller (Cancel, navigation or application shutdown), not Run's two-minute limit.
    private static async Task RunCutEncoderAsync(string executable, IReadOnlyList<string> arguments, string filterGraph, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var script = Path.Combine(Path.GetTempPath(), "lumibelle-cut-" + Guid.NewGuid().ToString("N") + ".filter");
        try
        {
            await File.WriteAllTextAsync(script, filterGraph, new UTF8Encoding(false), ct).ConfigureAwait(false);
            var run = arguments.Select(a => a.Replace("{filter-script}", script, StringComparison.Ordinal)).ToArray();
            var (exit, error) = await RunEncoderOnceAsync(executable, run, ct).ConfigureAwait(false);
            // FFmpeg before 7.0 has no "-/option" file syntax: ask once more with the option it does know.
            if (exit != 0 && CutExportPlan.RejectsFilterScriptOption(error) && run.Contains(CutExportPlan.FilterScriptOption))
                (exit, error) = await RunEncoderOnceAsync(executable,
                    run.Select(a => a == CutExportPlan.FilterScriptOption ? CutExportPlan.LegacyFilterScriptOption : a).ToArray(), ct).ConfigureAwait(false);
            if (exit != 0) throw new WorkspaceStoreException($"Cut export failed (FFmpeg exit {exit}). {error.Trim()}");
        }
        finally
        {
            try { File.Delete(script); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<(int Exit, string Error)> RunEncoderOnceAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception e)
        {
            throw new WorkspaceStoreException("FFmpeg could not be started. Check its executable path in Video models.", e);
        }
        var diagnostic = ReadErrorTailAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var error = await diagnostic.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return (process.ExitCode, error);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            // Observe the redirected stream even after cancellation; do not leave an
            // unobserved read task holding a pipe or replace a useful renderer error.
            await diagnostic.ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadErrorTailAsync(StreamReader reader)
    {
        const int maximum = 4096;
        var tail = new StringBuilder(maximum);
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > maximum) tail.Remove(0, tail.Length - maximum);
        }
        return tail.ToString();
    }
}
