namespace lumibelle.Models;

public sealed record ScriptOutlineSelection(Guid RunId, long ReviewRevision, int? Act = null, int? Scene = null, bool DraftScene = false, bool RedraftScene = false);
public sealed record ScriptSourceScene(Guid RunId, long ReviewRevision, int Act, int Scene);
public sealed record ScriptSourceSceneLink(ScriptSourceScene Scene, Guid[] BlockIds);
public sealed record ScriptAdaptationContext(Guid ProjectId, Guid RunId, long ReviewRevision, string Title,
    string Direction, SourceOutline Outline, ScriptOutlineSelection Selection, SourceNote[] Notes,
    SourceNote[] Passages, ScriptSourceScene[] InScript)
{
    public string Focus => Selection.Act is { } ai && Selection.Scene is { } si
        ? $"Sequence {ai + 1}.{si + 1}: {Outline.Acts[ai].Scenes[si].Title}" : "Whole outline";
    public string[] ScenesInScript => InScript.Where(s => s.Act < Outline.Acts.Count && s.Scene < Outline.Acts[s.Act].Scenes.Count)
        .Select(s => $"{s.Act + 1}.{s.Scene + 1}: {Outline.Acts[s.Act].Scenes[s.Scene].Title}").ToArray();
}
