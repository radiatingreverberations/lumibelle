using lumibelle.Models;

namespace lumibelle.Services.Story;

public static partial class ScriptAdaptation
{
    public static bool IsRedraft(AssistantRun run) => run.SourceScene is not null && run.Operation == WritingOperation.Rewrite;

    // Capture whole screenplay scenes, including local edits inserted since the first draft.
    // Never infer sequence boundaries from model-written titles or swallow unrelated scenes.
    public static ScriptTarget SequenceTarget(ScriptDocument script, ScriptSourceScene scene)
    {
        if (!Valid(scene)) throw new WorkspaceStoreException("Choose a linked sequence to redraft.");
        var linked = script.SourceSceneLinks.Where(l => l.Scene == scene).SelectMany(l => l.BlockIds).ToHashSet();
        var sections = ScriptStructure.Sections(script.Blocks).Where(s => s.Kind == ScriptBlockKind.Scene).ToArray();
        var selected = sections.Where(s => script.Blocks.Skip(s.Start).Take(s.Count).Any(b => linked.Contains(b.Id))).ToArray();
        if (selected.Length == 0) throw new WorkspaceStoreException("This sequence has no surviving linked scenes to replace. Draft it as a new sequence instead.");
        var start = selected[0].Start;
        var end = selected[^1].Start + selected[^1].Count;
        var blocks = script.Blocks.Skip(start).Take(end - start).ToList();
        var ids = blocks.Select(b => b.Id).ToHashSet();
        if (blocks.Any(b => b.Kind == ScriptBlockKind.Act) ||
            sections.Count(s => s.Start >= start && s.Start < end) != selected.Length ||
            script.SourceSceneLinks.Any(l => l.Scene != scene && l.BlockIds.Any(ids.Contains)))
            throw new WorkspaceStoreException("This sequence's linked scenes overlap other writing or are separated by another sequence or act. Resolve the boundaries before redrafting.");
        return new(ScriptScope.Document, $"Sequence {scene.Act + 1}.{scene.Scene + 1}", blocks.Select(b => b.Copy()).ToList());
    }

    public static bool Matches(ScriptDocument script, AssistantRun run, ScriptTarget target)
    {
        if (!IsRedraft(run)) return ScriptStructure.Matches(script, target);
        try { return ScriptStructure.EquivalentBlocks(SequenceTarget(script, run.SourceScene!).OriginalBlocks, target.OriginalBlocks); }
        catch (WorkspaceStoreException) { return false; }
    }

    public static string WritingContinuity(ScriptAssistantRequest request)
    {
        if (!IsRedraft(request.Run)) return ScreenplayContinuity(request.Script);
        var target = SequenceTarget(request.Script, request.Run.SourceScene!);
        var start = request.Script.Blocks.FindIndex(b => b.Id == target.OriginalBlocks[0].Id);
        var before = request.Script with { Blocks = request.Script.Blocks.Take(start).ToList() };
        var after = ScriptStructure.Markdown(request.Script.Blocks.Skip(start + target.OriginalBlocks.Count));
        var length = Math.Min(after.Length, 6000);
        if (length > 0 && char.IsHighSurrogate(after[length - 1])) length--;
        return "REDRAFT CONTEXT\nThe current version of the selected sequence is excluded. Create a fresh replacement from its source and the author instructions.\n\n" +
            ScreenplayContinuity(before) + "\n\nFOLLOWING WRITING FOR CONTINUITY ONLY\nDo not repeat or replace this later writing.\n" + after[..length];
    }
}
