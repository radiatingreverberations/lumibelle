namespace lumibelle.Models;

public sealed record ReelViewCandidate(int Number, ReelKeyframe Keyframe, string Sha256);
public sealed record ReelViewSuggestionRequest(int Version, Guid ProjectId, Guid AssetId, Guid ReelId,
    ReferenceVideoMedia Media, string ReelName, string AssetName, string AssetNotes, string UseGuidance,
    string CameraPlan, string Instructions, string SelectionInstructions, int Count, string Baseline,
    IReadOnlyList<ReelViewCandidate> Candidates, IReadOnlyList<string> ReferenceHashes);
public sealed record ReelViewSelection(int Candidate, string Name, string Notes);
public sealed record ReelViewSuggestionResult(string Summary, IReadOnlyList<ReelViewSelection> Views,
    IReadOnlyList<string> Warnings);
