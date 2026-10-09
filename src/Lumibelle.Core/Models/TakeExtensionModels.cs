namespace lumibelle.Models;

public enum MotionContextRoute { SavedLatents, Frames, SingleFrame }
public sealed record CapturedMotionFile(string FileName, long Bytes, string Sha256);
public sealed record TakeMotionContext(Guid SourceTakeId, MotionContextRoute Route, int StartFrame, int EndFrameExclusive,
    int Frames, int GenerationFrames, IReadOnlyList<CapturedMotionFile> Files);
public sealed record TakeSegment(Guid Key, ShotTake Source, int StartFrame, int EndFrameExclusive);
public sealed record TakeComposition(IReadOnlyList<TakeSegment> Segments, int JoinFrame)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasJoinPreview { get; init; }
}
public sealed record TakeExtensionRequest(ShotTake Source, int RetainedFrames, bool Combine,
    IReadOnlyList<CapturedMotionFile> SourceFiles)
{
    public int PrefixStartFrame { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReplayLastSegment { get; init; }
}
public sealed record TakeExtensionOptions(Guid SourceTakeId, int EndFrameExclusive, double AddedSeconds,
    string Action, IReadOnlyList<ShotDialogue> Dialogue, string? Prompt, bool Combine = true,
    Guid? DestinationShotId = null, bool SaveLosslessFrames = false);
public sealed record TakeAssemblyMedia(string Source, string? FramePattern, int StartFrame, int EndFrameExclusive, double Fps);
public sealed record TakeExtensionPublication(Guid ResultId, Guid FullTakeId, string Fingerprint);
