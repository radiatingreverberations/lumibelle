namespace lumibelle.Models;

// Prepared files live with the root batch, independently of the current shot and
// of queue continuations. Content hashes prevent a retry from using altered files.
public sealed record AiVideoInput(string FileName, bool Audio, long Bytes, string Sha256)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public VideoInputKind? Kind { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? VideoIndex { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public VideoInputKind EffectiveKind => Kind ?? (Audio ? VideoInputKind.Audio : VideoInputKind.Image);
}
public sealed record AiVideoJobRequest(int Version, Guid BatchId, VideoSnapshot Snapshot, IReadOnlyList<AiVideoInput> Inputs)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TakeExtensionRequest? Extension { get; init; }
    public TakeRefinement? Refinement { get; init; }
    public TakeTrimRange? OutputTrim { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DestinationShotId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid OutputShotId => DestinationShotId ?? Snapshot.Shot.Id;
}
public sealed record AiVideoCandidateResult(Guid TakeId, int Number, Guid JobId);
public sealed record AiVideoJobResult(IReadOnlyList<AiVideoCandidateResult> Candidates);
