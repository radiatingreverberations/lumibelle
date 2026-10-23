using Bunit;
using lumibelle.Models;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class ScriptComponentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenSequenceReviewFollowsPlanningThroughScreenplayCompletion(bool invalid)
    {
        await SaveAcceptedSourceOutline();
        _assistant.Wait = _assistant.ScenePlanning = true;
        _assistant.Invalid = invalid;
        var panel = Panel();
        panel.WaitForElement("#script-outline-focus");
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "outline-scene" });
        await panel.Find("#script-outline-act").ChangeAsync(new() { Value = "0" });
        await panel.Find(".request-action-button").ClickAsync(new());
        await _assistant.PartialSaved.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        await Click(panel, "View request");
        _dialogs.WaitForElement(".sequence-planning-review");
        var brief = new lumibelle.Services.AI.ScriptScenePlan(["Eira follows the river."], ["She carries the key."], ["The garden."], "She reaches the gate.", "");
        await _assistant.BeginScreenplayAsync(brief);
        _dialogs.WaitForAssertion(() => {
            Assert.Contains("Writing screenplay", _dialogs.Markup);
            Assert.Contains("Brief saved", _dialogs.Find(".planning-stages .is-complete").TextContent);
            Assert.Contains("Write screenplay", _dialogs.Find(".planning-stages [aria-current='step']").TextContent);
            Assert.Contains("In progress", _dialogs.Find(".planning-stages [aria-current='step']").TextContent);
            Assert.False(_dialogs.Find(".sequence-saved-brief").HasAttribute("open"));
        });
        _assistant.Release.TrySetResult();
        _dialogs.WaitForAssertion(() => {
            Assert.Contains("Review script proposal", _dialogs.Markup);
            Assert.Contains(invalid ? "Stopped" : "Ready for review", _dialogs.Find(".planning-stages [aria-current='step']").TextContent);
            Assert.False(_dialogs.Find(".planning-response-details").HasAttribute("open"));
            if (invalid) {
                Assert.Contains("Screenplay drafting stopped", _dialogs.Find(".planning-stopped").TextContent);
                Assert.Empty(_dialogs.FindAll(".script-review"));
            }
            else Assert.NotEmpty(_dialogs.FindAll(".script-review"));
        });
        Assert.Empty(_applications);
        Assert.Equal(1, _assistant.Calls);
    }

    [Fact]
    public async Task FailedPlanningUsesTheSharedEditorAndOffersExplicitRetryAndAdjustment()
    {
        await SaveAcceptedSourceOutline();
        _assistant.Invalid = _assistant.ScenePlanning = true;
        _assistant.PlanningOutput = System.Text.Json.JsonSerializer.Serialize(new lumibelle.Services.AI.ScriptScenePlan(
            ["Eira finds the key.", "She follows the river."], ["The gate is still closed."], ["The garden's identity."], "She reaches the gate.", ""));
        var panel = Panel();
        panel.WaitForElement("#script-outline-focus");
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "outline-scene" });
        await panel.Find("#script-outline-act").ChangeAsync(new() { Value = "0" });
        await panel.Find(".request-action-button").ClickAsync(new());
        await _assistant.Called();
        panel.WaitForAssertion(() => Assert.Contains("Needs attention", panel.Markup));
        await Click(panel, "Review latest response");
        _dialogs.WaitForAssertion(() => {
            Assert.Contains("Review sequence brief", _dialogs.Markup);
            Assert.Contains("Write screenplay", _dialogs.Find(".planning-stages").TextContent);
            Assert.Contains("Not started", _dialogs.Find(".planning-stages").TextContent);
            Assert.Equal(2, _dialogs.FindAll(".brief-beats li").Count);
            Assert.False(_dialogs.Find(".planning-response-details").HasAttribute("open"));
            Assert.False(_dialogs.Find(".sequence-planning-review .request-details").HasAttribute("open"));
            Assert.DoesNotContain("_requestModelName", _dialogs.Find(".planning-request-heading").TextContent);
            Assert.Contains("Captured model:", _dialogs.Find(".sequence-planning-review .submitted-input").TextContent);
            Assert.Contains("mud-button-filled-primary", _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Retry planning").ClassName);
            Assert.DoesNotContain("Review script proposal", _dialogs.Markup);
            Assert.Contains(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Paste / edit JSON");
        });
        Assert.Empty(_applications);
        await Click(panel, "Paste / edit JSON");
        _dialogs.WaitForElement("#corrected-script-json");
        Assert.Contains("Correct sequence brief JSON", _dialogs.Markup);
        Assert.Equal(_assistant.PlanningOutput, _dialogs.Find("#corrected-script-json").GetAttribute("value"));
        await Click(panel, "Cancel JSON edit");
        await Click(panel, "Adjust request");
        panel.WaitForAssertion(() => Assert.Equal("0:0", panel.Find("#script-outline-focus").GetAttribute("value")));
        Assert.Equal(1, _assistant.Calls);
    }

    [Fact]
    public async Task RedraftUsesAddedSequenceAndReviewRebasesOnlyItsLinkedWriting()
    {
        var plan = await SaveAcceptedSourceOutline();
        var scene = new ScriptSourceScene(plan.Id, plan.ReviewRevision, 0, 0);
        _store.Document = _store.Document with { SourceSceneLinks = [new(scene, _store.Document.Blocks.Select(b => b.Id).ToArray())] };
        var panel = Panel();
        panel.WaitForElement("#script-outline-focus");
        await panel.Find("#script-outline-focus").ChangeAsync(new() { Value = "0:0" });
        Assert.Equal("0:0", panel.Find("#script-outline-focus").GetAttribute("value"));
        Assert.Contains("The gate", panel.Find("#script-outline-focus").TextContent);
        Assert.Equal("redraft-sequence", panel.Find("#script-operation").GetAttribute("value"));
        Assert.Contains("replaces only this sequence", panel.Find(".assistant-target").TextContent);
        await panel.Find("#script-instructions").InputAsync(new() { Value = "Make the pacing quieter." });
        await panel.Find(".request-action-button").ClickAsync(new());
        await _assistant.Called(); Reviewed();
        Assert.True(_assistant.LastRequest!.OutlineSelection!.RedraftScene);
        Assert.Equal(WritingOperation.Rewrite, _assistant.LastRequest.Run.Operation);
        Assert.Equal("Make the pacing quieter.", _assistant.LastRequest.Run.Instructions);
        Assert.Empty(_applications);
        _dialogs.WaitForElement(".is-comparison");
        var changed = _store.Document.Copy();
        changed.Blocks[1] = changed.Blocks[1] with { Spans = [new("Edited while the draft ran.")] };
        _store.Document = changed;
        panel.Render(p => p.Add(c => c.Document, changed));
        _dialogs.WaitForAssertion(() => Assert.DoesNotContain(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Replace sequence"));
        await Click(panel, "Review with current script");
        _dialogs.WaitForAssertion(() => Assert.Contains(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Replace sequence"));
        await Click(panel, "Replace sequence");
        var applied = Assert.Single(_applications);
        Assert.Equal("Edited while the draft ran.", applied.Target.OriginalBlocks[1].Text);
        Assert.True((await LastRun()).Applied);
        Assert.Equal(1, _assistant.Calls);
    }

    [Fact]
    public async Task SequenceSelectionShowsAddedStatusAndChoosesACompatibleActionWithoutJumping()
    {
        var plan = await SaveAcceptedSourceOutline();
        _store.Document = _store.Document with { SourceSceneLinks = [new(new(plan.Id, plan.ReviewRevision, 0, 0), _store.Document.Blocks.Select(b => b.Id).ToArray())] };
        var panel = Panel();
        panel.WaitForElement("#script-outline-focus");
        Assert.Equal("0:1", panel.Find("#script-outline-focus").GetAttribute("value"));
        Assert.Contains("✓ Sequence 1:", panel.Find("#script-outline-focus option[value='0:0']").TextContent);
        Assert.Contains("Not added yet", panel.Find(".script-sequence-status").TextContent);
        Assert.Empty(panel.FindAll("#script-operation option[value='redraft-sequence']"));
        await panel.Find("#script-outline-focus").ChangeAsync(new() { Value = "0:0" });
        Assert.Equal("redraft-sequence", panel.Find("#script-operation").GetAttribute("value"));
        Assert.Contains("✓ Already in script", panel.Find(".script-sequence-status").TextContent);
        Assert.Empty(panel.FindAll("#script-operation option[value='outline-scene']"));
        panel.WaitForAssertion(() => Assert.False(panel.Find(".request-action-button").HasAttribute("disabled")));
        // Even a stale/programmatic change must not silently choose another sequence.
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "outline-scene" });
        Assert.Equal("0:0", panel.Find("#script-outline-focus").GetAttribute("value"));
        Assert.Equal("redraft-sequence", panel.Find("#script-operation").GetAttribute("value"));
        await panel.Find("#script-outline-focus").ChangeAsync(new() { Value = "0:1" });
        Assert.Equal("outline-scene", panel.Find("#script-operation").GetAttribute("value"));
        panel.WaitForAssertion(() => Assert.False(panel.Find(".request-action-button").HasAttribute("disabled")));
        Assert.Equal(0, _assistant.Calls);
    }

    [Fact]
    public async Task RevisionUsesTheSelectedWritingSourceInsteadOfTheNextUndraftedSequence()
    {
        var plan = await SaveAcceptedSourceOutline();
        _store.Document = _store.Document with { SourceSceneLinks = [new(new(plan.Id, plan.ReviewRevision, 0, 0),
            [_store.Document.Blocks[0].Id, _store.Document.Blocks[1].Id])] };
        var panel = Panel();
        panel.WaitForElement("#script-outline-focus");
        Assert.Equal("0:1", panel.Find("#script-outline-focus").GetAttribute("value"));
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "Revise" });
        Assert.Equal("0:0", panel.Find("#script-outline-focus").GetAttribute("value"));
        // An author can deliberately choose another source context after adopting a target.
        await panel.Find("#script-outline-focus").ChangeAsync(new() { Value = "0:1" });
        Assert.Equal("0:1", panel.Find("#script-outline-focus").GetAttribute("value"));
        await panel.Find("#script-scope").ChangeAsync(new() { Value = "Scene" });
        Assert.Equal("0:0", panel.Find("#script-outline-focus").GetAttribute("value"));
        await panel.Find(".script-source-toggle input").ChangeAsync(new() { Value = false });
        await panel.Find(".script-source-toggle input").ChangeAsync(new() { Value = true });
        Assert.Equal("0:0", panel.Find("#script-outline-focus").GetAttribute("value"));
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "outline-scene" });
        Assert.Equal("0:1", panel.Find("#script-outline-focus").GetAttribute("value"));
        Assert.Empty(_applications);
        Assert.Equal(0, _assistant.Calls);
    }

    [Fact]
    public async Task ActSelectionChoosesItsNextUndraftedSceneAndContextStillAllowsTheWholeOutline()
    {
        var plan = await SaveAcceptedSourceOutline();
        plan.Proposal!.Acts.Add(new() { Title = "Return", Scenes = [new() { Title = "Home", Summary = "Eira returns home.", Sources = [2] }] });
        await Services.GetRequiredService<FileSourceStore>().UpdateAsync(_store.Document.ProjectId,
            d => Task.FromResult(d with { Runs = [plan] }), _ct);
        _store.Document = _store.Document with { SourceSceneLinks = [new(new(plan.Id, plan.ReviewRevision, 0, 0), [_store.Document.Blocks[0].Id])] };
        var panel = Panel();
        panel.WaitForElement("#script-outline-act");
        Assert.Equal("0", panel.Find("#script-outline-act").GetAttribute("value"));
        Assert.Equal("0:1", panel.Find("#script-outline-focus").GetAttribute("value"));
        Assert.Contains("Sequence 2: The gate", panel.Find("#script-outline-focus").TextContent);
        Assert.DoesNotContain("Home", panel.Find("#script-outline-focus").TextContent);
        Assert.Contains("1 of 3 sequences added", panel.Find(".script-source-progress").TextContent);
        await panel.Find("#script-outline-act").ChangeAsync(new() { Value = "1" });
        Assert.Equal("1:0", panel.Find("#script-outline-focus").GetAttribute("value"));
        Assert.Contains("Sequence 1: Home", panel.Find("#script-outline-focus").TextContent);
        Assert.DoesNotContain("The gate", panel.Find("#script-outline-focus").TextContent);
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "Discuss" });
        Assert.Empty(panel.FindAll("#script-outline-act"));
        Assert.Equal(2, panel.FindAll("#script-outline-focus optgroup").Count);
        await panel.Find("#script-outline-focus").ChangeAsync(new() { Value = "" });
        Assert.Contains("Whole outline", panel.Find("#script-outline-focus").TextContent);
        Assert.Empty(_applications);
        Assert.Equal(0, _assistant.Calls);
    }

    [Fact]
    public async Task AddingMissingActsUsesTheAcceptedPlanAndKeepsExistingWriting()
    {
        var plan = await SaveAcceptedSourceOutline();
        var before = ScriptStructure.Fingerprint(_store.Document.Blocks);
        _store.Document = _store.Document with { SourceSceneLinks = [new(new(plan.Id, plan.ReviewRevision, 0, 0), [_store.Document.Blocks[0].Id])] };
        var panel = Panel();
        SourceAnalysisRun? received = null;
        panel.Render(p => p.Add(c => c.AddOutlineActs, source =>
        {
            received = source;
            _store.Document = ScriptAdaptation.WithActHeadings(_store.Document, source);
            return Task.FromResult(true);
        }));
        await panel.WaitForElement(".source-add-acts").ClickAsync(new());
        Assert.Equal(plan.Id, received!.Id);
        Assert.Equal(before, ScriptStructure.Fingerprint(_store.Document.Blocks.Where(b => b.Kind != ScriptBlockKind.Act)));
        panel.Render(p => p.Add(c => c.Document, _store.Document));
        Assert.Empty(panel.FindAll(".source-add-acts"));
    }

    [Fact]
    public async Task RetargetingOutlineSceneAfterActInsertionKeepsTheWholeDocumentAppendTarget()
    {
        await SaveAcceptedSourceOutline();
        var panel = Panel();
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "outline-scene" });
        await panel.Find("#script-outline-act").ChangeAsync(new() { Value = "0" });
        await panel.Find("#script-outline-focus").ChangeAsync(new() { Value = "0:1" });
        await panel.Find(".request-action-button").ClickAsync(new());
        await _assistant.Called(); Reviewed();
        var changed = _store.Document with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Act, "Discovery"), .. _store.Document.Blocks] };
        _store.Document = changed;
        panel.Render(p => p.Add(c => c.Document, changed));
        _dialogs.WaitForAssertion(() => {
            Assert.DoesNotContain(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Apply changes");
            var action = Assert.Single(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Review with current script");
            Assert.False(action.HasAttribute("disabled"));
            Assert.Contains("mud-button-filled-primary", action.ClassName);
            Assert.Empty(_dialogs.FindAll(".review-notice button"));
        });
        await Click(panel, "Review with current script");
        Assert.Empty(_applications);
        Assert.Equal(1, _assistant.Calls);
        _dialogs.WaitForAssertion(() => {
            Assert.False(_dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").HasAttribute("disabled"));
            Assert.DoesNotContain(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Review with current script");
        });
        await Click(panel, "Apply changes");
        var applied = Assert.Single(_applications);
        Assert.Equal(ScriptScope.Document, applied.Target.Scope);
        Assert.Equal(ScriptStructure.Fingerprint(changed.Blocks), ScriptStructure.Fingerprint(applied.Target.OriginalBlocks));
    }

    private async Task<SourceAnalysisRun> SaveAcceptedSourceOutline(SourceRunState state = SourceRunState.Accepted)
    {
        var store = Services.GetRequiredService<FileSourceStore>();
        const string text = "Chapter 1\nEira finds the key.\n\nChapter 2\nShe opens the gate.";
        await store.SaveAsync(_store.Document.ProjectId, 0, "", text, "", _ct);
        var outline = SourceAnalysisTests.Outline();
        outline.Acts[0].Scenes.Add(new() { Title = "The gate", Summary = "Eira opens the gate.", Sources = [2] });
        var run = SourceAnalysisTests.Run(text, SourceAnalysis.Split(text, 1000)) with {
            State = state, Proposal = outline
        };
        await store.UpdateAsync(_store.Document.ProjectId, d => Task.FromResult(d with { Runs = [run] }), _ct);
        return run;
    }

    [Fact]
    public async Task EnablingAcceptedOutlineOnEmptyScriptDefaultsToSceneDraftAndPreservesDeliberateDiscussion()
    {
        _store.Document = _store.Document with { Blocks = _store.Document.Blocks.Select(b => b with { Spans = [] }).ToList() };
        await SaveAcceptedSourceOutline();
        var panel = Panel();
        panel.WaitForElement("#script-outline-focus");
        Assert.Equal("outline-scene", panel.Find("#script-operation").GetAttribute("value"));
        await panel.Find(".script-source-toggle input").ChangeAsync(new() { Value = false });
        await panel.Find(".script-source-toggle input").ChangeAsync(new() { Value = true });
        Assert.Equal("outline-scene", panel.Find("#script-operation").GetAttribute("value"));
        Assert.Equal("0:0", panel.Find("#script-outline-focus").GetAttribute("value"));
        Assert.Contains("Draft sequence", panel.Find(".request-action-button").TextContent);
        Assert.Empty(panel.FindAll("#script-scope"));
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "Discuss" });
        await panel.Find(".script-source-toggle input").ChangeAsync(new() { Value = false });
        await panel.Find(".script-source-toggle input").ChangeAsync(new() { Value = true });
        Assert.Equal("Discuss", panel.Find("#script-operation").GetAttribute("value"));
    }

    [Fact]
    public async Task NewlyAcceptedOutlineDefaultsToSceneDraftWhenRefreshedInEmptyScript()
    {
        _store.Document = _store.Document with { Blocks = _store.Document.Blocks.Select(b => b with { Spans = [] }).ToList() };
        var run = await SaveAcceptedSourceOutline(SourceRunState.Review);
        var panel = Panel();
        panel.WaitForElement("button[aria-label='Refresh source outline']");
        Assert.Equal("Draft", panel.Find("#script-operation").GetAttribute("value"));
        await Services.GetRequiredService<FileSourceStore>().UpdateAsync(_store.Document.ProjectId,
            d => Task.FromResult(d with { Runs = [run with { State = SourceRunState.Accepted }] }), _ct);
        await panel.Find("button[aria-label='Refresh source outline']").ClickAsync(new());
        panel.WaitForAssertion(() => Assert.Equal("outline-scene", panel.Find("#script-operation").GetAttribute("value")));
    }

    [Fact]
    public async Task StartOverLeavesOutlineSceneMode()
    {
        await SaveAcceptedSourceOutline();
        var panel = Panel();
        panel.WaitForElement("#script-outline-focus");
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "outline-scene" });
        await panel.Find(".assistant-start-over button").ClickAsync(new());
        Assert.Equal("Draft", panel.Find("#script-operation").GetAttribute("value"));
        Assert.Contains("replacing the whole script", panel.Markup);
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "outline-scene" });
        await panel.Find(".script-source-toggle input").ChangeAsync(new() { Value = false });
        Assert.Equal("Revise", panel.Find("#script-operation").GetAttribute("value"));
    }

    [Fact]
    public async Task ReopeningActiveOutlineDraftRestoresItsSelectedSceneAndAction()
    {
        _assistant.Wait = true;
        await SaveAcceptedSourceOutline();
        var panel = Panel();
        panel.WaitForElement("#script-outline-focus");
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "outline-scene" });
        await panel.Find("#script-outline-act").ChangeAsync(new() { Value = "0" });
        await panel.Find("#script-outline-focus").ChangeAsync(new() { Value = "0:1" });
        await panel.Find(".request-action-button").ClickAsync(new());
        await _assistant.Called();
        await panel.Instance.DisposeAsync(); panel.Dispose();
        var reopened = Panel();
        reopened.WaitForAssertion(() => {
            Assert.Equal("outline-scene", reopened.Find("#script-operation").GetAttribute("value"));
            Assert.Equal("0:1", reopened.Find("#script-outline-focus").GetAttribute("value"));
            Assert.True(reopened.Find("#script-operation").HasAttribute("disabled"));
        });
        Assert.False(_assistant.Token.IsCancellationRequested);
        _assistant.Release.TrySetResult();
        reopened.WaitForAssertion(() => Assert.Contains("Review changes", reopened.Markup));
        Assert.Equal(1, _assistant.Calls);
    }

    [Fact]
    public async Task AcceptedOutlineIsAvailableForRevisionAndSceneDraftingRequiresAnExplicitSceneForUnlinkedWriting()
    {
        var store = Services.GetRequiredService<FileSourceStore>();
        const string text = "Chapter 1\nEira finds the key.\n\nChapter 2\nShe opens the gate.";
        await store.SaveAsync(_store.Document.ProjectId, 0, "", text, "", _ct);
        var outline = SourceAnalysisTests.Outline();
        outline.Acts[0].Scenes.Add(new() { Title = "The gate", Summary = "Eira opens the gate.", Sources = [2] });
        var run = SourceAnalysisTests.Run(text, SourceAnalysis.Split(text, 1000)) with { State = SourceRunState.Accepted, Proposal = outline };
        await store.UpdateAsync(_store.Document.ProjectId, d => Task.FromResult(d with { Runs = [run] }), _ct);
        await _history.SaveRunAsync(_store.Document.ProjectId, new() { Status = AssistantRunStatus.Completed, Operation = WritingOperation.Revise,
            Target = ScriptStructure.Capture(_store.Document, ScriptScope.Document, null), Proposal = ScriptFixtures.Document().Blocks }, _ct);
        var panel = Panel();
        panel.WaitForElement("#script-outline-focus");
        Assert.Equal("Revise", panel.Find("#script-operation").GetAttribute("value"));
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "outline-scene" });
        Assert.Equal("", panel.Find("#script-outline-focus").GetAttribute("value"));
        Assert.True(panel.Find(".request-action-button").HasAttribute("disabled"));
        Assert.Contains("Choose an act and sequence above", panel.Find(".script-submit-reason").TextContent);
        Assert.True(panel.Find("#script-outline-focus").HasAttribute("disabled"));
        await panel.Find("#script-outline-act").ChangeAsync(new() { Value = "0" });
        await panel.Find("#script-outline-focus").ChangeAsync(new() { Value = "0:1" });
        panel.WaitForAssertion(() => Assert.False(panel.Find(".request-action-button").HasAttribute("disabled")));
        Assert.Contains("Draft sequence", panel.Find(".request-action-button").TextContent);
        await panel.Find(".request-action-button").ClickAsync(new());
        await _assistant.Called();
        Assert.NotNull(_assistant.LastRequest!.Adaptation);
        Assert.Equal(1, _assistant.LastRequest.Adaptation.Selection.Scene);
        Assert.Equal(ScriptScope.Document, _assistant.LastRequest.Run.Target.Scope);
        Assert.Equal(WritingOperation.Continue, _assistant.LastRequest.Run.Operation);
        Assert.Equal(new(run.Id, 0, 0, 1), _assistant.LastRequest.Run.SourceScene);
        Assert.Empty(_store.Document.SourceSceneLinks);
        Assert.Empty(_applications);
    }
}
