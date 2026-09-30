namespace lumibelle.Models;

public enum ReelFraming { ContinuousTurn, BodyToFace, ThreeAngles, Custom, SideRearFace, EnvironmentTurn, EnvironmentPan, EnvironmentPath,
    EnvironmentHalfTurn, EnvironmentHeldViews, EnvironmentPullBack, EnvironmentApproach, EnvironmentArc, CharacterCapture,
    CharacterNeutralTurntable, CharacterSilhouetteReveal, CharacterPoseExpansion, CharacterDrapeCheck, CharacterSeatedToStanding, CharacterFacePriority, CharacterVoiceReference,
    PropOrbit, PropHalfOrbit, PropTurntable, PropHeldViews, PropRise, PropDetail, PropCustom }
public enum ReelCameraDirection { Right, Left }
public enum ReelVoiceMode { NewVoice, ExistingRecording, Silent }
public enum ReelArticulation { None, Arms, Knee }
public enum ReelCloseUpTransition { Cut, PushIn }

// A recipe is independent of screenplay coverage. Settings/inputs are projected
// into the legacy H3 transport at its boundary, never into the shot library.
public sealed record ReferenceReelDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public Guid? LookId { get; set; }
    public long Revision { get; set; }
    public string Name { get; set; } = "Character reference";
    public string Speaker { get; set; } = "";
    public string Language { get; set; } = "English";
    public string Line { get; set; } = "Okay. Here I am. Almost elegant.";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? VoiceDescription { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelSpeechSettings? Speech { get; set; }
    public ReelFraming Framing { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelCameraDirection? CameraDirection { get; set; }
    // Optional so opening an older recipe does not change its serialized fingerprint.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelArticulation? CaptureArticulation { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelCloseUpTransition? CaptureCloseUp { get; set; }
    public ReelVoiceMode VoiceMode { get; set; }
    public string PresetVersion { get; set; } = "character-reel-v1";
    public double Duration { get; set; } = 5;
    public string Aspect { get; set; } = "1:1";
    public string? GenerationPreset { get; set; }
    public bool Turbo { get; set; }
    public int TurboSteps { get; set; } = 4;
    public bool NativeResolution { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public VideoResolution? Resolution { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? SaveLosslessFrames { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LoraSelection>? Loras { get; set; }
    // This reel's own LoRAs, on top of the preset's Loras; for the same LoRA the reel's entry wins. Null keeps older fingerprints.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LoraSelection>? ReelLoras { get; set; }
    // Frozen preset identity/settings. Null keeps older recipes and their fingerprints intact.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GenerationSetup? GenerationSetup { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public GenerationOutputOverrides? OutputOverrides { get; set; }
    public List<ShotImageBinding> Images { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    // Historical wire name retained for saved recipes and captured fingerprints.
    // Each binding now selects Keyframes, RefMod or FullReel explicitly.
    public List<ShotVideoBinding>? KeyframeReels { get; set; }
    public ShotVoiceBinding? Voice { get; set; }
    public string Instructions { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string UseGuidance { get; set; } = "";
    public string? CheckedInputs { get; set; }
    public Guid? PendingJobId { get; set; }
    public List<Guid> ResolvedJobs { get; set; } = [];
    public ReferenceReelDraft Copy() => ShotCopy.Of(this);
}

public sealed record ReelPromptPair(string Prompt, string UseGuidance);
public sealed record ReelCompositionRequest(Guid ProjectId, ReferenceReelDraft Draft, string Baseline,
    AssetLookContext Character, IReadOnlyList<CompositionInput> Images)
{
    public IReadOnlyList<ShotReferenceGuidance> ImageGuidance { get; init; } = [];
}
public sealed record ReferenceReelGeneration(ReferenceReelDraft Recipe, Guid BatchId, Guid JobId,
    int Candidate, long Seed, VideoSnapshot Snapshot);
public sealed record AssetReferenceReel
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid AssetId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OriginalAssetId { get; init; }
    public Guid? LookId { get; init; }
    public string Name { get; init; } = "Character reference";
    public string UseGuidance { get; init; } = ShotVideoBinding.DefaultDescription;
    public required ReferenceVideoMedia Media { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public ReferenceReelGeneration? Generation { get; init; }
    public Guid? SourceTakeId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelKeyframeSet? Keyframes { get; init; }
}
public sealed record TrashedReferenceReel(AssetReferenceReel Reel, ReferenceAsset Owner, DateTimeOffset DeletedUtc)
{
    // Reels removed before they joined Trash expiry have none and stay until deleted.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresUtc { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Purging { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
    public static TrashedReferenceReel Removed(AssetReferenceReel reel, ReferenceAsset owner, DateTimeOffset now) => new(reel, owner, now) { ExpiresUtc = now.AddDays(30) };
}
public sealed record ReelPublication(Guid ReelId, Guid JobId, Guid BatchId, int Candidate, string Fingerprint);
public sealed record ReelVideoContext(ReferenceReelDraft Recipe, AssetLookContext Character)
{
    public ReferenceAsset? Owner { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReelRegenerationSource? RegenerationSource { get; init; }
}
public sealed record ReelRegenerationSource(Guid ReelId, long Seed, int Width, int Height);
