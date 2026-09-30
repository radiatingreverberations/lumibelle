using System.Text.Json.Serialization;

namespace lumibelle.Models;

public enum ImageWorkflow { Krea2, Flux2Klein9bKv, CodexImages, QwenImage21 }

public static class ImageWorkflowNames
{
    public static string Label(this ImageWorkflow workflow) => workflow switch
    {
        ImageWorkflow.Krea2 => "Krea 2",
        ImageWorkflow.QwenImage21 => "Qwen Image 2.1",
        ImageWorkflow.CodexImages => "Codex Images",
        ImageWorkflow.Flux2Klein9bKv => "FLUX.2 Klein 9B KV",
        _ => "Unknown image workflow"
    };
}

public sealed record AssetImageReference(Guid AssetId, Guid ImageId);
public sealed record AssetReferenceLook(AssetImageReference Reference, AssetLookContext Context);
public sealed record AssetReferenceCrop(AssetImageReference Reference, ImageCropRegion Crop);
public sealed record ReferenceImageSource(Guid AssetId, Guid ImageId, Stream Content);

public enum AssetCategory { Character, Environment, Prop, Reference }
public enum AssetImageOrigin { Imported, Generated, Edited, VideoFrame, Cropped }

public sealed record VideoFrameSource(Guid ProjectId, Guid ShotId, Guid TakeId, int FrameIndex,
    double Timestamp, double Fps, int Width, int Height)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Lossless { get; init; }
}
public sealed record CroppedImageSource(Guid ProjectId, Guid AssetId, Guid ImageId, ImageCropRegion Crop,
    int SourceWidth, int SourceHeight);
public sealed record ReelFrameExport(Guid ReelId, ReelFrameIdentity Frame);
public sealed record ReelImageSource(Guid ProjectId, Guid ReelId, string ReelName, ReelFrameIdentity Frame,
    bool Lossless, int Width, int Height);
public sealed record AssetImageSource(VideoFrameSource? Frame = null, CroppedImageSource? Crop = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReelImageSource? ReelFrame { get; init; }
}
public sealed record ImageDestination(Guid AssetId, Guid? LookId = null, string? NewAssetName = null,
    AssetCategory NewAssetCategory = AssetCategory.Reference);
public sealed record DerivedImageRequest(Guid ImageId, ImageDestination Destination, string Name, string Notes,
    Guid? TakeId = null, int? FrameIndex = null, AssetImageReference? Parent = null, ImageCropRegion? Crop = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReelFrameExport? ReelFrame { get; init; }
}
public sealed record ImageCopyReceipt(Guid ImageId, Guid AssetId, string Fingerprint);
public sealed record ImagePublicationReceipt(Guid JobId, Guid ImageId, Guid AssetId, string Fingerprint);
public sealed record GeneratedImageInput(Guid JobId, Guid ImageId, Guid AssetId, AssetImageInput Image);
public sealed record SavedAssetImage(AssetLibrary Library, Guid AssetId, Guid ImageId, bool Available);

public sealed record AssetSourceEvidence(string Label, Guid? SceneId, string Excerpt, Guid? ApprovedScriptId = null);

public sealed record AssetGenerationMetadata
{
    public int Resolution { get; init; } = 1024;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QwenImage21Output? QwenImage21 { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CodexImageDetails? Codex { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AiBackend? Provider { get; init; }
    public Guid? AiJobId { get; init; }
    public Guid? BatchId { get; init; }
    public int? CandidateNumber { get; init; }
    public AssetLookContext? Look { get; init; }
    public ImageWorkflow Workflow { get; init; } = ImageWorkflow.Krea2;
    public string Prompt { get; init; } = "";
    public long Seed { get; init; }
    public string AspectRatio { get; init; } = "1:1";
    public string DiffusionModel { get; init; } = "";
    public string TextEncoder { get; init; } = "";
    public string Vae { get; init; } = "";
    public int Steps { get; init; } = 8;
    public IReadOnlyList<AppliedLora> Loras { get; init; } = [];
    public AssetEditMetadata? Edit { get; init; }
}

public sealed record AssetEditMetadata
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RegionalImageSelection>? Regions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RegionalImageProvenance? Regional { get; init; }

    public required Guid SourceAssetId { get; init; }
    public required Guid SourceImageId { get; init; }
    public IReadOnlyList<AssetImageReference> References { get; init; } = [];
    public string Lora { get; init; } = "";
    public float LoraStrength { get; init; } = 1f;
    public float ReferenceBoost { get; init; } = 4f;
    public float? BaseReferenceBoost { get; init; }
    public int GroundingPixels { get; init; } = 768;
    public string FitMode { get; init; } = "fit";
    public ImageCropRegion? SourceCrop { get; init; }
    public IReadOnlyList<AssetReferenceCrop> ReferenceCrops { get; init; } = [];
    public IReadOnlyList<AssetReferenceLook> ReferenceLooks { get; init; } = [];
}

public sealed record ImageCropRegion
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 1;
    public double Height { get; init; } = 1;
}

public sealed record AssetImage
{
    // Organization can change without moving/re-encoding the original file.
    public Guid? StorageAssetId { get; init; }
    public IReadOnlyList<Guid> PreviousAssetIds { get; init; } = [];
    public string? Name { get; init; }
    public AssetImageSource? Source { get; init; }
    public Guid? LookId { get; init; }
    public string PreservationGuidance { get; init; } = "";
    // Retired: descriptions for text-only composition. Nothing reads or edits it; it is kept so saved images and
    // captured snapshots that have one keep their exact serialized form. Absent descriptions are omitted.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VisualDescription { get; init; }
    public required Guid Id { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public List<string> Tags { get; init; } = [];
    public bool IsReference { get; init; }
    public bool IsCover { get; init; }
    public AssetImageOrigin Origin { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public AssetGenerationMetadata? Generation { get; init; }
}

public sealed record ReferenceAsset
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? DefaultVoiceId { get; init; }
    // A starting choice for new reel bindings, never a live override of saved shots.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReelVisuals? DefaultReelVisuals { get; init; }
    public IReadOnlyList<PreferredImageReference> PreferredIdentityReferences { get; init; } = [];
    public IReadOnlyList<CharacterLook> Looks { get; init; } = [];
    public string PreservationGuidance { get; init; } = "";
    public IReadOnlyDictionary<ImageWorkflow, IReadOnlyList<LoraSelection>> Loras { get; init; } = new Dictionary<ImageWorkflow, IReadOnlyList<LoraSelection>>();
    public required Guid Id { get; init; }
    public AssetCategory Category { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public List<string> SuggestedImageTags { get; init; } = [];
    public List<AssetSourceEvidence> Evidence { get; init; } = [];
    public List<AssetImage> Images { get; init; } = [];
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed record AssetLibrary
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AssetReuseReceipt>? AssetReuseReceipts { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AssetMoveReceipt>? AssetMoveReceipts { get; init; }
    public List<AssetReferenceReel> Reels { get; init; } = [];
    public List<ReferenceReelDraft> ReelDrafts { get; init; } = [];
    public List<TrashedReferenceReel> ReelTrash { get; init; } = [];
    public List<ReelPublication> ReelPublications { get; init; } = [];
    public List<AssetExtractionReview> ExtractionReviews { get; init; } = [];
    public List<VoiceImportReceipt> VoiceImportReceipts { get; init; } = [];
    public List<ImageCopyReceipt> ImageCopyReceipts { get; init; } = [];
    public List<ImagePublicationReceipt> ImagePublications { get; init; } = [];
    public List<VoiceReference> Voices { get; init; } = [];
    public List<TrashedVoice> VoiceTrash { get; init; } = [];
    public int SchemaVersion { get; init; } = 1;
    public required Guid ProjectId { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset? UpdatedUtc { get; init; }
    public List<ReferenceAsset> Assets { get; init; } = [];
    public List<TrashedImage> Trash { get; init; } = [];

    public AssetLibrary Copy() => this with
    {
        AssetReuseReceipts = AssetReuseReceipts is null ? null : [.. AssetReuseReceipts],
        AssetMoveReceipts = AssetMoveReceipts is null ? null : [.. AssetMoveReceipts],
        Reels = ShotCopy.Of(Reels), ReelDrafts = ShotCopy.Of(ReelDrafts), ReelTrash = ShotCopy.Of(ReelTrash), ReelPublications = [.. ReelPublications],
        ExtractionReviews = ExtractionReviews.Select(r => r with { Scenes = r.Scenes.ToArray() }).ToList(),
        VoiceImportReceipts = [.. VoiceImportReceipts],
        ImageCopyReceipts = [.. ImageCopyReceipts],
        ImagePublications = [.. ImagePublications],
        Voices = ShotCopy.Of(Voices), VoiceTrash = ShotCopy.Of(VoiceTrash),
        Trash = Trash.Select(item => item with
        {
            Image = CopyImage(item.Image),
            Asset = item.Asset with { Images = [], PreferredIdentityReferences = item.Asset.PreferredIdentityReferences.ToArray(), Looks = item.Asset.Looks.Select(l => l.Copy()).ToArray(), Evidence = [.. item.Asset.Evidence], SuggestedImageTags = [.. item.Asset.SuggestedImageTags], Loras = CopyLoras(item.Asset) }
        }).ToList(),
        Assets = Assets.Select(asset => asset with
        {
            PreferredIdentityReferences = asset.PreferredIdentityReferences.ToArray(),
            Looks = asset.Looks.Select(l => l.Copy()).ToArray(),
            Evidence = [.. asset.Evidence],
            Loras = CopyLoras(asset),
            SuggestedImageTags = [.. asset.SuggestedImageTags],
            Images = asset.Images.Select(CopyImage).ToList()
        }).ToList()
    };
    private static IReadOnlyDictionary<ImageWorkflow, IReadOnlyList<LoraSelection>> CopyLoras(ReferenceAsset asset) =>
        asset.Loras.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<LoraSelection>)Array.AsReadOnly(pair.Value.ToArray()));
    private static AssetImage CopyImage(AssetImage image) => image with
    {
        PreviousAssetIds = image.PreviousAssetIds.ToArray(),
        Tags = [.. image.Tags],
        Generation = image.Generation is { } generation ? generation with { Loras = Array.AsReadOnly(generation.Loras.ToArray()), Edit = generation.Edit is { } edit ? edit with { References = edit.References.ToArray(), ReferenceCrops = edit.ReferenceCrops.ToArray(), ReferenceLooks = edit.ReferenceLooks.ToArray() } : null } : null
    };
}

public sealed record AssetImageInput(
    string FileName,
    IReadOnlyList<string> Tags,
    AssetImageOrigin Origin,
    AssetGenerationMetadata? Generation = null,
    Guid? LookId = null);

public sealed record AssetMedia(Stream Content, string ContentType, DateTimeOffset LastModified) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public enum ExtractionDecision { Create, Merge, Skip }

public sealed record AssetExtractionProposal
{
    public string? PreservationGuidance { get; set; }
    public List<LookExtractionProposal> Looks { get; set; } = [];
    public Guid Id { get; init; } = Guid.NewGuid();
    public AssetCategory Category { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> SuggestedTags { get; set; } = [];
    public List<AssetSourceEvidence> Evidence { get; init; } = [];
    public Guid? MatchedAssetId { get; set; }
    public ExtractionDecision Decision { get; set; } = ExtractionDecision.Create;
}

public sealed record AssetExtractionRequest(
    ScriptSourceSnapshot Script,
    AssetLibrary Library,
    IReadOnlyList<Guid> SceneIds,
    AiBackend Backend,
    string Model,
    TextModelReference? Selection = null);

public sealed record AssetExtractionResult(
    IReadOnlyList<AssetExtractionProposal> Proposals,
    string RawText,
    string? ValidationError = null);

public sealed record AssetExtractionUpdate(
    GenerationProgress? Progress = null,
    AssetExtractionResult? Result = null);

public sealed record ReferenceGenerationRequest
{
    public int Resolution { get; init; } = 1024;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QwenImage21Options? QwenImage21 { get; init; }
    public AssetLookContext? Look { get; init; }
    public Guid? ProjectId { get; init; }
    public IReadOnlyList<LoraSelection> Loras { get; init; } = [];
    public ImageWorkflow? Workflow { get; init; }
    public required string Prompt { get; init; }
    public required string AspectRatio { get; init; }
    public int Count { get; init; } = 1;
    public long? Seed { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record ReferenceEditRequest
{
    public int Resolution { get; init; } = 1024;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QwenImage21Options? QwenImage21 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RegionalImageSelection>? Regions { get; init; }

    public AssetLookContext? Look { get; init; }
    // Visit-local batch input: extensions keep the original ComfyUI configuration.
    [System.Text.Json.Serialization.JsonIgnore]
    public AiSettings? SettingsSnapshot { get; init; }
    public Guid? ProjectId { get; init; }
    public IReadOnlyList<LoraSelection> Loras { get; init; } = [];
    public ImageWorkflow? Workflow { get; init; }
    public required Guid SourceAssetId { get; init; }
    public required Guid SourceImageId { get; init; }
    public required string Prompt { get; init; }
    public required string AspectRatio { get; init; }
    public int Count { get; init; } = 1;
    public long? Seed { get; init; }
    public float ReferenceBoost { get; init; } = 4f;
    public float BaseReferenceBoost { get; init; } = 1f;
    public int GroundingPixels { get; init; } = 768;
    public ImageCropRegion? SourceCrop { get; init; }
    public IReadOnlyList<AssetReferenceCrop> ReferenceCrops { get; init; } = [];
    public IReadOnlyList<AssetReferenceLook> ReferenceLooks { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record ReferenceGenerationUpdate(
    string Status,
    int Candidate,
    int Total,
    byte[]? Image = null,
    string? FileName = null,
    AssetGenerationMetadata? Metadata = null,
    GenerationProgress? Progress = null);

public sealed record ComfyImageConfiguration(
    bool Success,
    string Message,
    IReadOnlyList<AiModel> DiffusionModels,
    IReadOnlyList<AiModel> TextEncoders,
    IReadOnlyList<AiModel> Vaes);

public sealed record ComfyImageEditConfiguration(
    bool Success,
    string Message,
    IReadOnlyList<AiModel> Loras,
    int MaximumReferences = 1,
    string? ReferenceCapabilityMessage = null);
