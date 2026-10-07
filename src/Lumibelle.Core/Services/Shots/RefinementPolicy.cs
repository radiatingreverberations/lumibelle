using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class RefinementPolicy
{
    public const string Profile = "h3-refinement-stock-v1";
    public static IReadOnlyList<RefinementSize> Sizes(int width, int height)
    {
        if (width < 32 || height < 32 || width % 32 != 0 || height % 32 != 0)
            throw new WorkspaceStoreException("The source take does not use the H3 pixel grid.");
        List<RefinementSize> sizes = [new("Same size", width, height, (long)width * height > 1344 * 768)];
        foreach (var (name, pixels) in new[] { ("Native / approximately 768p", 1344 * 768), ("1080p-class", 1920 * 1080), ("1440p-class", 2560 * 1440) })
        {
            var scale = Math.Sqrt(pixels / ((double)width * height));
            int Align(double value) => Math.Max(32, (int)Math.Round(value / 32, MidpointRounding.AwayFromZero) * 32);
            var w = Align(width * scale); var h = Align(height * scale);
            var issue = w < width || h < height ? "This would downscale the source. Choose Same size or a larger preset."
                : w > width * 4 || h > height * 4 ? "The learned upscaler supports at most 4× per dimension." : null;
            sizes.Add(new(name, w, h, pixels > 1344 * 768, issue));
        }
        return sizes;
    }
    public static RefinementSize DefaultSize(int width, int height) => Sizes(width, height)
        .Skip(1).FirstOrDefault(s => s.Issue is null && s.Pixels > (long)width * height) ?? Sizes(width, height)[0];
    public static void Validate(TakeRefinement r, VideoSnapshot source)
    {
        var p = r.SourcePackage;
        if (r.ParentTakeId == Guid.Empty || p is null || p.Id == Guid.Empty || !Enum.IsDefined(r.Mode) || !Enum.IsDefined(r.Implementation) || r.Profile != Profile ||
            string.IsNullOrWhiteSpace(r.Upscaler) || !r.Upscaler.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) || p.Bytes <= 0 || !Hash(p.Sha256) ||
            p.FrameCount != source.FrameCount || !Sizes(p.Width, p.Height).Any(s => s.Issue is null && s.Width == r.Width && s.Height == r.Height))
            throw new WorkspaceStoreException("Invalid refinement source, size, or profile. Review the saved take again.");
    }
    public static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    public static (int Width, int Height) OutputSize(VideoRun run) => H3PreviewUpscaling.OutputSize(run.Snapshot, run.Refinement);
    public static string Label(ShotTake take) => take.Refinement is { } r
        ? $"{r.Mode} · {take.Width} × {take.Height} · version {take.Candidate}" : $"Take {take.Candidate}";
}
