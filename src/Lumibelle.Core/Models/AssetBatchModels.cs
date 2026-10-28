namespace lumibelle.Models;

public enum AssetBatchKind { Images, CharacterReels, EnvironmentReels }
public enum AssetBatchState { Running, Paused, Completed, Stopped }
public enum AssetBatchItemState { Pending, Generating, Evaluating, Ready, NeedsAttention, Skipped, Enhancing }
public enum AssetImageVerdict { Pass, Concerns, CannotJudge }

public sealed record AssetBatchItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required AssetLookContext Context { get; init; }
    public AssetCategory Category { get; init; }
    public required string Prompt { get; init; }
    public string AdditionalInstructions { get; init; } = "";
    public string? EnhancedPrompt { get; init; }
    public int PromptAttempts { get; init; }
    public IReadOnlyList<Guid> PromptJobs { get; init; } = [];
    public VisualStyle? VisualStyle { get; init; }
    public ReferenceReelDraft? Recipe { get; init; }
    public Guid? ExistingReelId { get; init; }
    public IReadOnlyList<Guid> ReelCandidates { get; init; } = [];
    public IReadOnlyList<AssetReelReview> ReelReviews { get; init; } = [];
    public IReadOnlyList<Guid> ReviewedReels { get; init; } = [];
    public IReadOnlyList<AssetReferenceLook> References { get; init; } = [];
    public IReadOnlyList<LoraSelection> Loras { get; init; } = [];
    public AssetBatchItemState State { get; init; }
    public int Attempts { get; init; }
    public int ReviewAttempts { get; init; }
    public bool GenerateAgain { get; init; }
    // Retain the original wire name for saved image batches.
    [System.Text.Json.Serialization.JsonPropertyName("imageJobs")]
    public IReadOnlyList<Guid> GenerationJobs { get; init; } = [];
    public IReadOnlyList<Guid> Candidates { get; init; } = [];
    public Guid? ReviewJobId { get; init; }
    public IReadOnlyList<Guid> ReviewJobs { get; init; } = [];
    public AssetImageEvaluation? Evaluation { get; init; }
    public Guid? ApprovedImageId { get; init; }
    public string? Error { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public int OutputCount => Candidates.Count + ReelCandidates.Count;
}
public sealed record AssetBatch
{
    public AssetBatchKind Kind { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public required Guid TabId { get; init; }
    public required AiSettings Settings { get; init; }
    public TextModelReference? Reviewer { get; init; }
    public TextModelReference? PromptModel { get; init; }
    public int CandidateCount { get; init; } = 2;
    public string AspectRatio { get; init; } = "1:1";
    public int Resolution { get; init; } = 1024;
    public required IReadOnlyList<AssetBatchItem> Items { get; init; }
    public AssetBatchState State { get; init; }
    public Guid? PendingItemId { get; init; }
    public AiJobSubmission? Pending { get; init; }
    public string? Error { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Active => State is AssetBatchState.Running or AssetBatchState.Paused;
}
public sealed record AssetBatches
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid ProjectId { get; init; }
    public long Revision { get; init; }
    public IReadOnlyList<AssetBatch> Runs { get; init; } = [];
}
public sealed record AssetBatchPlanRow(AssetBatchItem Item, bool MissingReference, string? Issue);
public sealed record AssetImageReviewTile(AssetImageReference Image, string Sha256, string Role);
public sealed record AssetImageEvaluationRequest(Guid ProjectId, Guid BatchId, Guid ItemId, AssetLookContext Context,
    string Prompt, IReadOnlyList<Guid> CandidateIds, IReadOnlyList<AssetImageReviewTile> Tiles, string ContactSheetHash);
public sealed record AssetImageCheck(string Name, AssetImageVerdict Verdict, string Reason);
public sealed record AssetImageCandidateEvaluation(Guid ImageId, int Rank, AssetImageVerdict Verdict, string Summary, IReadOnlyList<AssetImageCheck> Checks);
public sealed record AssetImageEvaluation(IReadOnlyList<AssetImageCandidateEvaluation> Candidates, Guid? RecommendedImageId);
