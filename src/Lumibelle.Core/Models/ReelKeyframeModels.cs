using System.Text.Json.Serialization;

namespace lumibelle.Models;

public enum ReelVisuals { FullReel, Keyframes, None, RefMod }

// Source is either the original MP4 hash or the immutable lossless archive manifest hash.
public sealed record ReelFrameIdentity(Guid MediaId, string Source, int Index, double Seconds);
public sealed record ReelKeyframe
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required ReelFrameIdentity Frame { get; init; }
    public ImageCropRegion? Crop { get; set; }
    public string Notes { get; set; } = "";
}
public sealed record ReelKeyframeSet
{
    public int Version { get; init; } = 1;
    public List<ReelKeyframe> Frames { get; set; } = [];
}
public sealed record ReelAudioExcerpt(double Start, double Duration);
public sealed record ReelFrameCatalog(string Source, bool Lossless, IReadOnlyList<double> Timestamps)
{
    // Saved identities stay relative to frame zero; browser media times retain the original PTS origin.
    public double PlaybackOrigin { get; init; }
}
public sealed record ReelFrameArchive(string SourceSha256, int Width, int Height, int FrameCount,
    IReadOnlyList<ShotFrame> Frames, IReadOnlyList<ReelArchiveFile> Files);
public sealed record ReelArchiveFile(string FileName, long Bytes, string Sha256);
// Written before a reel's lossless segments are deleted. The index stays, because saved keyframes use
// its hash as their source; the listed keyframe pictures become the only copy of those frames.
public sealed record ReelArchiveRemoval(DateTimeOffset RequestedUtc, IReadOnlyList<ReelArchiveFile> Files, IReadOnlyList<string> Keyframes,
    DateTimeOffset? CompletedUtc = null);
