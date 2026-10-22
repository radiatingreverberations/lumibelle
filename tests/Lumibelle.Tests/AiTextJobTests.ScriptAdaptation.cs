using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    private async Task<SourceAnalysisRun> AcceptedScriptSource()
    {
        const string text = "Chapter 1\nEira finds a brass key.\n\nChapter 2\nShe follows the river.\n\nChapter 3\nThe key opens a garden gate.";
        await SourceStore.SaveAsync(_project, 0, "", text, "", _ct);
        var outline = new SourceOutline { Title = "Reviewed adaptation", Premise = "A key leads Eira home.", Acts = [new() {
            Title = "The journey", Scenes = [new() { Title = "The key", Summary = "Eira discovers the key.", Sources = [1] },
                new() { Title = "The river", Summary = "She chooses to follow the river.", Sources = [2] },
                new() { Title = "The garden", Summary = "She opens the gate.", Sources = [3] }]
        }] };
        var chunks = SourceAnalysis.Split(text, 1000);
        var run = SourceAnalysisTests.Run(text, chunks) with { State = SourceRunState.Accepted, ReviewRevision = 2,
            Proposal = SourceAnalysisTests.Outline(), Reviewed = outline,
            Notes = chunks.Select(c => new SourceNote($"Section {c.Number}: evidence.", [c.Number])).ToList() };
        await SourceStore.UpdateAsync(_project, d => Task.FromResult(d with { Runs = [run] }), _ct);
        return run;
    }
    private ScriptAssistantRequest ScriptFromOutline(SourceAnalysisRun source, bool draft = true, int? scene = 0) => new(
        new() { Operation = WritingOperation.Continue, Backend = _model.Backend, Model = _model.Model,
            Target = ScriptStructure.Capture(_script, ScriptScope.Document, null) }, _script, [], _model) {
        OutlineSelection = new(source.Id, source.ReviewRevision, scene is null ? null : 0, scene, draft)
    };
    private AiTextJobCapture SourceScriptCapture => new(_settings, _projects, _assets, Enhancer, Guidance, providers: _providers, sources: SourceStore);

    [Fact]
    public async Task ScriptCapturesReviewedOutlineAndOnlyFocusedPassagesWithoutRereadingChangedSource()
    {
        var source = await AcceptedScriptSource();
        var request = ScriptFromOutline(source);
        var submission = await SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(), request, true, _ct);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var saved = captured.Payload<ScriptAssistantRequest>();
        Assert.Equal("Reviewed adaptation", saved.Adaptation!.Outline.Title);
        Assert.Equal(3, saved.Adaptation.Outline.Acts[0].Scenes.Count);
        Assert.Equal([1], Assert.Single(saved.Adaptation.Passages).Sources);
        Assert.Contains("brass key", saved.Adaptation.Passages[0].Text);
        Assert.DoesNotContain("garden gate", saved.Adaptation.Passages[0].Text);
        Assert.Equal([1], Assert.Single(saved.Adaptation.Notes).Sources);
        Assert.Equal(new(source.Id, 2, 0, 0), saved.Run.SourceScene);
        Assert.Contains(captured.Messages.SelectMany(m => m.Parts), m => m.Text?.Contains("Draft ONLY the selected outline sequence") == true);
        Assert.Contains(captured.Messages.SelectMany(m => m.Parts), m => m.Text?.Contains("accepted adaptation outline") == true);
        var immediateTask = string.Join("\n", captured.Messages[^1].Parts.Select(p => p.Text));
        Assert.Contains("SELECTED SEQUENCE TO COMPLETE", immediateTask);
        Assert.Contains("Sequence 1.1: The key", immediateTask);
        Assert.Contains("Sequence summary: Eira discovers the key.", immediateTask);
        Assert.Contains("The app adds act headings from the accepted outline", immediateTask);
        Assert.DoesNotContain("Sequence summary: She chooses to follow the river.", immediateTask);
        await SourceStore.SaveAsync(_project, 1, "", "An entirely different story.", "", _ct);
        _providers.Chat.Output = "[{\"kind\":\"Scene\",\"spans\":[{\"text\":\"EXT. RIVER - DAY\"}]},{\"kind\":\"Action\",\"spans\":[{\"text\":\"Eira finds a key.\"}]}]";
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(await Claim(submission), submission.Snapshot, _ct)).State);
        Assert.Contains(_providers.Chat.Messages, m => m.Text?.Contains("brass key") == true);
        Assert.DoesNotContain(_providers.Chat.Messages, m => m.Text?.Contains("An entirely different story") == true);
    }
    [Fact]
    public async Task LaterOutlineScenesKeepTheActAndTimeBoundariesExplicit()
    {
        var source = await AcceptedScriptSource();
        var script = _script with { SourceSceneLinks = [new(new(source.Id, source.ReviewRevision, 0, 0), [_script.Blocks[0].Id])] };
        var request = ScriptFromOutline(source, scene: 1) with { Script = script };
        var captured = (await SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(), request, true, _ct)).Snapshot
            .Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var task = string.Join("\n", captured.Messages[^1].Parts.Select(p => p.Text));
        Assert.Contains("Sequence 1.2: The river", task);
        Assert.Contains("Use Scene headings; omit Act blocks.", task);
        Assert.Contains("without inventing a numeric interval", task);
        Assert.Contains("Reserve the next planned sequence's opening action", task);
    }
    [Fact]
    public async Task WholeOutlineContextDoesNotAttachTheNovelOrEveryAnalysisNote()
    {
        var source = await AcceptedScriptSource();
        var request = ScriptFromOutline(source, false, null);
        var captured = (await SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(), request, true, _ct)).Snapshot
            .Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<ScriptAssistantRequest>();
        Assert.NotNull(captured.Adaptation);
        Assert.Empty(captured.Adaptation.Passages); Assert.Empty(captured.Adaptation.Notes);
        Assert.Null(captured.Run.SourceScene);
    }
    [Theory]
    [InlineData("Action", "A synopsis without a scene heading.")]
    [InlineData("Scene", "EXT. RIVER - DAY")]
    public async Task OutlineSceneNeedsBothAHeadingAndWritingBeforeItCanBeApplied(string kind, string text)
    {
        var source = await AcceptedScriptSource(); var request = ScriptFromOutline(source);
        var submission = await SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(), request, true, _ct);
        _providers.Chat.Output = JsonSerializer.Serialize(new[] { new { kind, spans = new[] { new { text } } } });
        var outcome = await Handler().ExecuteAsync(await Claim(submission), submission.Snapshot, _ct);
        Assert.Equal(AiJobState.NeedsAttention, outcome.State);
        Assert.Empty(ScriptAdaptation.InScript(_script));
    }
    [Theory]
    [InlineData(SourceRunState.Review)]
    [InlineData(SourceRunState.Discarded)]
    [InlineData(SourceRunState.Paused)]
    public async Task ScriptCannotUseUnacceptedSourcePlans(SourceRunState state)
    {
        var source = await AcceptedScriptSource();
        await SourceStore.UpdateAsync(_project, d => Task.FromResult(FileSourceStore.Replace(d, d.Runs[0] with { State = state })), _ct);
        var request = ScriptFromOutline(source);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(), request, true, _ct));
        Assert.Equal(0, _providers.Created); Assert.Empty((await Store.ReadAsync(_ct)).Jobs);
    }
    [Fact]
    public async Task ScriptRejectsChangedOutlineRevisionOrSourceAndNeverSilentlyTruncatesEvidence()
    {
        var source = await AcceptedScriptSource();
        var request = ScriptFromOutline(source);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(),
            request with { OutlineSelection = request.OutlineSelection! with { ReviewRevision = 99 } }, true, _ct));
        _providers.ContextLength = 2000;
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(), request, true, _ct));
        Assert.Contains("context window", error.Message);
        await SourceStore.SaveAsync(_project, 1, "", source.Text, "A changed direction", _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(), request, true, _ct));
        Assert.Equal(0, _providers.Created); Assert.Empty((await Store.ReadAsync(_ct)).Jobs);
    }
    [Fact]
    public async Task OutlineSceneCaptureRejectsReplacementTargetsAndAlreadyAppliedScenes()
    {
        var source = await AcceptedScriptSource(); var request = ScriptFromOutline(source);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(),
            request with { Run = request.Run with { Operation = WritingOperation.Draft } }, true, _ct));
        var script = _script with { SourceSceneLinks = [new(new(source.Id, source.ReviewRevision, 0, 0), [_script.Blocks[0].Id])] };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => SourceScriptCapture.ScriptAsync(request.Run.Id, Guid.NewGuid(), request with { Script = script }, true, _ct));
        Assert.Empty((await Store.ReadAsync(_ct)).Jobs);
    }
}
