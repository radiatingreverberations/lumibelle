namespace lumibelle.Models;

public enum VideoPreparationStage { Source, Setup, References, Capture, Request, Queue }

// Progress describes completed work, not an estimate of the final render time.
public sealed record VideoPreparationProgress(VideoPreparationStage Stage, string Message,
    long? Completed = null, long? Total = null, string? Detail = null);
