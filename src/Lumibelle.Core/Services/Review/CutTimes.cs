using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Review;

/// <summary>Turns a time in the exported cut, as a director's note gives it, into the shot and take frame it shows.</summary>
public static class CutTimes
{
    /// <summary>Seconds from "234.5", "3:54", "3:54.2" or "1:03:54".</summary>
    public static double Parse(string time)
    {
        var parts = (time ?? "").Trim().Split(':');
        if (parts.Length is < 1 or > 3 || parts.Any(string.IsNullOrWhiteSpace)) throw Invalid();
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0) throw Invalid();
            seconds = seconds * 60 + value;
        }
        return seconds;
        static WorkspaceStoreException Invalid() => new("Give the time as seconds or as minutes:seconds, such as 3:54.");
    }

    public static double Duration(CutDocument cut) => cut.Clips.Sum(c => c.Duration);

    /// <summary>The clip playing at this time, or null when the time is past the end of the cut.</summary>
    public static CutLocation? Locate(CutDocument cut, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return null;
        double start = 0;
        for (var i = 0; i < cut.Clips.Count; i++)
        {
            var clip = cut.Clips[i];
            if (seconds < start + clip.Duration || i == cut.Clips.Count - 1 && seconds <= start + clip.Duration)
            {
                var into = seconds - start;
                var frame = Math.Clamp(clip.StartFrame + (int)Math.Floor(into * clip.Fps), clip.StartFrame, clip.EndFrameExclusive - 1);
                return new(i, clip, into, frame, start);
            }
            start += clip.Duration;
        }
        return null;
    }

    public static string Format(double seconds)
    {
        // Rounded to tenths as a whole, so 59.96 carries into the next minute instead of reading 0:59 and a tenth of 1.0.
        var tenths = (long)Math.Round(Math.Max(0, seconds) * 10, MidpointRounding.AwayFromZero);
        var whole = tenths / 10;
        return $"{whole / 60}:{whole % 60:00}" + (tenths % 10 is > 0 and var tenth ? "." + tenth.ToString(CultureInfo.InvariantCulture) : "");
    }
}
