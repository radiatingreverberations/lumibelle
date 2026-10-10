namespace lumibelle.Models;

public enum MotionContextRoute { SavedLatents, Frames, SingleFrame }
public enum TakeExtensionDirection { After, Before }
public sealed record CapturedMotionFile(string FileName, long Bytes, string Sha256);
public sealed record TakeMotionContext(Guid SourceTakeId, MotionContextRoute Route, int StartFrame, int EndFrameExclusive,
    int Frames, int GenerationFrames, IReadOnlyList<CapturedMotionFile> Files)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public TakeExtensionDirection Direction { get; init; }
}
public sealed record TakeSegment(Guid Key, ShotTake Source, int StartFrame, int EndFrameExclusive)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long GenerationOrder { get; init; }
}
public sealed record TakeComposition(IReadOnlyList<TakeSegment> Segments, int JoinFrame)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasJoinPreview { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public Guid? GeneratedSegmentKey { get; init; }
}
public sealed record TakeExtensionRequest(ShotTake Source, int RetainedFrames, bool Combine,
    IReadOnlyList<CapturedMotionFile> SourceFiles)
{
    public int PrefixStartFrame { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReplayLastSegment { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public TakeExtensionDirection Direction { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public Guid? ReplacementSegmentKey { get; init; }
}
public sealed record TakeExtensionOptions(Guid SourceTakeId, int EndFrameExclusive, double AddedSeconds,
    string Action, IReadOnlyList<ShotDialogue> Dialogue, string? Prompt, bool Combine = true,
    Guid? DestinationShotId = null, bool SaveLosslessFrames = false)
{
    public TakeExtensionDirection Direction { get; init; }
    public int StartFrame { get; init; }
    public TakeExtensionReferences? References { get; init; }
}
// An extension-local draft. Null on the options means reuse the take's exact captured inputs.
public sealed record TakeExtensionReferences(Shot Inputs, IReadOnlyList<ShotReferenceGuidance> Guidance,
    IReadOnlyList<ShotAppearanceContext> Appearances);
public sealed record TakeAssemblyMedia(string Source, string? FramePattern, int StartFrame, int EndFrameExclusive, double Fps);
public sealed record TakeExtensionPublication(Guid ResultId, Guid FullTakeId, string Fingerprint);
