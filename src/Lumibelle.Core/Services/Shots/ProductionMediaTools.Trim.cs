using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed partial class ProductionMediaTools
{
    public async Task TrimTakeAsync(string source, string? framePattern, string target, int start, int end, double fps, H3Settings settings, CancellationToken ct)
    {
        var info = await VideoInfoAsync(source, settings, ct);
        TakeTrimming.Range(start, end, info.Frames);
        string N(double n) => n.ToString("0.############", CultureInfo.InvariantCulture);
        List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1"];
        if (framePattern is not null) args.AddRange(["-framerate", N(fps), "-i", framePattern]);
        args.AddRange(["-i", source]);
        var video = framePattern is null ? $"[0:v]trim=start_frame={start}:end_frame={end},setpts=PTS-STARTPTS[v]" : "[0:v]setpts=PTS-STARTPTS[v]";
        var audio = info.HasAudio ? $";[{(framePattern is null ? 0 : 1)}:a]atrim=start={N(start / fps)}:end={N(end / fps)},asetpts=PTS-STARTPTS,apad,atrim=duration={N((end - start) / fps)}[a]" : "";
        args.AddRange(["-filter_complex_threads", "1", "-filter_complex", video + audio, "-map", "[v]"]);
        if (info.HasAudio) args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "192k"]);
        else args.Add("-an");
        args.AddRange(["-frames:v", (end - start).ToString(CultureInfo.InvariantCulture), "-t", N((end - start) / fps),
            "-r", N(fps), "-fps_mode", "cfr", "-c:v", "libx264", "-crf", "16", "-preset", "fast", "-pix_fmt", "yuv420p", "-threads", "1", "-map_metadata", "-1", "-movflags", "+faststart", target]);
        await Run(settings.Ffmpeg, args, ct);
        var output = await VideoInfoAsync(target, settings, ct);
        if (output.Frames != end - start || output.Width != info.Width || output.Height != info.Height || Math.Abs(output.Fps - fps) > .001 || output.HasAudio != info.HasAudio)
            throw new WorkspaceStoreException("The trimmed video does not match the selected frames and audio.");
    }
}
