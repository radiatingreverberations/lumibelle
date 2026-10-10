using System.Text.Json;
using lumibelle.Services.Story;

namespace lumibelle.Models;

public enum VideoResolution { Quick, Preview, Detail, Native }

public static class ShotCopy
{
    public static T Of<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
}

public sealed record H3Settings
{
    public H3PerformancePreferences Performance { get; set; } = new();
    // MiniMax's own Ref2VA weights, as packaged by Comfy-Org. Saved settings keep whatever model they chose.
    public string Model { get; set; } = "minimax_h3_ref2va_pruned_int8_convrot.safetensors";
    // The encoder and VAEs ComfyUI's own Ref2VA template uses; community files are alternatives.
    public string Encoder { get; set; } = "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors";
    public string VideoVae { get; set; } = "minimax_h3_video_vae_int8_convrot.safetensors";
    public string AudioVae { get; set; } = "minimax_h3_audio_vae_fp32.safetensors";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? LarryLora { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PddCheckpoint { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? HyperFlowLora { get; set; }
    public string TurboLora { get; set; } = "minimax_h3_ref2v_turbo_4step_v0.1_comfyui_bf16.safetensors";
    public string Turbo8StepLora { get; set; } = "minimax_h3_ref2v_turbo_8step_v1.0_768p_comfyui_bf16.safetensors";
    public int TimeoutSeconds { get; set; } = 7200;
    public string Ffmpeg { get; set; } = "ffmpeg";
    public string Ffprobe { get; set; } = "ffprobe";
    public string LatentUpscaler { get; set; } = "";
}
// Retain the old value solely so saved bindings are shown as unavailable rather than dropped.
public enum ShotImageKind { AssetImage, ContinuityFrame }
public sealed record ShotImageBinding
{
    public string AiUseHint { get; set; } = "Auto";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool InferUsage { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ShotImageUse? Use { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReferenceSetupOrigin? SetupOrigin { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public ShotImageKind Kind { get; set; }
    public Guid AssetId { get; set; }
    public Guid MediaId { get; set; }
    public string Name { get; set; } = "";
    public string Role { get; set; } = "Appearance";
    public string Notes { get; set; } = "";
    public Guid? RepresentsId { get; set; }
    public Guid? LookId { get; set; }
    public ShotReferencePurpose? Purpose { get; set; }
    public string? PreservationOverride { get; set; }
    public ImageCropRegion? Crop { get; set; }
}
public sealed record ShotCharacter(Guid Id, string Name)
{
    public ShotAppearance? Appearance { get; init; }
}
public sealed record ShotReferenceGuidance(Guid BindingId, string AssetDefault, string ImageDefault, string? Override)
{
    public string LookDefault { get; init; } = "";
    public string Phase { get; init; } = "";
    public string Effective => Override ?? string.Join("\n", new[] { AssetDefault, LookDefault, ImageDefault }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
public sealed record ShotDialogue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Speaker { get; set; } = "";
    public string Language { get; set; } = "English";
    public string Text { get; set; } = "";
}
public sealed record ShotVoiceBinding
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CharacterAssetId { get; set; }
    public Guid VoiceId { get; set; }
    public Guid AssetId { get; set; }
    public string Speaker { get; set; } = "";
    public double Start { get; set; }
    public double Duration { get; set; } = 5;
}
public sealed record ShotPlanningSource(Guid Id, string Profile, TextModelReference Model, double RequestedMaximum, int EffectiveMaximumFrames, string Instructions, IReadOnlyList<Guid> SceneIds, DateTimeOffset CreatedUtc);
public sealed record Shot
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<CharacterVoiceSelection>? CharacterVoices { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LoraSelection>? Loras { get; set; }
    // This shot's own LoRAs, applied on top of the preset's Loras; for the same LoRA the shot's entry wins.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LoraSelection>? ShotLoras { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Untitled shot";
    public Guid? ApprovedScriptId { get; set; }
    public Guid? SceneId { get; set; }
    public string SceneTitle { get; set; } = "";
    public List<Guid> SourceBlockIds { get; set; } = [];
    public string SourceExcerpt { get; set; } = "";
    public ShotPlanningSource? Planning { get; set; }
    public double? Duration { get; set; }
    public string Description { get; set; } = "";
    public List<ShotDialogue> Dialogue { get; set; } = [];
    public List<ShotCharacter> Characters { get; set; } = [];
    public string Atmosphere { get; set; } = "";
    public string Music { get; set; } = "No background music.";
    public List<ShotImageBinding> Images { get; set; } = [];
    public List<ShotVoiceBinding> Voices { get; set; } = [];
    public List<ShotVideoBinding> Videos { get; set; } = [];
    public string Aspect { get; set; } = "16:9";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AspectOverride { get; set; }
    public bool NativeResolution { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public VideoResolution? Resolution { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool UpscalePreview { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationPreset { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool SaveLosslessFrames { get; set; }
    // Keeps each take's audio/video latents so it can be refined at a larger size later.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool SaveLatents { get; set; }
    public bool Turbo { get; set; }
    public int TurboSteps { get; set; } = 4;
    public Guid? SelectedTakeId { get; set; }
    // Takes of this shot open exactly on this frame of an earlier take, for a continuous cut.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ShotStartFrame? StartFrame { get; set; }
    // A take frame used as a reference picture, numbered after the shot's images: usually the previous shot's last frame.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ShotContinuityFrame? ContinuityFrame { get; set; }
    public Shot Copy() => ShotCopy.Of(this);
}
/// <summary>A frame of a saved take used as a Picture reference, read from the take rather than saved to Assets. Id identifies the picture binding.</summary>
public sealed record ShotContinuityFrame(Guid Id, Guid TakeId, int Frame, string Name);
/// <summary>A frame of a saved take, by zero-based index. Take frames never change, so the take and index identify the image.</summary>
public sealed record ShotStartFrame(Guid TakeId, int Frame);
public sealed record ShotRecovery(Guid Id, DateTimeOffset CreatedUtc, string Reason, List<Shot> Shots)
{
    public List<SceneReferenceSetup> SceneSetups { get; init; } = [];
}
public sealed record ShotFrame(int Index, string FileName, long Bytes)
{
    public int ArchiveFrameIndex { get; init; }
}
public sealed record ShotTake
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ShotTakeCopySource? CopySource { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AiVideoJobRequest? CopyRequest { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TakeComposition? Composition { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAnyLosslessFrames => FrameArchiveRemoval is null && TakeBundlesAvailable();
    private bool TakeBundlesAvailable() => Frames.Count > 0 || Composition?.Segments.Any(s => s.Source.HasAnyLosslessFrames) == true || Extension?.Source.HasAnyLosslessFrames == true;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TakeExtensionRequest? Extension { get; set; }
    public TakeTrim? Trim { get; set; }
    public RetainedRefinementSource? RetainedSource { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int FrameCount => Composition is { } composition ? composition.Segments.Sum(s => s.EndFrameExclusive - s.StartFrame) : Trim is { } trim ? trim.EndFrameExclusive - trim.StartFrame : Snapshot.FrameCount;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasLosslessFrames => FrameArchiveRemoval is null && (Composition is { } composition ? composition.Segments.All(s => s.Source.HasLosslessFrames) : Trim?.LosslessFrames ?? (Refinement is not null || Snapshot.OutputPolicy?.SaveLosslessFrames != false));
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FrameArchiveRemoval? FrameArchiveRemoval { get; set; }
    public VideoTakeTimings? Timings { get; set; }
    public H3RefinementPackage? RefinementPackage { get; set; }
    public TakeRefinement? Refinement { get; set; }
    public Guid? AiJobId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasVisibleLosslessFrames => FrameArchiveRemoval is null && (Composition is { } c ? c.Segments.Any(s => s.Source.HasLosslessFrames) : HasLosslessFrames);
    public bool IsLosslessFrame(int frame)
    {
        if (frame < 0 || frame >= FrameCount || FrameArchiveRemoval is not null) return false;
        if (Composition is null) return HasLosslessFrames;
        foreach (var segment in Composition.Segments) {
            var count = segment.EndFrameExclusive - segment.StartFrame;
            if (frame < count) return segment.Source.IsLosslessFrame(segment.StartFrame + frame);
            frame -= count;
        }
        return false;
    }
    public Guid Id { get; set; } = Guid.NewGuid();
    // Current library owner; Snapshot.Shot retains the original generation context.
    public Guid ShotId { get; set; }
    public Guid RunId { get; set; }
    public int Candidate { get; set; }
    public long Seed { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; } = 24;
    public long Bytes { get; set; }
    public string Directory { get; set; } = "";
    public List<ShotFrame> Frames { get; set; } = [];
    public required VideoSnapshot Snapshot { get; set; }
}
public enum ShotTrashKind { Take }
public sealed record ShotTakeCopySource(Guid ProjectId, Guid TakeId, VideoSnapshot Snapshot);
public sealed record ShotTrashEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ShotTrashKind Kind { get; set; }
    public ShotTake? Take { get; set; }
    public Shot? Owner { get; set; }
    public int OwnerPosition { get; set; }
    public int Position { get; set; }
    public DateTimeOffset DeletedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public bool Purging { get; set; }
    public string? Error { get; set; }
}
public sealed record ShotDocument
{
    public List<lumibelle.Services.Shots.ShotProjectCopyReceipt> ProjectCopies { get; set; } = [];
    public int SchemaVersion { get; set; } = 2;
    public List<SceneReferenceSetup> SceneSetups { get; set; } = [];
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset? UpdatedUtc { get; set; }
    public List<Shot> Shots { get; set; } = [];
    public List<ShotTake> Takes { get; set; } = [];
    public List<ShotTrashEntry> Trash { get; set; } = [];
    public List<ShotRecovery> Recovery { get; set; } = [];
    public List<ShotPlanningReview> PlanningReviews { get; set; } = [];
    public List<ShotTakePublication> TakePublications { get; set; } = [];
    public List<TakeTrimRequest> TrimPublications { get; set; } = [];
    public List<TakeExtensionPublication> ExtensionPublications { get; set; } = [];
    public ShotDocument Copy() => ShotCopy.Of(this);
}
public sealed record ShotTakePublication(Guid TakeId, Guid RunId, Guid ShotId, Guid? JobId, int Candidate, string Fingerprint, DateTimeOffset PublishedUtc);
public sealed record VoiceReference
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelVoiceSource? SourceReel { get; init; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? StorageAssetId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? PreviousAssetIds { get; init; }
    public bool Matches(ShotVoiceBinding binding) => Id == binding.VoiceId &&
        (AssetId == binding.AssetId || PreviousAssetIds?.Contains(binding.AssetId) == true);
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public double Duration { get; set; }
    public double Start { get; set; }
    public double ExcerptDuration { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}
public sealed record TrashedVoice(Guid Id, VoiceReference Voice, ReferenceAsset Owner, int Position,
    DateTimeOffset DeletedUtc, DateTimeOffset ExpiresUtc, bool Purging = false, string? Error = null);
public sealed record VideoSnapshot(Guid ProjectId, long SourceRevision, Shot Shot, string Prompt, string Fingerprint,
    string ComfyUrl, H3Settings Settings, int Width, int Height, int FrameCount, string Profile = "h3-single-take-v1")
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TakeMotionContext? Motion { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TakeRegenerationSource? RegenerationSource { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public VideoDubContext? Dub { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetComfyUrl { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ExecutionComfyUrl => TargetComfyUrl ?? ComfyUrl;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ProductionVideoContext? Production { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelVideoContext? Reel { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AppliedLora>? AppliedLoras { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public H3PerformanceProfile? Performance { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public H3PresetProfile? Preset { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public H3OutputPolicy? OutputPolicy { get; init; }
    public bool CaptureRefinementData { get; init; }
    public IReadOnlyList<ShotReferenceGuidance> ReferenceGuidance { get; init; } = [];
    public IReadOnlyList<ShotAppearanceContext> Appearances { get; init; } = [];
    public H3Sampling? Sampling { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public H3PreviewUpscaleProfile? PreviewUpscale { get; init; }
    /// <summary>The shot's scene in the script as it stood when the take was generated, so later script edits, and only those, mark the take.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SceneFingerprint { get; init; }
}
public sealed record TakeRegenerationSource(Guid TakeId, long Seed, int Width, int Height);
public enum H3UpscalerImplementation { Lbh, Plus }
public sealed record H3PreviewUpscaleProfile(string Version, H3UpscalerImplementation Implementation, string Checkpoint,
    int Width, int Height, string Device, string Precision, int Align, bool Offload, bool TemporalChunking, bool KeepProportion);
public sealed record H3PreviewUpscaleCapability(H3UpscalerImplementation? Implementation, string? Issue);
public sealed record H3Sampling(string Profile, int Steps, string Sampler, string Scheduler, double VideoShift, double AudioShift, string? Lora, double LoraStrength);
public enum VideoCandidateState { Waiting, Preparing, Submitting, Running, Downloading, Complete, Cancelled, Failed, Uncertain }
public sealed record VideoCandidate
{
    public int Number { get; set; }
    public Guid TakeId { get; set; } = Guid.NewGuid();
    public long Seed { get; set; }
    public VideoCandidateState State { get; set; }
    public string? PromptId { get; set; }
    public string ClientId { get; set; } = Guid.NewGuid().ToString("D");
    public string? Error { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
    public JsonElement? Output { get; set; }
    public ShotTake? ArchivedTake { get; set; }
}
public sealed record PreparedVideoInput(string FileName, bool Audio)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public VideoInputKind? Kind { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? VideoIndex { get; init; }
    /// <summary>The captured content's hash, which finds the file in the project's input store.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public VideoInputKind EffectiveKind => Kind ?? (Audio ? VideoInputKind.Audio : VideoInputKind.Image);
}
public sealed record VideoRun
{
    public TakeRefinement? Refinement { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public required VideoSnapshot Snapshot { get; set; }
    public List<VideoCandidate> Candidates { get; set; } = [];
    public List<PreparedVideoInput> Inputs { get; set; } = [];
    public bool InputsPrepared { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public bool CancelRequested { get; set; }
    public bool Paused { get; set; }
    public string Status { get; set; } = "Waiting";
    public VideoRun Copy() => ShotCopy.Of(this);
}
public sealed record ShotPlanningRequest(ScriptSourceSnapshot Script, AssetLibrary Assets, IReadOnlyList<Guid> SceneIds,
    double MaximumSeconds, string Instructions, TextModelReference Selection)
{
    public bool CoverageOnly { get; init; }
    // Set when drafting one existing shot in place rather than breaking down whole scenes.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SingleShotDraft? SingleShot { get; init; }
}
/// <summary>One shot drafted at its place in a scene. The scene's other shots, in order, are context the new shot should not repeat;
/// Position is the index among them where this shot sits. Current is the shot's existing content, if any, for the directions to revise.</summary>
public sealed record SingleShotDraft(Guid ShotId, int Position, IReadOnlyList<SceneShotSummary> SceneShots, SceneShotSummary? Current);
public sealed record SceneShotSummary(string Title, double? Duration, string Description, IReadOnlyList<ShotDialogue> Dialogue, IReadOnlyList<Guid> SourceBlockIds)
{
    public static SceneShotSummary From(Shot shot) => new(shot.Title, shot.Duration, shot.Description, ShotCopy.Of(shot.Dialogue), [.. shot.SourceBlockIds]);
}
public sealed record ShotPlanningResult(List<Shot> Shots, string Raw, List<string> UncoveredDialogue, string? Error = null)
{
    public List<string> DialogueNotes { get; init; } = [];
    public List<string> CoverageNotes { get; init; } = [];
    public List<string> SourceNotes { get; init; } = [];
}
public sealed record ShotPlanningUpdate(GenerationProgress? Progress = null, ShotPlanningResult? Result = null);
public sealed record ShotPlanningReview(Guid JobId, DateTimeOffset AppliedUtc, IReadOnlyList<Guid> ShotIds, string Fingerprint);
public sealed record ShotPlanningReviewDraft(List<Shot> Shots);
public sealed record H3Configuration(bool StandardReady, bool TurboReady, string Message,
    IReadOnlyList<string> Models, IReadOnlyList<string> Encoders, IReadOnlyList<string> Vaes, IReadOnlyList<string> Loras)
{
    public bool DiscoverySucceeded { get; init; }
    public string? VideoReferenceIssue { get; init; } = "Refresh Video models to check reference video support.";
    public IReadOnlyList<H3PresetSetup> Presets { get; init; } = [];
    public string? ArchiveIssue { get; init; }
    public string? AttentionIssue { get; init; }
    public ComfyLoraCheck? OptionalLoras { get; init; }
    public H3PerformanceCapabilities Performance { get; init; } = new();
    public string? SelectedPerformanceIssue { get; init; }
    public bool PackageCaptureReady { get; init; }
    public string? RefinementIssue { get; init; } = "Check Video models to see whether takes can be refined.";
    public IReadOnlyList<string> LatentUpscalers { get; init; } = [];
    public H3PreviewUpscaleCapability PreviewUpscaling { get; init; } = new(null, "Refresh Video models to check preview upscaling setup.");
    public bool Turbo8StepReady { get; init; }
    public string? TurboIssue { get; init; }
    public string? Turbo8StepIssue { get; init; }
    public string? RefModIssue { get; init; } = "Refresh Video models to check the experimental RefMod nodes.";
    public string? StartFrameIssue { get; init; } = "Refresh Video models to check support for starting from a frame.";
    public bool Ready(Shot shot) => (!Services.Production.ReelRefMods.Uses(shot) || RefModIssue is null) &&
        (!shot.Videos.Any(v => v.EffectiveVisuals == ReelVisuals.FullReel) || VideoReferenceIssue is null) &&
        (shot.StartFrame is null || StartFrameIssue is null) && Services.Shots.H3Presets.Issue(this, shot) is null;
    public string Issue(Shot shot) => (Services.Production.ReelRefMods.Uses(shot) ? RefModIssue : null) ??
        (shot.Videos.Any(v => v.EffectiveVisuals == ReelVisuals.FullReel) ? VideoReferenceIssue : null) ??
        (shot.StartFrame is not null ? StartFrameIssue : null) ?? Services.Shots.H3Presets.Issue(this, shot) ?? Message;
    public IReadOnlyList<string> InstalledModels { get; init; } = Models;
    public IReadOnlyList<string> InstalledEncoders { get; init; } = Encoders;
    public IReadOnlyList<string> InstalledVaes { get; init; } = Vaes;
    public IReadOnlyList<string> InstalledLoras { get; init; } = Loras;
    // Every node class ComfyUI reported, so the settings page can tell which node packs are installed.
    public IReadOnlyCollection<string> Nodes { get; init; } = [];
}
