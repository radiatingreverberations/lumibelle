using System.Globalization;
using System.Text;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed record CutExportSegment(string Source, int StartFrame, int EndFrameExclusive, double Fps)
{
    public double Start => StartFrame / Fps;
    public double Duration => (EndFrameExclusive - StartFrame) / Fps;
}

// Pure command construction: test the media contract without starting a process.
public sealed record CutExportPlan(IReadOnlyList<string> Arguments, long Frames, double Fps, bool HasAudio, string FilterGraph)
{
    public double Duration => Frames / Fps;

    public static CutExportPlan Create(IReadOnlyList<CutExportSegment> segments,
        IReadOnlyDictionary<string, VideoFileInfo> sources, string target)
    {
        const int WindowsCommandLineCharacters = 32767;
        if (segments.Count == 0)
            throw new WorkspaceStoreException("Add at least one available take to the cut before exporting.");
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Source) || !double.IsFinite(segment.Fps) || segment.Fps <= 0 ||
                segment.StartFrame < 0 || segment.EndFrameExclusive <= segment.StartFrame ||
                !sources.TryGetValue(segment.Source, out var info) || segment.EndFrameExclusive > info.Frames ||
                info.Width < 2 || info.Height < 2 || !double.IsFinite(info.Fps) || Math.Abs(info.Fps - segment.Fps) > .001)
                throw new WorkspaceStoreException("An export clip no longer matches its media or frame range.");
        }
        var first = sources[segments[0].Source];
        var fps = segments[0].Fps;
        // One explicit CFR output grid. Mixed-rate clips round to its nearest frame.
        var width = first.Width + first.Width % 2;
        var height = first.Height + first.Height % 2;
        var hasAudio = segments.Any(s => sources[s.Source].HasAudio);
        var filter = new StringBuilder();
        long totalFrames = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var frames = Math.Max(1, checked((long)Math.Round(segment.Duration * fps, MidpointRounding.AwayFromZero)));
            totalFrames = checked(totalFrames + frames);
            var duration = frames / fps;
            filter.Append($"[{i}:v:0]trim=start_frame={segment.StartFrame}:end_frame={segment.EndFrameExclusive}," +
                $"setpts=PTS-STARTPTS,fps=fps={N(fps)}:start_time=0," +
                $"tpad=stop_mode=clone:stop_duration={N(1 / fps)},trim=end_frame={frames}," +
                $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2," +
                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,settb=AVTB,setpts=N/({N(fps)}*TB)[v{i}];");
            if (!hasAudio) continue;
            if (sources[segment.Source].HasAudio)
            {
                filter.Append($"[{i}:a:0]asetpts=PTS-STARTPTS,atrim=start={N(segment.Start)}:end={N(segment.Start + segment.Duration)}," +
                    "asetpts=PTS-STARTPTS,aresample=48000:async=1:first_pts=0," +
                    "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,apad,");
            }
            else filter.Append("anullsrc=r=48000:cl=stereo,");
            filter.Append($"atrim=duration={N(duration)},asetpts=N/SR/TB[a{i}];");
        }
        for (var i = 0; i < segments.Count; i++)
        {
            filter.Append($"[v{i}]");
            if (hasAudio) filter.Append($"[a{i}]");
        }
        filter.Append($"concat=n={segments.Count}:v=1:a={(hasAudio ? 1 : 0)}[outv]");
        if (hasAudio) filter.Append("[outa]");

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-filter_complex_threads", "1" };
        foreach (var segment in segments) args.AddRange(["-i", segment.Source]);
        var filterText = filter.ToString();
        // ProcessStartInfo still uses Windows' command-line length limit even when the
        // path is not shell-quoted. Long cuts move the graph to a UTF-8 filter script.
        var inline = filterText.Length + args.Sum(a => a.Length) + args.Count + 1 <= WindowsCommandLineCharacters;
        args.AddRange(inline ? ["-filter_complex", filterText] : [FilterScriptOption, "{filter-script}"]);
        args.AddRange(["-map", "[outv]"]);
        if (hasAudio) args.AddRange(["-map", "[outa]", "-c:a", "aac", "-b:a", "192k"]);
        else args.Add("-an");
        args.AddRange(["-c:v", "libx264", "-crf", "18", "-preset", "fast", "-pix_fmt", "yuv420p",
            "-r", N(fps), "-fps_mode", "cfr", "-t", N(totalFrames / fps), "-map_metadata", "-1", "-movflags", "+faststart", target]);
        return new(args, totalFrames, fps, hasAudio, filterText);
    }

    // FFmpeg 7.0 reads any option's value from a file with "-/option"; FFmpeg 8 removed the older
    // -filter_complex_script, which earlier releases still need. The runner retries with it once.
    public const string FilterScriptOption = "-/filter_complex";
    public const string LegacyFilterScriptOption = "-filter_complex_script";
    internal static bool RejectsFilterScriptOption(string error) => error.Contains("Unrecognized option '/filter_complex'", StringComparison.Ordinal);

    private static string N(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
}
