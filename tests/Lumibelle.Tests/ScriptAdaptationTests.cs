using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ScriptAdaptationTests
{
    [Fact]
    public void PlannedSceneDraftUsesBoundedRecentWritingAndHeadingsInsteadOfRepeatingTheWholeScript()
    {
        var script = new ScriptDocument { ProjectId = Guid.NewGuid(), Blocks = [ScriptBlock.Create(ScriptBlockKind.Scene, "INT. FIRST - DAY"),
            ScriptBlock.Create(ScriptBlockKind.Action, "A distinct early event. " + new string('a', 20000)),
            ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. LAST - NIGHT"), ScriptBlock.Create(ScriptBlockKind.Action, "The latest decision.")] };
        var request = new ScriptAssistantRequest(new() { Operation = WritingOperation.Continue, SourceScene = new(Guid.NewGuid(), 1, 0, 2),
            Target = ScriptStructure.Capture(script, ScriptScope.Document, null) }, script, []);
        var messages = lumibelle.Services.AI.ScriptAssistant.BuildMessages(request);
        var text = string.Join("\n", messages.Select(m => m.Text));
        Assert.Contains("INT. FIRST - DAY", text); Assert.Contains("The latest decision.", text);
        Assert.Contains("Earlier writing is omitted", text); Assert.DoesNotContain("A distinct early event.", text);
        // Bound the screenplay excerpt itself; writing/format guidance has its own size.
        Assert.True(messages[1].Text!.Length < 12500);
    }
    [Fact]
    public void OutlineDraftIsOnlyTrackedAfterApplyAndAppendsWithoutReplacingExistingWriting()
    {
        var document = ScriptFixtures.Document();
        var scene = new ScriptSourceScene(Guid.NewGuid(), 2, 0, 1);
        var run = new AssistantRun { Operation = WritingOperation.Continue, Status = AssistantRunStatus.Completed,
            Target = ScriptStructure.Capture(document, ScriptScope.Document, null), SourceScene = scene,
            Proposal = [ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. RIVER - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "Eira follows the river.")] };
        Assert.Empty(ScriptAdaptation.InScript(document));
        var applied = ScriptProposals.Apply(document, run);
        Assert.Equal(ScriptStructure.Fingerprint(document.Blocks), ScriptStructure.Fingerprint(applied.Blocks.Take(document.Blocks.Count)));
        Assert.Equal(document.Blocks.Count + 2, applied.Blocks.Count);
        Assert.Equal(scene, Assert.Single(ScriptAdaptation.InScript(applied)));
        Assert.All(Assert.Single(applied.SourceSceneLinks).BlockIds, id => Assert.Contains(applied.Blocks, b => b.Id == id));
        var duplicate = run with { Id = Guid.NewGuid(), Target = ScriptStructure.Capture(applied, ScriptScope.Document, null) };
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(applied, duplicate));
        var removed = applied with { Blocks = document.Blocks };
        Assert.Empty(ScriptAdaptation.InScript(removed));
        var copy = applied.Copy(); copy.SourceSceneLinks[0].BlockIds[0] = Guid.NewGuid();
        Assert.NotEqual(applied.SourceSceneLinks[0].BlockIds[0], copy.SourceSceneLinks[0].BlockIds[0]);
    }
    [Fact]
    public void CorrectingGeneratedJsonKeepsTheOutlineSceneProvenance()
    {
        var run = new AssistantRun { Operation = WritingOperation.Continue, Status = AssistantRunStatus.Failed,
            SourceScene = new(Guid.NewGuid(), 1, 0, 0) };
        var corrected = ScriptProposals.CorrectJson(run, "[{\"kind\":\"Scene\",\"spans\":[{\"text\":\"EXT. RIVER - DAY\"}]}]", Guid.NewGuid());
        Assert.Equal(run.SourceScene, corrected.SourceScene);
        Assert.Equal(run.Id, corrected.CorrectedFromRunId);
    }
}
