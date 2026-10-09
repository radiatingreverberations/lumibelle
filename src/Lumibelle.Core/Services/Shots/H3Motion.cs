using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class H3Motion
{
    public const string Profile = "h3-motion-stock-v1";
    public const string SourceFolder = "extension-source";
    public const int ContextFrames = 39;
    public static int VideoSteps(int frames) => frames <= 5 ? 2 : checked((frames - 5) / 17 * 5 + 2);
    public static int AudioBoundary(int frame) => checked((int)Math.Round(frame * 40d / 24));
    public static int GenerationFrames(int context, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds is < 1 or > 15 || context is not (1 or 5 or 22 or 39)) throw new WorkspaceStoreException("Choose 1–15 added seconds.");
        var requested = checked(context + (int)Math.Ceiling(seconds * 24));
        var frames = requested + (5 - requested % 17 + 17) % 17;
        if (frames > 362) throw new WorkspaceStoreException($"Choose at most {((362 - context) / 24d):0.###} added seconds with this motion window.");
        return frames;
    }
    public static (TakeSegment Segment, int LocalEnd) Tail(ShotTake take, int end)
    {
        TakeTrimming.Range(0, end, take.FrameCount);
        if (take.Composition is null) return (new(take.Id, take, 0, take.FrameCount), end);
        var cursor = 0;
        foreach (var segment in take.Composition.Segments) {
            var length = segment.EndFrameExclusive - segment.StartFrame;
            if (end <= cursor + length) return (segment, segment.StartFrame + end - cursor);
            cursor += length;
        }
        throw new WorkspaceStoreException("The selected frame is unavailable.");
    }
    public static bool CanUseLatents(ShotTake take, int end)
    {
        var (segment, localEnd) = Tail(take, end);
        var rawEnd = (segment.Source.Trim?.SourceStartFrame ?? 0) + localEnd;
        return segment.Source.RefinementPackage is { } package && package.Width == take.Width && package.Height == take.Height &&
            localEnd - segment.StartFrame >= ContextFrames && rawEnd % 17 == 5;
    }
    public static (TakeSegment Segment, int LocalStart) Head(ShotTake take, int start)
    {
        TakeTrimming.Range(start, take.FrameCount, take.FrameCount);
        var (segment, localEnd) = Tail(take, start + 1);
        return (segment, localEnd - 1);
    }
    public static TakeSegment GeneratedSegment(ShotTake take) => take.Composition!.GeneratedSegmentKey is { } key
        ? take.Composition.Segments.Single(s => s.Key == key) : take.Composition.Segments[^1];
    public static bool CanUseLeadingLatents(ShotTake take, int start)
    {
        var (segment, localStart) = Head(take, start);
        var rawStart = (segment.Source.Trim?.SourceStartFrame ?? 0) + localStart;
        return segment.Source.RefinementPackage is { } package && package.Width == take.Width && package.Height == take.Height &&
            segment.EndFrameExclusive - localStart >= ContextFrames && rawStart % 17 == 0;
    }
    public static int? AlignedStart(ShotTake take, int start)
    {
        var (segment, localStart) = Head(take, start);
        if (segment.Source.RefinementPackage is not { } package || package.Width != take.Width || package.Height != take.Height) return null;
        var rawStart = (segment.Source.Trim?.SourceStartFrame ?? 0) + localStart;
        var delta = (17 - rawStart % 17) % 17;
        return segment.EndFrameExclusive - localStart - delta >= ContextFrames ? start + delta : null;
    }
    public static int? AlignedEnd(ShotTake take, int end)
    {
        var (segment, localEnd) = Tail(take, end);
        if (segment.Source.RefinementPackage is not { } package || package.Width != take.Width || package.Height != take.Height) return null;
        var offset = segment.Source.Trim?.SourceStartFrame ?? 0;
        var delta = ((offset + localEnd - 5) % 17 + 17) % 17;
        var aligned = end - delta;
        return aligned > 0 && localEnd - delta - segment.StartFrame >= ContextFrames ? aligned : null;
    }
    public static int Window(int available) => available >= 39 ? 39 : available >= 22 ? 22 : available >= 5 ? 5 : 1;
    public static Guid OutputId(Guid fullTakeId) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fullTakeId + ":extension"))[..16]);
    public static void Validate(TakeMotionContext motion, VideoSnapshot snapshot)
    {
        if (motion.SourceTakeId == Guid.Empty || !Enum.IsDefined(motion.Route) || !Enum.IsDefined(motion.Direction) || motion.StartFrame < 0 || motion.EndFrameExclusive - motion.StartFrame != motion.Frames ||
            motion.Frames is not (1 or 5 or 22 or 39) || motion.GenerationFrames != snapshot.FrameCount || motion.GenerationFrames <= motion.Frames || motion.GenerationFrames > 362 ||
            motion.GenerationFrames % 17 != 5 || motion.Route == MotionContextRoute.SavedLatents && motion.Frames != 39 ||
            motion.Route == MotionContextRoute.SingleFrame && motion.Frames != 1 || motion.Files is null || motion.Files.Count == 0 ||
            motion.Files.Any(f => f.FileName != Path.GetFileName(f.FileName) || f.FileName.Contains('\\') || f.Bytes <= 0 || !RefinementPolicy.Hash(f.Sha256)) ||
            motion.Files.DistinctBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).Count() != motion.Files.Count)
            throw new WorkspaceStoreException("Invalid captured motion context.");
        var names = motion.Files.Select(f => f.FileName).ToHashSet(StringComparer.Ordinal);
        var required = motion.Route == MotionContextRoute.SavedLatents
            ? new[] { "motion-video.latent", "motion-audio.latent", "motion-audio-mask.png" }.ToHashSet(StringComparer.Ordinal)
            : Enumerable.Range(0, motion.Frames).Select(i => $"motion-frame-{i:D2}.png").ToHashSet(StringComparer.Ordinal);
        if (motion.Route != MotionContextRoute.SavedLatents) names.Remove("motion-audio.wav");
        if (!names.SetEquals(required)) throw new WorkspaceStoreException("The captured motion files do not match the selected route.");
    }
    public static string Label(MotionContextRoute route) => route switch { MotionContextRoute.SavedLatents => "Saved motion", MotionContextRoute.Frames => "Motion from frames · re-encoded context", _ => "Single-frame continuation · re-encoded context" };
}
