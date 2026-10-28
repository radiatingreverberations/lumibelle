namespace lumibelle.Models;

public sealed record AssetReelEvaluationRequest(Guid BatchId, Guid ItemId, AssetLookContext Context,
    ReelViewSuggestionRequest Views, string ContactSheetHash);
public sealed record AssetReelEvaluation(AssetImageVerdict Verdict, IReadOnlyList<AssetImageCheck> Checks, ReelViewSuggestionResult Selection);
public sealed record AssetReelReview(Guid JobId, AssetReelEvaluationRequest Request, AssetReelEvaluation Result);
