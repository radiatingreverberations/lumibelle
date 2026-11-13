namespace lumibelle.Models;

// Provenance is informational. Execution uses the independently captured clip and inputs.
public sealed record MotionReferenceSource(Guid TakeId, Guid BindingId, ReferenceVideoMedia Media,
    int VisibleFrames, double Fps, string BasePrompt, string Instructions)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ReelId { get; init; }
    // Keyframes picked on the source reel. When kept, the final saves those frames losslessly and the new reel gets them.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ReelKeyframe>? SourceKeyframes { get; init; }
}

public sealed record MotionReferenceDraft(Shot Shot, MotionReferenceSource Source,
    IReadOnlyList<ShotReferenceGuidance> Guidance, IReadOnlyList<ShotAppearanceContext> Appearances)
{
    public string? SceneFingerprint { get; init; }
    public ReelVideoContext? Reel { get; init; }
}

public sealed record MotionReferenceOptions(MotionReferenceDraft Draft, string BasePrompt, string Instructions,
    VideoResolution Resolution = VideoResolution.Native, long? Seed = null, bool SaveLatents = false,
    bool SaveLosslessFrames = false, bool KeepKeyframes = false)
{
    public string GenerationPreset { get; init; } = "standard";
    // Absent on older batches, which continue to use Standard with the configured models.
    public MotionReferenceGeneration? Generation { get; init; }
}

public sealed record MotionReferenceGeneration(string ComfyUrl, H3Settings Settings, IReadOnlyList<AppliedLora>? AppliedLoras);
