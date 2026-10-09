using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed partial class ProductionMediaTools
{
    public async Task AssembleTakeAsync(IReadOnlyList<TakeAssemblyMedia> segments, string target, H3Settings settings, CancellationToken ct)
    {
        if (segments.Count == 0) throw new WorkspaceStoreException("The extension has no visible frames.");
        var infos = new List<VideoFileInfo>();
        foreach (var segment in segments) {
            var info = await VideoInfoAsync(segment.Source, settings, ct);
            TakeTrimming.Range(segment.StartFrame, segment.EndFrameExclusive, info.Frames);
            if (info.Width != infos.FirstOrDefault()?.Width && infos.Count > 0 || info.Height != infos.FirstOrDefault()?.Height && infos.Count > 0 || Math.Abs(info.Fps - segment.Fps) > .001)
                throw new WorkspaceStoreException("Extension segments must have matching dimensions and frame rates.");
            infos.Add(info);
        }
        string N(double n) => n.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
        List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1"];
        var filters = new List<string>(); var nextInput = 0; var audio = infos.Any(i => i.HasAudio);
        for (var i = 0; i < segments.Count; i++) {
            var s = segments[i]; var media = nextInput++; args.AddRange(["-i", s.Source]);
            var video = media;
            if (s.FramePattern is not null) { video = nextInput++; args.AddRange(["-framerate", N(s.Fps), "-i", s.FramePattern]); }
            filters.Add(s.FramePattern is null
                ? $"[{video}:v]trim=start_frame={s.StartFrame}:end_frame={s.EndFrameExclusive},setpts=PTS-STARTPTS,setsar=1[v{i}]"
                : $"[{video}:v]trim=end_frame={s.EndFrameExclusive - s.StartFrame},setpts=PTS-STARTPTS,setsar=1[v{i}]");
            if (audio) filters.Add(infos[i].HasAudio
                ? $"[{media}:a]atrim=start={N(s.StartFrame / s.Fps)}:end={N(s.EndFrameExclusive / s.Fps)},asetpts=PTS-STARTPTS,aresample=48000,aformat=channel_layouts=stereo,apad,atrim=duration={N((s.EndFrameExclusive - s.StartFrame) / s.Fps)}[a{i}]"
                : $"anullsrc=r=48000:cl=stereo,atrim=duration={N((s.EndFrameExclusive - s.StartFrame) / s.Fps)},asetpts=PTS-STARTPTS[a{i}]");
        }
        filters.Add(string.Concat(Enumerable.Range(0, segments.Count).Select(i => $"[v{i}]" + (audio ? $"[a{i}]" : ""))) + $"concat=n={segments.Count}:v=1:a={(audio ? 1 : 0)}[v]" + (audio ? "[a]" : ""));
        args.AddRange(["-filter_complex_threads", "1", "-filter_complex_script", "{filter-script}", "-map", "[v]"]);
        if (audio) args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "192k"]); else args.Add("-an");
        var count = segments.Sum(s => s.EndFrameExclusive - s.StartFrame); var fps = segments[0].Fps;
        args.AddRange(["-frames:v", count.ToString(), "-t", N(count / fps), "-r", N(fps), "-fps_mode", "cfr", "-c:v", "libx264", "-crf", "16", "-preset", "fast", "-pix_fmt", "yuv420p", "-threads", "1", "-map_metadata", "-1", "-movflags", "+faststart", target]);
        await RunCutEncoderAsync(settings.Ffmpeg, args, string.Join(';', filters), ct);
        var output = await VideoInfoAsync(target, settings, ct);
        if (output.Frames != count || output.Width != infos[0].Width || output.Height != infos[0].Height || Math.Abs(output.Fps - fps) > .001 || output.HasAudio != audio)
            throw new WorkspaceStoreException("The saved extension does not match its frames and soundtrack.");
    }
    public async Task CaptureMotionAudioAsync(string source, string target, int start, int end, double fps, H3Settings settings, CancellationToken ct)
    {
        var info = await VideoInfoAsync(source, settings, ct);
        if (!info.HasAudio) return;
        await Run(settings.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", source, "-map", "0:a:0", "-vn", "-af", $"atrim=start={Number(start / fps)}:end={Number(end / fps)},asetpts=PTS-STARTPTS,apad,atrim=duration={Number((end - start) / fps)}", "-ar", "32000", "-ac", "2", "-c:a", "pcm_s16le", target], ct);
    }
}
