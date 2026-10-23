using lumibelle.Models;

namespace lumibelle.Services.Story;

public static partial class ScriptAdaptation
{
    // Source links, rather than model-written headings, identify each act's first surviving block.
    // Existing headings (including manually renamed acts) and all screenplay writing keep their identities.
    public static ScriptDocument WithActHeadings(ScriptDocument script, SourceAnalysisRun plan)
    {
        var outline = plan.Reviewed ?? plan.Proposal;
        if (outline is null) return script;
        var positions = script.Blocks.Select((b, i) => (b.Id, Index: i)).ToDictionary(p => p.Id, p => p.Index);
        var anchors = new Dictionary<Guid, int>();
        foreach (var link in script.SourceSceneLinks.Where(l => l.Scene.RunId == plan.Id && l.Scene.ReviewRevision == plan.ReviewRevision && l.Scene.Act < outline.Acts.Count))
        {
            var first = link.BlockIds.Where(positions.ContainsKey).Select(id => script.Blocks[positions[id]])
                .Where(b => b.Kind != ScriptBlockKind.Act).MinBy(b => positions[b.Id]);
            if (first is not null) anchors.TryAdd(first.Id, link.Scene.Act);
        }
        List<ScriptBlock> blocks = [];
        ScriptBlock? heading = null;
        int? previousAct = null;
        Guid? previousHeading = null;
        var scenesSinceHeading = false;
        foreach (var block in script.Blocks)
        {
            if (block.Kind == ScriptBlockKind.Act) { heading = block; scenesSinceHeading = false; }
            if (anchors.TryGetValue(block.Id, out var act))
            {
                if (heading is null || previousAct is null && scenesSinceHeading || previousAct is not null && previousAct != act && heading.Id == previousHeading)
                {
                    heading = ScriptBlock.Create(ScriptBlockKind.Act, $"Act {act + 1}: {outline.Acts[act].Title}");
                    blocks.Add(heading);
                    scenesSinceHeading = false;
                }
                previousAct = act;
                previousHeading = heading.Id;
            }
            blocks.Add(block);
            if (block.Kind == ScriptBlockKind.Scene) scenesSinceHeading = true;
        }
        ScriptStructure.ValidateBlocks(blocks);
        return script with { Blocks = blocks };
    }

    public static void ValidateScenePlan(ScriptSourceScene scene, SourceAnalysisRun plan)
    {
        var outline = plan.Reviewed ?? plan.Proposal;
        if (!Valid(scene) || plan.State != SourceRunState.Accepted || scene.RunId != plan.Id || scene.ReviewRevision != plan.ReviewRevision ||
            outline is null || scene.Act >= outline.Acts.Count || scene.Scene >= outline.Acts[scene.Act].Scenes.Count)
            throw new WorkspaceStoreException("The accepted source outline changed. Review this sequence against its original outline before applying it.");
    }

    public static string? DraftError(IReadOnlyList<ScriptBlock> blocks) =>
        blocks.Any(b => b.Kind == ScriptBlockKind.Scene && !string.IsNullOrWhiteSpace(b.Text)) &&
        blocks.Any(b => b.Kind is ScriptBlockKind.Action or ScriptBlockKind.Dialogue && !string.IsNullOrWhiteSpace(b.Text))
            ? null : "A sequence draft needs a screenplay scene heading and action or dialogue. Inspect or correct the response before applying it.";

    public static string ScreenplayContinuity(ScriptDocument script)
    {
        var headings = string.Join("\n", script.Blocks.Where(b => b.Kind is ScriptBlockKind.Act or ScriptBlockKind.Scene).Select(b => b.Text));
        var writing = ScriptStructure.Markdown(script.Blocks);
        var start = Math.Max(0, writing.Length - 12000);
        if (start > 0 && char.IsLowSurrogate(writing[start])) start++;
        return "CURRENT SCREENPLAY STRUCTURE\n" + headings + "\n\nRECENT SCREENPLAY WRITING FOR CONTINUITY\n" +
            (start > 0 ? "Earlier writing is omitted from this excerpt; use the structure and in-script outline links for orientation.\n" : "") + writing[start..];
    }
    public static SourceAnalysisRun? Accepted(SourceDocument document) => document.Runs.LastOrDefault(r =>
        r.State == SourceRunState.Accepted && (r.Reviewed ?? r.Proposal) is not null && !SourceAnalysis.Stale(document, r));

    public static bool Valid(ScriptSourceScene scene) => scene.RunId != Guid.Empty && scene.ReviewRevision >= 0 && scene.Act >= 0 && scene.Scene >= 0;

    public static ScriptSourceScene[] InScript(ScriptDocument script) => script.SourceSceneLinks
        .Where(l => l.BlockIds.Any(id => script.Blocks.Any(b => b.Id == id))).Select(l => l.Scene).Distinct().ToArray();

    public static async Task<ScriptAdaptationContext> CaptureAsync(FileSourceStore store, ScriptDocument script,
        ScriptOutlineSelection selection, CancellationToken ct = default)
    {
        var source = await store.LoadAsync(script.ProjectId, ct);
        var run = source.Runs.SingleOrDefault(r => r.Id == selection.RunId);
        if (run is null || Accepted(source)?.Id != run.Id || run.State != SourceRunState.Accepted || SourceAnalysis.Stale(source, run) ||
            run.ReviewRevision != selection.ReviewRevision || (run.Reviewed ?? run.Proposal) is not { } outline)
            throw new WorkspaceStoreException("The accepted source outline changed or is unavailable. Refresh the outline in the assistant before requesting writing.");
        if ((selection.Act is null) != (selection.Scene is null) || selection.Act is < 0 || selection.Scene is < 0 ||
            selection.Act is { } act && (act >= outline.Acts.Count || selection.Scene >= outline.Acts[act].Scenes.Count) ||
            selection.DraftScene && selection.Scene is null || selection.RedraftScene && !selection.DraftScene)
            throw new WorkspaceStoreException("Choose a sequence from the accepted source outline.");
        var inScript = InScript(script);
        if (selection.DraftScene) {
            var scene = new ScriptSourceScene(run.Id, run.ReviewRevision, selection.Act!.Value, selection.Scene!.Value);
            if (selection.RedraftScene) SequenceTarget(script, scene);
            else if (inScript.Contains(scene))
                throw new WorkspaceStoreException("This sequence is already in the script. Choose Redraft sequence to replace it, or Revise to make focused changes.");
        }
        return Context(run, script, selection);
    }

    public static ScriptAdaptationContext Context(SourceAnalysisRun run, ScriptDocument script, ScriptOutlineSelection selection)
    {
        var outline = (run.Reviewed ?? run.Proposal)!;
        var numbers = selection.Act is { } ai && selection.Scene is { } si ? outline.Acts[ai].Scenes[si].Sources : [];
        return new(script.ProjectId, run.Id, run.ReviewRevision, run.Title, run.Direction, outline, selection,
            run.Notes.Where(n => n.Sources.Any(numbers.Contains)).ToArray(),
            run.Chunks.Where(c => numbers.Contains(c.Number)).Select(c => new SourceNote(run.Text.Substring(c.Start, c.Length), [c.Number])).ToArray(),
            InScript(script).Where(s => s.RunId == run.Id && s.ReviewRevision == run.ReviewRevision).ToArray());
    }

    public static void ValidateLinks(ScriptDocument script)
    {
        if (script.SourceSceneLinks is null || script.SourceSceneLinks.Count > 10000 || script.SourceSceneLinks.Any(l =>
            l is null || l.Scene is null || !Valid(l.Scene) || l.BlockIds is not { Length: > 0 and <= 10000 } || l.BlockIds.Contains(Guid.Empty)))
            throw new WorkspaceStoreException("The script's source outline links are invalid.");
    }
}
