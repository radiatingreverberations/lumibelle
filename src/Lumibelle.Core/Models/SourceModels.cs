namespace lumibelle.Models;

public sealed record SourceDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid ProjectId { get; init; }
    public long Revision { get; init; }
    public long SourceRevision { get; init; }
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string Direction { get; init; } = "";
    public List<SourceAnalysisRun> Runs { get; init; } = [];
}

public enum SourceAnalysisStage { Read, Combine, Outline }
public enum SourceRunState { Running, Paused, Review, Accepted, Discarded }
// Offsets address the immutable source captured by this run, never the subsequently edited source.
public sealed record SourceChunk(int Number, string Heading, int Start, int Length);
public sealed record SourceNote(string Text, int[] Sources);
public sealed record SourceAnalysisRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    // A new proposal can reuse the immutable evidence from a completed analysis.
    public Guid? AnalysisFromRunId { get; init; }
    public required string Fingerprint { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    public required string Direction { get; init; }
    public required TextModelReference Model { get; init; }
    public required Guid TabId { get; init; }
    public int PromptBudget { get; init; }
    public List<SourceChunk> Chunks { get; init; } = [];
    public List<SourceNote> Notes { get; init; } = [];
    public List<SourceNote>? Level { get; init; }
    public List<SourceNote> Combined { get; init; } = [];
    public int CombineIndex { get; init; }
    public SourceRunState State { get; init; } = SourceRunState.Running;
    // Written before enqueue: a crash or lost acknowledgement reuses exactly the same request.
    public AiJobSubmission? Pending { get; init; }
    public List<Guid> Jobs { get; init; } = [];
    public SourceOutline? Proposal { get; init; }
    public SourceOutline? Reviewed { get; init; }
    public long ReviewRevision { get; init; }
    public string? Error { get; init; }
}
public sealed record SourceAnalysisRequest(Guid ProjectId, Guid RunId, SourceAnalysisStage Stage,
    string Title, string Direction, List<SourceNote> Inputs, string PreviousContext = "");
public sealed record SourceAnalysisResult(string Summary, SourceOutline? Outline = null);
public sealed record SourceOutline
{
    public string Title { get; set; } = "";
    public string Premise { get; set; } = "";
    public string Approach { get; set; } = "";
    public List<SourceOutlineAct> Acts { get; set; } = [];
    public string Omissions { get; set; } = "";
    // Explicit review decisions, separate from the model's prose about omissions.
    public int[] ReviewedOmissions { get; set; } = [];
}
public sealed record SourceOutlineAct
{
    public string Title { get; set; } = "";
    public string Purpose { get; set; } = "";
    public List<SourceOutlineScene> Scenes { get; set; } = [];
}
public sealed record SourceOutlineScene
{
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public int[] Sources { get; set; } = [];
}
