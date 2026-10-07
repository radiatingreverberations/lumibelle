using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed record VideoFileInfo(int Width, int Height, int Frames, double Fps, bool HasAudio)
{
    public double? Duration { get; init; }
}
public interface IProductionMediaTools
{
    Task ExportCutAsync(IReadOnlyList<CutExportSegment> segments, string target, H3Settings settings, CancellationToken ct)
        => throw new WorkspaceStoreException("Cut export is unavailable.");
    // Original presentation timestamps, including a nonzero first-frame timestamp.
    Task<IReadOnlyList<double>> ReelFrameTimesAsync(string source, H3Settings settings, CancellationToken ct)
        => throw new WorkspaceStoreException("Reel frame inspection is unavailable.");
    Task ExtractReelFramesAsync(string source, IReadOnlyList<int> indices, string directory, int maximumEdge, H3Settings settings, CancellationToken ct)
        => throw new WorkspaceStoreException("Reel frame extraction is unavailable.");
    Task PrepareVideoThumbnailAsync(string source, string target, H3Settings settings, CancellationToken ct)
        => throw new WorkspaceStoreException("Video thumbnails are unavailable.");
    Task PrepareReferenceVideoAsync(string source, string video, string? audio, ReferenceVideoMedia media, int frames, H3Settings settings, CancellationToken ct)
        => throw new WorkspaceStoreException("Reference video preparation is unavailable.");
    Task<byte[]> ExtractFrameAsync(string path, int index, int width, int height, H3Settings settings, CancellationToken ct)
        => throw new WorkspaceStoreException("MP4 frame extraction is unavailable.");
    Task<double> AudioDurationAsync(string path, H3Settings settings, CancellationToken ct);
    Task PrepareVoiceAsync(string source, string target, double start, double duration, H3Settings settings, CancellationToken ct);
    Task<VideoFileInfo> VideoInfoAsync(string path, H3Settings settings, CancellationToken ct);
}
public sealed partial class ProductionMediaTools : IProductionMediaTools
{
    public async Task PrepareVideoThumbnailAsync(string source, string target, H3Settings settings, CancellationToken ct)
    {
        // Scale after autorotation, preserving display aspect and bounding both dimensions.
        const string scale = "scale=w='max(1,round(iw*sar*min(1,min(480/(iw*sar),320/ih))))':h='max(1,round(ih*min(1,min(480/(iw*sar),320/ih))))',setsar=1";
        await Run(settings.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1", "-ss", "0.25", "-i", source,
            "-map", "0:v:0", "-an", "-vf", scale, "-frames:v", "1", "-map_metadata", "-1", "-threads", "1", "-c:v", "mjpeg", "-q:v", "3", "-f", "image2", "-update", "1", target], ct);
    }
    public async Task PrepareReferenceVideoAsync(string source, string video, string? audio, ReferenceVideoMedia media, int frames, H3Settings settings, CancellationToken ct)
    {
        lumibelle.Services.Production.ReferenceVideos.ValidateMedia(media);
        if (frames < 5 || frames > 362 || frames % 17 != 5) throw new WorkspaceStoreException("Invalid reference frame count.");
        // Evaluate after FFmpeg's autorotation and account for non-square source pixels.
        const string scale = "scale=w='max(2,trunc(iw*sar*min(1,sqrt(1032192/(iw*sar*ih)))/2)*2)':h='max(2,trunc(ih*min(1,sqrt(1032192/(iw*sar*ih)))/2)*2)',setsar=1";
        await Run(settings.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", source, "-map", "0:v:0", "-an",
            "-vf", $"fps=24:start_time=0,{scale}", "-frames:v", frames.ToString(CultureInfo.InvariantCulture), "-map_metadata", "-1",
            "-c:v", "libx264", "-crf", "18", "-preset", "fast", "-pix_fmt", "yuv420p", "-threads", "1", "-movflags", "+faststart", video], ct);
        var prepared = await VideoInfoAsync(video, settings, ct);
        if (prepared.Frames != frames || Math.Abs(prepared.Fps - 24) > .001 || prepared.HasAudio)
            throw new WorkspaceStoreException("The reference could not be prepared as the expected 24 fps clip.");
        if (audio is not null)
        {
            if (!media.HasAudio) throw new WorkspaceStoreException("The reference video has no soundtrack.");
            await Run(settings.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", source, "-map", "0:a:0", "-vn",
                "-af", "aresample=async=1:first_pts=0,apad", "-t", Number(frames / 24d), "-map_metadata", "-1", "-ar", "32000", "-ac", "2", "-c:a", "pcm_s16le", audio], ct);
        }
    }
    public async Task<byte[]> ExtractFrameAsync(string path, int index, int width, int height, H3Settings settings, CancellationToken ct)
    {
        if (index is < 0 or >= 362) throw new WorkspaceStoreException("Choose an available frame.");
        var info = new ProcessStartInfo(settings.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-i", path, "-map", "0:v:0", "-vf",
            $"select=eq(n\\,{index})", "-frames:v", "1", "-fps_mode", "passthrough", "-map_metadata", "-1", "-threads", "1", "-f", "image2pipe", "-c:v", "png", "pipe:1" }) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            process.Start(); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            using var bytes = new MemoryStream(); var buffer = new byte[65536]; int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (bytes.Length + read > 64 * 1024 * 1024) throw new WorkspaceStoreException("The extracted frame is too large.");
                await bytes.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            await process.WaitForExitAsync(timeout.Token); await error;
            if (process.ExitCode != 0 || bytes.Length == 0) throw new WorkspaceStoreException("The MP4 frame could not be extracted. Check the video and FFmpeg setup.");
            bytes.Position = 0;
            var image = await SixLabors.ImageSharp.Image.IdentifyAsync(bytes, timeout.Token);
            if (image.Width != width || image.Height != height) throw new WorkspaceStoreException("The extracted frame dimensions do not match this take.");
            return bytes.ToArray();
        }
        catch (System.ComponentModel.Win32Exception e) { throw new WorkspaceStoreException("FFmpeg could not be started. Check its path in Video models.", e); }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }
    private static async Task<string> Run(string executable, IEnumerable<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token); var output = await stdout; await stderr;
            if (process.ExitCode != 0) throw new WorkspaceStoreException("The media could not be decoded. Check the file and FFmpeg setup in Video models.");
            return output;
        }
        catch (System.ComponentModel.Win32Exception e) { throw new WorkspaceStoreException("FFmpeg/ffprobe could not be started. Set their executable paths in AI settings → Video models.", e); }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }
    private static string Number(double value) => value.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);
    public Task<string> PrepareAudioPreviewAsync(string source, string target, H3Settings settings, CancellationToken ct) =>
        Run(settings.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", source, "-map", "0:a:0", "-vn", "-map_metadata", "-1", "-ar", "32000", "-ac", "2", "-c:a", "libmp3lame", "-b:a", "128k", target], ct);
    public async Task<double> AudioDurationAsync(string path, H3Settings settings, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await Run(settings.Ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], ct));
        if (!json.RootElement.GetProperty("streams").EnumerateArray().Any(s => s.GetProperty("codec_type").GetString() == "audio") ||
            !double.TryParse(json.RootElement.GetProperty("format").GetProperty("duration").GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 1 || seconds > 3600) throw new WorkspaceStoreException("Choose readable audio from 1 second to 1 hour long.");
        await Run(settings.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-xerror", "-i", path, "-map", "0:a:0", "-f", "null", "-"], ct);
        return seconds;
    }
    public Task ExtractReelAudioAsync(string source, string target, H3Settings settings, CancellationToken ct) =>
        Run(settings.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", source, "-map", "0:a:0", "-vn", "-map_metadata", "-1", "-c:a", "pcm_s24le", "-fflags", "+bitexact", target], ct);
    public async Task PrepareVoiceAsync(string source, string target, double start, double duration, H3Settings settings, CancellationToken ct)
    {
        var total = await AudioDurationAsync(source, settings, ct);
        if (!double.IsFinite(start) || start < 0 || !double.IsFinite(duration) || duration is < 1 or > 15 || start + duration > total + .01)
            throw new WorkspaceStoreException("Choose a 1–15 second excerpt within this voice recording.");
        await Run(settings.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", source, "-ss", Number(start), "-t", Number(duration), "-map", "0:a:0", "-vn", "-map_metadata", "-1", "-ar", "32000", "-ac", "2", "-c:a", "pcm_s16le", "-fflags", "+bitexact", target], ct);
    }
    public async Task<VideoFileInfo> VideoInfoAsync(string path, H3Settings settings, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await Run(settings.Ffprobe, ["-v", "error", "-count_frames", "-show_streams", "-show_format", "-of", "json", path], ct));
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new WorkspaceStoreException("ComfyUI returned a file without video.");
        var rate = video.GetProperty("r_frame_rate").GetString()!.Split('/');
        return new(video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(), int.Parse(video.GetProperty("nb_read_frames").GetString()!),
            double.Parse(rate[0], System.Globalization.CultureInfo.InvariantCulture) / double.Parse(rate[1], System.Globalization.CultureInfo.InvariantCulture),
            streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"))
            { Duration = video.TryGetProperty("duration", out var duration) &&
                double.TryParse(duration.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : null };
    }
}
