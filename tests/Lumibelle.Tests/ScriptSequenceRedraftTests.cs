using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ScriptSequenceRedraftTests
{
    private readonly Guid _plan = Guid.NewGuid();
    private ScriptSourceScene Scene(int index) => new(_plan, 2, 0, index);
    private ScriptDocument Document()
    {
        var blocks = new List<ScriptBlock> {
            ScriptBlock.Create(ScriptBlockKind.Act, "A manually named act"),
            ScriptBlock.Create(ScriptBlockKind.Scene, "INT. HOME - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "Eira finds the key."),
            ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. RIVER - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "OLD DRAFT: She waits."),
            ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. BRIDGE - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "OLD DRAFT: She crosses."),
            ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. GARDEN - NIGHT"), ScriptBlock.Create(ScriptBlockKind.Action, "She opens the gate.") };
        return new() { ProjectId = Guid.NewGuid(), Blocks = blocks, SourceSceneLinks = [
            new(Scene(0), blocks.Skip(1).Take(2).Select(b => b.Id).ToArray()),
            new(Scene(1), blocks.Skip(3).Take(4).Select(b => b.Id).ToArray()),
            new(Scene(2), blocks.Skip(7).Take(2).Select(b => b.Id).ToArray())] };
    }
    private AssistantRun Redraft(ScriptDocument doc) => new() {
        Operation = WritingOperation.Rewrite, Status = AssistantRunStatus.Completed, SourceScene = Scene(1),
        Target = ScriptAdaptation.SequenceTarget(doc, Scene(1)), SourceFingerprint = ScriptStructure.ContextFingerprint(doc),
        Proposal = [ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. RIVER - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "She takes a boat.")] };

    [Fact]
    public void MiddleSequenceReplacementKeepsNeighborsActIdentityAndUpdatedSourceLinks()
    {
        var doc = Document();
        var extra = ScriptBlock.Create(ScriptBlockKind.Action, "A local edit inside the sequence.");
        doc.Blocks.Insert(5, extra);
        var run = Redraft(doc);
        Assert.Contains(run.Target.OriginalBlocks, b => b.Id == extra.Id);
        var applied = ScriptProposals.Apply(doc, run);
        Assert.Equal(ScriptStructure.Fingerprint(doc.Blocks.Take(3)), ScriptStructure.Fingerprint(applied.Blocks.Take(3)));
        Assert.Equal(ScriptStructure.Fingerprint(doc.Blocks.TakeLast(2)), ScriptStructure.Fingerprint(applied.Blocks.TakeLast(2)));
        Assert.Equal(doc.Blocks[3].Id, applied.Blocks[3].Id); // Matching location retains its downstream identity.
        Assert.Equal(7, applied.Blocks.Count);
        Assert.Equal(3, ScriptAdaptation.InScript(applied).Length);
        var link = Assert.Single(applied.SourceSceneLinks, l => l.Scene == Scene(1));
        Assert.Equal(applied.Blocks.Skip(3).Take(2).Select(b => b.Id), link.BlockIds);
        Assert.DoesNotContain(extra.Id, link.BlockIds);
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(applied, run));
    }

    [Fact]
    public void LaterEditsRemainUntouchedButChangedOrDeletedSequenceRequiresAnotherReview()
    {
        var doc = Document(); var run = Redraft(doc);
        var later = doc.Copy(); later.Blocks[^1] = later.Blocks[^1] with { Spans = [new("A later author edit.")] };
        Assert.True(ScriptAdaptation.Matches(later, run, run.Target));
        Assert.Equal("A later author edit.", ScriptProposals.Apply(later, run).Blocks[^1].Text);
        var changed = doc.Copy(); changed.Blocks[4] = changed.Blocks[4] with { Spans = [new("An edit during generation.")] };
        Assert.False(ScriptAdaptation.Matches(changed, run, run.Target));
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(changed, run));
        var deleted = doc with { Blocks = doc.Blocks.Take(3).Concat(doc.Blocks.Skip(7)).ToList() };
        Assert.False(ScriptAdaptation.Matches(deleted, run, run.Target));
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(deleted, run));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterleavedOrSharedSequenceLinksCannotReplaceUnrelatedWriting(bool shared)
    {
        var doc = Document();
        if (shared) doc.SourceSceneLinks.Add(new(Scene(2), [doc.Blocks[4].Id]));
        else doc.Blocks.Insert(5, ScriptBlock.Create(ScriptBlockKind.Scene, "INT. UNLINKED SCENE - DAY"));
        Assert.Throws<WorkspaceStoreException>(() => ScriptAdaptation.SequenceTarget(doc, Scene(1)));
    }

    [Fact]
    public void NewPromptUsesSourceAndSurroundingContinuityWithoutTheOldDraft()
    {
        var doc = Document(); var run = Redraft(doc);
        var outline = new SourceOutline { Acts = [new() { Scenes = [new(), new() { Summary = "She crosses the river." }, new()] }] };
        var request = new ScriptAssistantRequest(run with { Instructions = "Use a quieter tone." }, doc, []) {
            Adaptation = new(doc.ProjectId, _plan, 2, "A journey", "", outline, new(_plan, 2, 0, 1, true, true), [], [], []) };
        var messages = ScriptAssistant.BuildMessages(request, includeSourceEvidence: false);
        var text = string.Join("\n", messages.Select(m => m.Text));
        Assert.Contains("Redraft ONLY the selected outline sequence", text);
        Assert.Contains("Eira finds the key.", text);
        Assert.Contains("She opens the gate.", text);
        Assert.Contains("Use a quieter tone.", text);
        Assert.DoesNotContain("OLD DRAFT", text);
        Assert.DoesNotContain("append after the screenplay", text);
        var planning = string.Join("\n", ScriptScenePlanning.Messages(request).Select(m => m.Text));
        Assert.DoesNotContain("OLD DRAFT", planning);
        Assert.Contains("fresh redraft", planning);
    }
}
