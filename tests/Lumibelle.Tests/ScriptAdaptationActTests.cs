using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ScriptAdaptationActTests
{
    private static SourceAnalysisRun Plan() => SourceAnalysisTests.Run("A story.", []) with
    {
        State = SourceRunState.Accepted, ReviewRevision = 2,
        Proposal = new() { Acts = [
            new() { Title = "Discovery", Scenes = [new() { Title = "Key" }, new() { Title = "Gate" }] },
            new() { Title = "Return", Scenes = [new() { Title = "Home" }] }
        ] }
    };
    private static AssistantRun Draft(ScriptDocument document, SourceAnalysisRun plan, int act = 0, int scene = 0, bool modelAct = false) => new()
    {
        Status = AssistantRunStatus.Completed, Operation = WritingOperation.Continue,
        Target = ScriptStructure.Capture(document, ScriptScope.Document, null),
        SourceScene = new(plan.Id, plan.ReviewRevision, act, scene),
        Proposal = [.. (modelAct ? new[] { ScriptBlock.Create(ScriptBlockKind.Act, "The model's wrong act") } : []),
            ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. RIVER - DAY"),
            ScriptBlock.Create(ScriptBlockKind.Action, "Eira follows the river.")]
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyingFirstSceneUsesTheAcceptedActWhetherTheModelOmitsOrInventsOne(bool modelAct)
    {
        var document = new ScriptDocument { ProjectId = Guid.NewGuid() };
        var plan = Plan();
        var result = ScriptProposals.ApplyWithChanges(document, Draft(document, plan, modelAct: modelAct), plan);
        var act = Assert.Single(result.Document.Blocks, b => b.Kind == ScriptBlockKind.Act);
        Assert.Equal("Act 1: Discovery", act.Text);
        Assert.Equal(act.Id, result.Document.Blocks[0].Id);
        Assert.Contains(result.Changes, c => c.BlockId == act.Id);
        Assert.DoesNotContain(act.Id, Assert.Single(result.Document.SourceSceneLinks).BlockIds);
    }

    [Fact]
    public void LaterSceneKeepsActIdentityAndManualTitleAndNewActStartsItsOwnGroup()
    {
        var document = new ScriptDocument { ProjectId = Guid.NewGuid() }; var plan = Plan();
        var first = ScriptProposals.Apply(document, Draft(document, plan), plan);
        first.Blocks[0] = first.Blocks[0] with { Spans = [new("My revised act title")] };
        var second = ScriptProposals.Apply(first, Draft(first, plan, scene: 1, modelAct: true), plan);
        Assert.Equal(ScriptStructure.Fingerprint(first.Blocks), ScriptStructure.Fingerprint(second.Blocks.Take(first.Blocks.Count)));
        Assert.Single(second.Blocks, b => b.Kind == ScriptBlockKind.Act);
        var third = ScriptProposals.Apply(second, Draft(second, plan, act: 1), plan);
        var acts = third.Blocks.Where(b => b.Kind == ScriptBlockKind.Act).ToArray();
        Assert.Equal(["My revised act title", "Act 2: Return"], acts.Select(a => a.Text));
        var sections = ScriptStructure.Sections(third.Blocks).Where(s => s.Kind == ScriptBlockKind.Scene).ToArray();
        Assert.Equal([acts[0].Id, acts[0].Id, acts[1].Id], sections.Select(s => s.ActId!.Value));
        Assert.Equal(ScriptStructure.Fingerprint(third.Blocks), ScriptStructure.Fingerprint(ScriptAdaptation.WithActHeadings(third, plan).Blocks));
    }

    [Fact]
    public void AddingMissingActsPreservesEveryExistingBlockAndLinkAndDoesNotInventUndraftedActs()
    {
        var document = new ScriptDocument { ProjectId = Guid.NewGuid() }; var plan = Plan();
        // Older drafts were saved without passing an outline to the application step.
        var first = ScriptProposals.Apply(document, Draft(document, plan));
        var second = ScriptProposals.Apply(first, Draft(first, plan, scene: 1));
        var repaired = ScriptAdaptation.WithActHeadings(second, plan);
        Assert.Single(repaired.Blocks, b => b.Kind == ScriptBlockKind.Act);
        Assert.Equal(ScriptStructure.Fingerprint(second.Blocks), ScriptStructure.Fingerprint(repaired.Blocks.Where(b => b.Kind != ScriptBlockKind.Act)));
        Assert.Same(second.SourceSceneLinks, repaired.SourceSceneLinks);
        Assert.Equal(2, ScriptAdaptation.InScript(repaired).Length);
        // A draft captured before the structural edit remains available but needs explicit retargeting.
        var pending = Draft(second, plan, act: 1);
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(repaired, pending, plan));
        var retargeted = pending with { Target = ScriptStructure.Capture(repaired, ScriptScope.Document, null) };
        Assert.Equal(2, ScriptProposals.Apply(repaired, retargeted, plan).Blocks.Count(b => b.Kind == ScriptBlockKind.Act));
    }

    [Fact]
    public void UnrelatedManualWritingAndItsActArePreservedBeforeNewSourceAct()
    {
        var document = new ScriptDocument { ProjectId = Guid.NewGuid(), Blocks = [
            ScriptBlock.Create(ScriptBlockKind.Act, "An existing prologue"),
            ScriptBlock.Create(ScriptBlockKind.Scene, "INT. ROOM - NIGHT"),
            ScriptBlock.Create(ScriptBlockKind.Action, "Earlier manual writing.")
        ] }; var plan = Plan();
        var applied = ScriptProposals.Apply(document, Draft(document, plan), plan);
        Assert.Equal(ScriptStructure.Fingerprint(document.Blocks), ScriptStructure.Fingerprint(applied.Blocks.Take(document.Blocks.Count)));
        Assert.Equal("Act 1: Discovery", applied.Blocks[3].Text);
    }

    [Fact]
    public void OtherOutlineRevisionsAndDeletedSceneLinksDoNotCreateActs()
    {
        var document = new ScriptDocument { ProjectId = Guid.NewGuid() }; var plan = Plan();
        var first = ScriptProposals.Apply(document, Draft(document, plan));
        Assert.Equal(ScriptStructure.Fingerprint(first.Blocks), ScriptStructure.Fingerprint(ScriptAdaptation.WithActHeadings(first, plan with { ReviewRevision = 3 }).Blocks));
        Assert.Empty(ScriptAdaptation.WithActHeadings(first with { Blocks = [] }, plan).Blocks);
        Assert.Throws<WorkspaceStoreException>(() => ScriptProposals.Apply(first, Draft(first, plan, scene: 1), plan with { ReviewRevision = 3 }));
    }
}
