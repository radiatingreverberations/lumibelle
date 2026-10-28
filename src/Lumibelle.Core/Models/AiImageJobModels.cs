namespace lumibelle.Models;

// Prepared PNGs are captured once with their exact source identities and crops.
// They belong to the job, independently of mutable sidebar controls and asset notes.
public sealed record AiImageInput(AssetImageReference Reference, ImageCropRegion? Crop, AssetLookContext Context, byte[] Png);
public sealed record AiImageJobRequest(int Version, Guid BatchId, Guid ProjectId, Guid AssetId, long SourceRevision,
    AiSettings Settings, string Profile, ReferenceGenerationRequest? Create, ReferenceEditRequest? Edit,
    IReadOnlyList<AiImageInput> Inputs, IReadOnlyList<AppliedLora> AppliedLoras)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CodexCapture? Codex { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalPrompt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RegionalImageCapture? Regional { get; init; }
    public ImageWorkflow Workflow => Settings.DefaultImageWorkflow;
    public string Prompt => Create?.Prompt ?? Edit!.Prompt;
    public string AspectRatio => Create?.AspectRatio ?? Edit!.AspectRatio;
    public AssetLookContext? Look => Create?.Look ?? Edit?.Look;
    public IReadOnlyList<string> Tags => Create?.Tags ?? Edit!.Tags;
    public IReadOnlyList<LoraSelection> Loras => Create?.Loras ?? Edit!.Loras;
}
public sealed record AiImageStaging(Guid CandidateId, string FileName, byte[] Bytes, AssetGenerationMetadata Metadata);
public sealed record AiImageCandidateResult(Guid CandidateId, int Number, Guid ImageId, AssetGenerationMetadata Metadata);
public sealed record AiImageJobResult(IReadOnlyList<AiImageCandidateResult> Candidates, string? Raw = null);
