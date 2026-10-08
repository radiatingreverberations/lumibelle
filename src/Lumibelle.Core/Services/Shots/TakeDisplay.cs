using lumibelle.Models;
using System.Globalization;

namespace lumibelle.Services.Shots;

public static class TakeDisplay
{
    public static string Label(ShotTake take) => (take.Snapshot.Dub is { } dub ? $"[{dub.Target.Name}] " : "") +
        $"{RefinementPolicy.Label(take)} · {take.CreatedUtc.ToLocalTime():g} · {take.Id.ToString()[..8]}";
    public static string Dimensions(ShotTake take) => $"{take.Width} × {take.Height}";
    public static string Badge(ShotTake take) => (take.Snapshot.Dub is { } dub ? dub.Target.Code + " · " : "") + ResolutionBadge(take);
    public static string Megapixels(ShotTake take) => ((long)take.Width * take.Height / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " MP";
    // Quick and Preview sizes are for exploring; a chosen take is regenerated at Detail or Native.
    public static bool IsDraft(ShotTake take) => take.Refinement is null && take.Snapshot.PreviewUpscale is null && (long)take.Width * take.Height < 500_000;
    private static string ResolutionBadge(ShotTake take)
    {
        var mp = Megapixels(take);
        if (take.Refinement is not null) return "Refined · " + mp;
        if (take.Snapshot.PreviewUpscale is not null) return "Upscaled · " + mp;
        return VideoResolutions.Selected(take.Snapshot.Shot) == VideoResolution.Native &&
            (take.Width, take.Height) == VideoResolutions.Size(take.Snapshot.Shot) ? "Native · " + mp : mp;
    }
    public static string Duration(ShotTake take) => (take.FrameCount / take.Fps).ToString("0.###", CultureInfo.InvariantCulture) + " s";
    public const string NoLatents = "This take has no saved latents. To refine it, regenerate it with the same seed and resolution and Save latents on, then refine the new take.";
    public static string? RegenerationIssue(ShotTake take) => take.Snapshot.Dub is not null
        ? "Render this translation again from Language versions; its master take fixes the generation settings and input files."
        : take.Refinement is not null
        ? "Regenerate the original take to reuse its generation seed and inputs."
        : take.AiJobId is null ? "This older take has no captured request. Generate from its setup instead." : null;
}
