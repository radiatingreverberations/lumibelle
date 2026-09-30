namespace lumibelle.Models;

public enum WritingOperation { Discuss, Outline, Draft, Revise, Continue, Rewrite }
public enum AssistantRunStatus { Running, Completed, Failed, Cancelled, Interrupted }
public enum AiBackend { ComfyUI, OpenRouter, Codex, ClaudeCode }
public enum AiModelVerificationState { NotApplicable, Untested, Verified }
public enum GenerationPhase { Submitting, Queued, Preparing, Generating, Finalizing, Downloading, Saving, Completed }
public sealed record ConversationMessage(string Role, string Text);

public sealed record GenerationProgress(
    GenerationPhase Phase,
    string Label,
    double? Current = null,
    double? Maximum = null,
    string? Unit = null,
    TimeSpan Elapsed = default,
    TimeSpan? EstimatedRemaining = null,
    bool LiveUpdatesAvailable = true)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionStageId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? EstimateScope { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EstimateObservedUtc { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EstimateExpiresUtc { get; init; }
    public bool IsDeterminate => Current is not null && Maximum is > 0;
    public double Percentage => IsDeterminate ? Math.Clamp(Current!.Value / Maximum!.Value * 100d, 0d, 100d) : 0d;
}

public sealed record AssistantRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long Revision { get; init; }
    public Guid SessionId { get; init; }
    public Guid? JobId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CorrectedFromRunId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public WritingOperation Operation { get; init; }
    public ScriptTarget Target { get; init; } = new(ScriptScope.Document, "Whole script", []);
    public long SourceRevision { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public string Instructions { get; init; } = "";
    public AiBackend Backend { get; init; }
    public string Model { get; init; } = "";
    public AssistantRunStatus Status { get; init; } = AssistantRunStatus.Running;
    public string Output { get; init; } = "";
    public string? Error { get; init; }
    public List<ScriptBlock>? Proposal { get; init; }
    public int EditFormat { get; init; }
    public ScriptEditProposal? Edits { get; init; }
    public bool Rejected { get; init; }
    public bool Applied { get; init; }
    public ScriptTarget? AppliedTarget { get; init; }
}

public sealed record AssistantHistory
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid ProjectId { get; init; }
    public long Revision { get; init; }
    public List<AssistantRun> Runs { get; init; } = [];
}

public sealed record ScriptAssistantRequest(AssistantRun Run, ScriptDocument Script,
    IReadOnlyList<ConversationMessage> Conversation, TextModelReference? Selection = null);
public sealed record AssistantUpdate(string? Text = null, string? Status = null,
    List<ScriptBlock>? Blocks = null, string? ValidationError = null, bool Complete = false,
    GenerationProgress? Progress = null, ScriptEditProposal? Edits = null);

public sealed record AiSettings
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public QwenImage21Settings? QwenImage21 { get; init; }
    public CodexSettings Codex { get; init; } = new();
    public ClaudeCodeSettings ClaudeCode { get; init; } = new();
    public H3Settings H3 { get; init; } = new();
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; }
    public AiBackend DefaultBackend { get; init; } = AiBackend.ComfyUI;
    public string ComfyUrl { get; init; } = "http://127.0.0.1:8188";
    public string ComfyModel { get; init; } = "gemma4_e4b_it_fp8_scaled.safetensors";
    public string OpenRouterModel { get; init; } = "";
    public bool HasOpenRouterKey { get; init; }
    public float Temperature { get; init; } = 0.7f;
    public int MaxOutputTokens { get; init; } = 2048;
    public int TimeoutSeconds { get; init; } = 300;
    public int OpenRouterConcurrency { get; init; } = 1;
    public string ComfyImageModel { get; init; } = "krea2_turbo_int8_convrot.safetensors";
    public string ComfyImageTextEncoder { get; init; } = "qwen3vl_4b_fp8_scaled.safetensors";
    public string ComfyImageVae { get; init; } = "qwen_image_vae.safetensors";
    public string ComfyImageEditLora { get; init; } = "krea2_identity_edit_v1_2.safetensors";
    public ImageWorkflow DefaultImageWorkflow { get; init; } = ImageWorkflow.Krea2;
    public string FluxKleinModel { get; init; } = "flux-2-klein-9b-kv-fp8.safetensors";
    public string FluxKleinTextEncoder { get; init; } = "qwen_3_8b_fp8mixed.safetensors";
    public string FluxKleinVae { get; init; } = "flux2-vae.safetensors";
    public int ImageTimeoutSeconds { get; init; } = 600;
    public List<ComfyTextModelVerification> ComfyTextModelVerifications { get; init; } = [];
    public List<TextModelReference> StarredTextModels { get; init; } = [];
    public List<TextModelReference> TextModelProfiles { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TextModelReference? TextDefault { get; init; }
    public Dictionary<string, string> TextModelAliases { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, ComfyTextModelSettings> ComfyTextModels { get; init; } = new(StringComparer.Ordinal);
    public List<OpenRouterTextModelBenchmark> OpenRouterTextModelBenchmarks { get; init; } = [];
    public IReadOnlyList<LoraDefinition> LoraLibrary { get; init; } = [];
}

public sealed record ComfyTextModelBenchmark(
    DateTimeOffset MeasuredUtc,
    string? DeviceName,
    int? DeviceIndex,
    long? VramTotalBytes,
    long? BaselineVramUsedBytes,
    long? PeakVramUsedBytes,
    long? BaselineTorchAllocatedBytes,
    long? PeakTorchAllocatedBytes,
    int TokenLimit,
    int? GeneratedTokens,
    double? TokensPerSecond,
    bool CacheClearConfirmed,
    bool CustomPrompt);
public sealed record ComfyTextModelVerification(string ComfyUrl, string ComfyVersion, string Model, DateTimeOffset VerifiedUtc,
    List<ComfyTextModelBenchmark>? Benchmarks = null)
{
    // Null when the test predates capability probes or could not run them (for example after recovery).
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ComfyTextModelCapabilities? Capabilities { get; init; }
}
/// <summary>
/// Observed by the model test: each capability is claimed only when the model repeated a code it could
/// have received solely through that channel, so a tokenizer that silently drops the input fails.
/// </summary>
public sealed record ComfyTextModelCapabilities(bool SystemPrompt, ComfyVisionInput Vision);
public sealed record ComfyTextModelTestRequest(string Prompt, int MaxOutputTokens = 256);
public sealed record AiModel(string Id, string Name, AiModelVerificationState Verification = AiModelVerificationState.NotApplicable, bool SupportsImages = false, IReadOnlyList<string>? ReasoningEfforts = null, string? DefaultEffort = null, AiModelCatalogInfo? Catalog = null);
public sealed record AiModelCatalogInfo(string? Description = null, long? ContextLength = null, long? MaxOutputTokens = null,
    bool SupportsReasoning = false, AiModelPricing? Pricing = null, DateTimeOffset? AddedUtc = null, int? PopularityOrder = null)
{
    // Null means the catalog did not report this capability, not that it is unsupported.
    public IReadOnlyList<string>? SupportedParameters { get; init; }
    public IReadOnlyList<string>? SupportedReasoningEfforts { get; init; }
    public bool? SupportsReasoningBudget { get; init; }
    public bool? ReasoningMandatory { get; init; }
}
public sealed record AiModelPricing(decimal? InputPerToken, decimal? OutputPerToken, IReadOnlyDictionary<string, decimal?> Additional,
    bool Variable = false, bool Conditional = false)
{
    public bool FreeText => !Variable && !Conditional && InputPerToken == 0 && OutputPerToken == 0 &&
        new[] { "request", "internal_reasoning" }.All(key => !Additional.ContainsKey(key) || Additional[key] == 0);
}
public sealed record AiConnectionCheck(bool Success, string Message, IReadOnlyList<AiModel> Models, string? BackendVersion = null);
public sealed record AiModelVerificationUpdate(GenerationProgress? Progress = null, ComfyTextModelVerification? Verification = null,
    string? Response = null);
