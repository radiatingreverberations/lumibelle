using System.Text.Json.Serialization;

namespace lumibelle.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ScriptBlockKind>))]
public enum ScriptBlockKind { Act, Scene, Action, Character, Dialogue, Parenthetical, Transition }
[JsonConverter(typeof(JsonStringEnumConverter<ScriptScope>))]
public enum ScriptScope { Document, Scene, Act, Passage }
public sealed record ScriptSpan(string Text, bool Bold = false, bool Italic = false);
public sealed record ScriptBlock
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ScriptBlockKind Kind { get; init; } = ScriptBlockKind.Action;
    public List<ScriptSpan> Spans { get; init; } = [];
    [JsonIgnore] public string Text => string.Concat(Spans.Select(s => s.Text));
    public static ScriptBlock Create(ScriptBlockKind kind, string text) => new() { Kind = kind, Spans = [new(text)] };
    public ScriptBlock Copy() => this with { Spans = [.. Spans] };
}
public sealed record ScriptBrief
{
    public string Idea { get; init; } = "";
    public string Duration { get; init; } = "2–3 minutes";
    public string Style { get; init; } = "";
    public string ProductionProfile { get; init; } = "h3-practical-v1";
}
public sealed record ScriptDocument
{
    public int SchemaVersion { get; init; } = 2;
    public required Guid ProjectId { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset? UpdatedUtc { get; init; }
    // Compatibility for historical callers only; never persisted or sent by new requests.
    [JsonIgnore] public ScriptBrief Brief { get; init; } = new();
    public List<ScriptBlock> Blocks { get; init; } = [];
    public List<Guid> AppliedProposalIds { get; init; } = [];
    public List<ScriptSourceSceneLink> SourceSceneLinks { get; init; } = [];
    public Guid? ApprovedSnapshotId { get; init; }
    public ScriptDocument Copy() => this with { Blocks = Blocks.Select(b => b.Copy()).ToList(), AppliedProposalIds = [.. AppliedProposalIds],
        SourceSceneLinks = SourceSceneLinks.Select(l => l with { BlockIds = [.. l.BlockIds] }).ToList() };
}
public record ScriptSourceSnapshot(Guid Id, Guid ProjectId, long SourceRevision, DateTimeOffset CapturedUtc, List<ScriptBlock> Blocks);
public sealed record ApprovedScriptSnapshot(Guid Id, Guid ProjectId, long SourceRevision, DateTimeOffset ApprovedUtc,
    string Duration, string ProductionProfile, List<ScriptBlock> Blocks)
    : ScriptSourceSnapshot(Id, ProjectId, SourceRevision, ApprovedUtc, Blocks);
public sealed record ScriptRecovery(Guid Id, string Reason, DateTimeOffset CreatedUtc, ScriptDocument Document);
public sealed record ScriptSection(Guid Id, ScriptBlockKind Kind, string Title, int Start, int Count, int Number, Guid? ActId);
public sealed record ScriptSelection(Guid AnchorBlockId, int AnchorOffset, Guid HeadBlockId, int HeadOffset);
public sealed record ScriptTarget(ScriptScope Scope, string Name, List<ScriptBlock> OriginalBlocks, int StartOffset = 0, int EndOffset = 0);
public sealed record ScriptEditorState(long Version, List<ScriptBlock> Blocks, ScriptSelection? Selection, bool Bold = false, bool Italic = false, long Sequence = 0);
public sealed record ScriptRequestContext(ScriptDocument Document, ScriptTarget Target);
