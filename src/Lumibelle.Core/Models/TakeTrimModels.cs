namespace lumibelle.Models;

public sealed record TakeTrimRequest(Guid TakeId, Guid ResultId, int StartFrame, int EndFrameExclusive);
public sealed record TakeTrim(Guid ParentTakeId, int ParentFrameCount, int StartFrame, int EndFrameExclusive,
    int SourceStartFrame, int SourceEndFrameExclusive, bool LosslessFrames);
// Snapshot and Refinement on the take describe this full source. The package is never sliced.
public sealed record RetainedRefinementSource(IReadOnlyList<AiVideoInput> Inputs)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public H3RefinementPackage? RefinementInput { get; init; }
}
public sealed record TakeTrimRange(int StartFrame, int EndFrameExclusive);
