using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using lumibelle.Models;

namespace lumibelle.Services.Story;

public static class ScriptStructure
{
    public static IReadOnlyList<ScriptSection> Sections(IReadOnlyList<ScriptBlock> blocks)
    {
        List<ScriptSection> result = []; Guid? act = null; int number = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Kind is not (ScriptBlockKind.Act or ScriptBlockKind.Scene)) continue;
            if (b.Kind == ScriptBlockKind.Act) act = b.Id; else number++;
            var end = i + 1;
            while (end < blocks.Count && (b.Kind == ScriptBlockKind.Act
                ? blocks[end].Kind != ScriptBlockKind.Act : blocks[end].Kind is not (ScriptBlockKind.Scene or ScriptBlockKind.Act))) end++;
            result.Add(new(b.Id, b.Kind, string.IsNullOrWhiteSpace(b.Text) ? $"Untitled {b.Kind.ToString().ToLowerInvariant()}" : b.Text,
                i, end - i, b.Kind == ScriptBlockKind.Scene ? number : 0, b.Kind == ScriptBlockKind.Scene ? act : null));
        }
        return result;
    }
    public static string Fingerprint(IEnumerable<ScriptBlock> blocks) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(blocks, AtomicJsonFile.Options))));
    public static string ContextFingerprint(ScriptDocument doc) => Fingerprint(doc.Blocks);
    // Importing into the editor merges adjacent spans with identical styling and
    // removes empty spans. That representation change is not an author edit.
    // Keep persisted fingerprints exact; normalize only when comparing writing.
    public static bool EquivalentBlocks(IEnumerable<ScriptBlock> left, IEnumerable<ScriptBlock> right) =>
        Fingerprint(left.Select(b => b with { Spans = MergeSpans(b.Spans) })) ==
        Fingerprint(right.Select(b => b with { Spans = MergeSpans(b.Spans) }));
    private static List<ScriptSpan> MergeSpans(IEnumerable<ScriptSpan> spans)
    {
        List<ScriptSpan> merged = [];
        foreach (var span in spans.Where(s => s.Text.Length > 0)) {
            if (merged.Count > 0 && merged[^1].Bold == span.Bold && merged[^1].Italic == span.Italic)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + span.Text };
            else merged.Add(span);
        }
        return merged;
    }
    public static bool SameProduction(ScriptDocument doc, ApprovedScriptSnapshot? approved) => approved is not null &&
        Fingerprint(doc.Blocks) == Fingerprint(approved.Blocks) && doc.Brief.Duration == approved.Duration && doc.Brief.ProductionProfile == approved.ProductionProfile;

    public static void ValidateBlocks(IReadOnlyList<ScriptBlock> blocks)
    {
        if (blocks is null || blocks.Count > 10000 || blocks.Any(b => b is null || b.Id == Guid.Empty || !Enum.IsDefined(b.Kind) ||
            b.Spans is null || b.Spans.Any(s => s is null || s.Text is null)) || blocks.Select(b => b.Id).Distinct().Count() != blocks.Count)
            throw new WorkspaceStoreException("The screenplay contains invalid blocks. The saved script has not been replaced.");
        if (blocks.Sum(b => (long)b.Text.Length) > 500000)
            throw new WorkspaceStoreException("This script exceeds the 500,000 character limit. Your saved version is intact.");
    }
    public static ScriptTarget Capture(ScriptDocument doc, ScriptScope scope, ScriptSelection? selection)
    {
        if (scope == ScriptScope.Document) return new(scope, "Whole script", doc.Blocks.Select(b => b.Copy()).ToList());
        if (selection is null) throw new WorkspaceStoreException("Place the cursor in the script or select a passage first.");
        var start = doc.Blocks.FindIndex(b => b.Id == selection.AnchorBlockId);
        var end = doc.Blocks.FindIndex(b => b.Id == selection.HeadBlockId);
        if (start < 0 || end < 0) throw new WorkspaceStoreException("Select a target in the current script.");
        if (scope != ScriptScope.Passage)
        {
            var kind = scope == ScriptScope.Act ? ScriptBlockKind.Act : ScriptBlockKind.Scene;
            var section = Sections(doc.Blocks).LastOrDefault(s => s.Kind == kind && s.Start <= start && start < s.Start + s.Count)
                ?? throw new WorkspaceStoreException($"The cursor is not inside a {scope.ToString().ToLowerInvariant()}.");
            return new(scope, section.Title, doc.Blocks.Skip(section.Start).Take(section.Count).Select(b => b.Copy()).ToList());
        }
        var from = selection.AnchorOffset; var to = selection.HeadOffset;
        if (start > end || start == end && from > to) { (start, end) = (end, start); (from, to) = (to, from); }
        if (from < 0 || to < 0 || from > doc.Blocks[start].Text.Length || to > doc.Blocks[end].Text.Length || start == end && from == to)
            throw new WorkspaceStoreException("Select the passage you want to revise.");
        var scene = Sections(doc.Blocks).LastOrDefault(s => s.Kind == ScriptBlockKind.Scene && s.Start < start && end < s.Start + s.Count);
        if (scene is null) throw new WorkspaceStoreException("Select text within one scene. Use Scene or Act for structural changes.");
        return new(scope, $"Passage in {scene.Title}", doc.Blocks.Skip(start).Take(end - start + 1).Select(b => b.Copy()).ToList(), from, to);
    }
    public static bool Matches(ScriptDocument doc, ScriptTarget target)
    {
        if (target.Scope == ScriptScope.Document) return EquivalentBlocks(doc.Blocks, target.OriginalBlocks);
        if (target.OriginalBlocks.Count == 0) return false;
        var index = doc.Blocks.FindIndex(b => b.Id == target.OriginalBlocks[0].Id);
        if (index < 0) return false;
        var count = target.OriginalBlocks.Count;
        if (target.Scope is ScriptScope.Scene or ScriptScope.Act)
        {
            var section = Sections(doc.Blocks).SingleOrDefault(s => s.Id == target.OriginalBlocks[0].Id);
            if (section is null || section.Count != count) return false;
        }
        return EquivalentBlocks(doc.Blocks.Skip(index).Take(count), target.OriginalBlocks);
    }
    public static void ValidateTarget(ScriptTarget target)
    {
        if (target is null || !Enum.IsDefined(target.Scope) || target.Name is null)
            throw new WorkspaceStoreException("The saved proposal target is invalid.");
        ValidateBlocks(target.OriginalBlocks);
        if (target.Scope != ScriptScope.Document && target.OriginalBlocks.Count == 0 || target.StartOffset < 0 || target.EndOffset < 0 ||
            target.Scope == ScriptScope.Passage && (target.StartOffset > target.OriginalBlocks[0].Text.Length || target.EndOffset > target.OriginalBlocks[^1].Text.Length ||
                target.OriginalBlocks.Count == 1 && target.StartOffset >= target.EndOffset))
            throw new WorkspaceStoreException("The saved proposal selection is invalid.");
    }
    public static List<ScriptSpan> Slice(IReadOnlyList<ScriptSpan> spans, int from, int length)
    {
        List<ScriptSpan> result = []; var pos = 0;
        foreach (var span in spans)
        {
            var a = Math.Max(from, pos); var z = Math.Min(from + length, pos + span.Text.Length);
            if (z > a) result.Add(span with { Text = span.Text.Substring(a - pos, z - a) });
            pos += span.Text.Length;
        }
        return result;
    }
    public static List<ScriptBlock> TargetBlocks(ScriptTarget target)
    {
        var blocks = target.OriginalBlocks.Select(b => b.Copy()).ToList();
        if (target.Scope != ScriptScope.Passage || blocks.Count == 0) return blocks;
        for (int i = 0; i < blocks.Count; i++)
        {
            var from = i == 0 ? target.StartOffset : 0;
            var end = i == blocks.Count - 1 ? target.EndOffset : blocks[i].Text.Length;
            blocks[i] = blocks[i] with { Spans = Slice(blocks[i].Spans, from, end - from) };
        }
        return blocks;
    }
    public static string Markdown(IEnumerable<ScriptBlock> blocks) => string.Join("\n\n", blocks.Select(b =>
    {
        var text = string.Concat(b.Spans.Select(s => (s.Bold ? "**" : "") + (s.Italic ? "*" : "") + s.Text + (s.Italic ? "*" : "") + (s.Bold ? "**" : "")));
        return b.Kind switch { ScriptBlockKind.Act => "# " + text, ScriptBlockKind.Scene => "## " + text,
            ScriptBlockKind.Character => "### " + text, ScriptBlockKind.Parenthetical => "(" + text.Trim('(', ')') + ")", _ => text };
    }));
}
