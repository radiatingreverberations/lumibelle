using System.Text.Json.Serialization;

namespace lumibelle.Models;

public sealed record ProductionDocument
{
    public int SchemaVersion { get; set; } = 3;
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public List<ProductionComposition> Compositions { get; set; } = [];
    public List<ShotProductionContent> ShotContent { get; set; } = [];
    public List<LegacySetupContent> EarlierSetupContent { get; set; } = [];
}

public sealed record ProductionComposition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ShotId { get; set; }
    public string Name { get; set; } = "Default setup";
    public Guid? GenerationSetupId { get; set; }
    public long GenerationSetupVersion { get; set; }
    [JsonIgnore] public bool GenerationSetupArchived { get; set; }
    public long? Seed { get; set; }
    public int TakeCount { get; set; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerationOutputOverrides? OutputOverrides { get; set; }
    public bool Archived { get; set; }
    public long Version { get; set; }
    public string SourceFingerprint { get; set; } = "";
    // Runtime adapter for the video engine. The store hydrates coverage and shared
    // prompt/references; only generation settings are persisted with each setup.
    [JsonIgnore] public Shot Shot { get; set; } = new();
    public SetupInputs Inputs { get => SetupInputs.From(Shot); set => value.Apply(Shot); }
    public Guid? AppliedJobId { get; set; }
    public string DirectingNotes { get; set; } = "";
    public string RevisionNotes { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string ReferenceUsage { get; set; } = "";
    public Guid? AcceptedRevisionId { get; set; }
    public Guid? ReviewJobId { get; set; }
    public List<CompositionPromptRevision> History { get; set; } = [];
    [JsonIgnore] public CompositionPromptRevision? Accepted => History.FirstOrDefault(r => r.Id == AcceptedRevisionId);
    public ProductionComposition Copy() { var copy = ShotCopy.Of(this); copy.Shot = Shot.Copy(); return copy; }
}

public sealed record ShotProductionContent
{
    public Guid ShotId { get; set; }
    public string Prompt { get; set; } = "";
    public string DirectingNotes { get; set; } = "";
    public string RevisionNotes { get; set; } = "";
    public string ReferenceUsage { get; set; } = "";
    public Guid? AppliedJobId { get; set; }
    public Guid? ReviewJobId { get; set; }
    public Guid? AcceptedRevisionId { get; set; }
    public List<CompositionPromptRevision> History { get; set; } = [];
    public List<CharacterVoiceSelection>? CharacterVoices { get; set; }
    public List<ShotImageBinding> Images { get; set; } = [];
    public List<ShotVoiceBinding> Voices { get; set; } = [];
    public List<ShotVideoBinding> Videos { get; set; } = [];
    // Framing belongs to the shot, so every setup renders it at the same aspect. Null follows the project.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AspectOverride { get; set; }
    // LoRAs chosen for this shot in every setup, on top of each setup's preset LoRAs.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LoraSelection>? Loras { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShotContinuityFrame? ContinuityFrame { get; set; }

    public static ShotProductionContent From(ProductionComposition c) => ShotCopy.Of(new ShotProductionContent {
        ShotId = c.ShotId, Prompt = c.Prompt, DirectingNotes = c.DirectingNotes, RevisionNotes = c.RevisionNotes,
        ReferenceUsage = c.ReferenceUsage, AppliedJobId = c.AppliedJobId, ReviewJobId = c.ReviewJobId,
        AcceptedRevisionId = c.AcceptedRevisionId, History = c.History, CharacterVoices = c.Shot.CharacterVoices,
        Images = c.Shot.Images, Voices = c.Shot.Voices, Videos = c.Shot.Videos, AspectOverride = c.Shot.AspectOverride, Loras = c.Shot.ShotLoras,
        ContinuityFrame = c.Shot.ContinuityFrame
    });
    public void Apply(ProductionComposition c)
    {
        var copy = ShotCopy.Of(this);
        c.Prompt = copy.Prompt; c.DirectingNotes = copy.DirectingNotes; c.RevisionNotes = copy.RevisionNotes;
        c.ReferenceUsage = copy.ReferenceUsage; c.AppliedJobId = copy.AppliedJobId; c.ReviewJobId = copy.ReviewJobId;
        c.AcceptedRevisionId = copy.AcceptedRevisionId; c.History = copy.History;
        c.Shot.CharacterVoices = copy.CharacterVoices; c.Shot.Images = copy.Images;
        c.Shot.Voices = copy.Voices; c.Shot.Videos = copy.Videos; c.Shot.AspectOverride = copy.AspectOverride; c.Shot.ShotLoras = copy.Loras;
        c.Shot.ContinuityFrame = copy.ContinuityFrame;
    }
}

// Preserve differing pre-migration drafts and reference selections for explicit recovery.
public sealed record LegacySetupContent(Guid SetupId, string SetupName, ShotProductionContent Content);

public sealed record CompositionPromptRevision(Guid Id, DateTimeOffset CreatedUtc, string Prompt,
    string ReferenceUsage, string ContextFingerprint, string SourceFingerprint, Guid? JobId = null,
    TextModelReference? Model = null, IReadOnlyList<CompositionInput>? Images = null)
{
    // Optional: legacy revisions and immutable request fingerprints serialize as before.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferenceFingerprint { get; init; }
}
public sealed record CompositionInput(Guid BindingId, string Sha256);
public sealed record ProductionVideoContext(Guid CompositionId, long Version, string Name,
    CompositionPromptRevision Revision, IReadOnlyList<CompositionInput> Images)
{
    public Guid? GenerationSetupId { get; init; }
    public long? GenerationSetupVersion { get; init; }
}
public sealed record PromptCompositionRequest(Guid ProjectId, Guid CompositionId, long CompositionVersion,
    string ContextFingerprint, string SourceFingerprint, Shot Shot, string SceneContext,
    IReadOnlyList<string> NearbyShots, IReadOnlyList<ShotReferenceGuidance> Guidance,
    IReadOnlyList<ShotAppearanceContext> Appearances, IReadOnlyList<CompositionInput> Images,
    string DirectingNotes, string CurrentPrompt, string RevisionNotes, TextModelReference Model,
    bool FollowsDefault = false)
{
    public string Intent { get; init; } = string.IsNullOrEmpty(CurrentPrompt) ? "Initial" : "Revision";
    // Both only in requests captured before text-only composition was removed, so those still load; new requests
    // always send the references and leave them null.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InspectReferenceImages { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CompositionVisualDescription>? VisualDescriptions { get; init; }
    // Scene text and neighbouring shots were left out to keep the prompt small; set by requests from before they could be left out separately.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReducedScriptContext { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OmitSceneText { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OmitNearbyShots { get; init; }
    [JsonIgnore] public bool SceneTextLeftOut => ReducedScriptContext || OmitSceneText;
    [JsonIgnore] public bool NearbyShotsLeftOut => ReducedScriptContext || OmitNearbyShots;
    // The take frame the shot starts from (BindingId is the take), attached after the references.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompositionInput? OpeningFrame { get; init; }
}
/// <summary>Estimated tokens of the two parts of a composition's script context that can be left out.</summary>
public sealed record CompositionContextSize(int SceneTextTokens, int NearbyShotsTokens, int NearbyShots);
public sealed record PromptCompositionResult(string Prompt, string ReferenceUsage)
{
    // Advisory findings that do not block applying the prompt, such as a rounded duration.
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record SetupInputs
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CharacterVoiceSelection>? CharacterVoices { get; set; }
    public List<ShotImageBinding> Images { get; set; } = [];
    public List<ShotVoiceBinding> Voices { get; set; } = [];
    public List<ShotVideoBinding> Videos { get; set; } = [];
    public string Aspect { get; set; } = "16:9";
    public bool NativeResolution { get; set; } = false;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoResolution? Resolution { get; set; }
    public bool UpscalePreview { get; set; } = false;
    public string? GenerationPreset { get; set; } = null;
    public bool SaveLosslessFrames { get; set; } = false;
    public bool Turbo { get; set; } = false;
    public int TurboSteps { get; set; } = 4;
    public IReadOnlyList<LoraSelection>? Loras { get; set; } = null;
    public static SetupInputs From(Shot s) => new() { CharacterVoices = s.CharacterVoices, Images = s.Images, Voices = s.Voices, Videos = s.Videos, Aspect = s.Aspect, NativeResolution = s.NativeResolution, Resolution = s.Resolution, UpscalePreview = s.UpscalePreview, GenerationPreset = s.GenerationPreset, SaveLosslessFrames = s.SaveLosslessFrames, Turbo = s.Turbo, TurboSteps = s.TurboSteps, Loras = s.Loras };
    public void Apply(Shot s) { s.CharacterVoices = CharacterVoices; s.Images = Images; s.Voices = Voices; s.Videos = Videos; s.Aspect = Aspect; s.NativeResolution = NativeResolution; s.Resolution = Resolution; s.UpscalePreview = UpscalePreview; s.GenerationPreset = GenerationPreset; s.SaveLosslessFrames = SaveLosslessFrames; s.Turbo = Turbo; s.TurboSteps = TurboSteps; s.Loras = Loras; }
}
