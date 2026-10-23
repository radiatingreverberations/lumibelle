using System.Text.Json;
using Bunit;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class ScriptComponentTests
{
    [Fact]
    public async Task BatchActionExplainsDirectAdditionsAndHasWorkingPauseResumeAndStopControls()
    {
        await SaveAcceptedSourceOutline();
        var panel = Panel();
        panel.WaitForElement("#script-operation");
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "draft-remaining" });
        Assert.Empty(panel.FindAll("#script-outline-focus"));
        Assert.Contains("directly to the script", panel.Find(".assistant-target").TextContent);
        await panel.Find(".request-action-button").ClickAsync(new());
        panel.WaitForElement(".script-batch-progress");
        Assert.True(panel.Find("#script-operation").HasAttribute("disabled"));
        Assert.True(panel.Find("#script-instructions").HasAttribute("disabled"));
        Assert.DoesNotContain(_dialogs.FindAll("h2"), e => e.TextContent == "Review script proposal");
        await Click(panel, "Pause after this sequence");
        panel.WaitForAssertion(() => Assert.Contains("Batch paused", panel.Find(".script-batch-progress").TextContent));
        await Click(panel, "Resume batch");
        panel.WaitForAssertion(() => Assert.Contains("Drafting remaining sequences", panel.Find(".script-batch-progress").TextContent));
        await Click(panel, "Stop batch");
        panel.WaitForAssertion(() => Assert.Empty(panel.FindAll(".script-batch-progress")));
        Assert.Equal(ScriptDraftBatchState.Stopped, (await DraftWorkflow.LoadAsync(_store.Document.ProjectId, _ct)).Runs.Last().State);
        Assert.False(panel.Find("#script-operation").HasAttribute("disabled"));
        Assert.Equal(0, _assistant.Calls);
        Assert.Empty(_applications);
    }

    [Fact]
    public async Task StoppedBatchStaysOutOfTheComposerWhenDraftingAgainAndReopeningTheAssistant()
    {
        var plan = await SaveAcceptedSourceOutline();
        var stopped = (await StartBatch(plan)).Runs.Last();
        await DraftWorkflow.StopAsync(_store.Document.ProjectId, stopped.Id, _ct);
        _assistant.Wait = true;
        var panel = Panel();
        panel.WaitForElement("#script-operation");
        Assert.Empty(panel.FindAll(".script-batch-progress"));
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "outline-scene" });
        await panel.Find("#script-outline-focus").ChangeAsync(new() { Value = "0:0" });
        await panel.Find(".request-action-button").ClickAsync(new());
        await _assistant.Called();
        panel.WaitForAssertion(() => {
            Assert.True(panel.Find("#script-operation").HasAttribute("disabled"));
            Assert.Empty(panel.FindAll(".script-batch-progress"));
        });
        await panel.Instance.DisposeAsync(); panel.Dispose();
        var reopened = Panel();
        reopened.WaitForAssertion(() => {
            Assert.True(reopened.Find("#script-operation").HasAttribute("disabled"));
            Assert.Empty(reopened.FindAll(".script-batch-progress"));
        });
        Assert.Equal(ScriptDraftBatchState.Stopped, (await DraftWorkflow.LoadAsync(_store.Document.ProjectId, _ct)).Runs.Last().State);
        Assert.Equal(1, _assistant.Calls);
        Assert.Empty(_applications);
    }

    private ScriptDraftWorkflow DraftWorkflow => Services.GetRequiredService<ScriptDraftWorkflow>();
    [Fact]
    public async Task LocalBatchCannotAppendACompletedDraftWithoutItsAutomaticReviewApproval()
    {
        var plan = await SaveAcceptedSourceOutline();
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>();
        settings.Value = settings.Value with { VllmUrl = "http://llm-server:8000/v1" };
        var before = ScriptStructure.ContextFingerprint(_store.Document);
        var batch = (await DraftWorkflow.StartAsync(_store.Document.Copy(), plan.Id, plan.ReviewRevision,
            new(AiBackend.Vllm, "test/model", "Local") { VllmUrl = settings.Value.VllmUrl }, TextModelSelectionSource.ProjectDefault,
            Guid.NewGuid(), Guid.NewGuid(), "", null, [], _ct)).Runs.Last();
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        await WaitForBatchJob(batch.Pending!.Id);
        batch = (await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.Equal(ScriptDraftBatchState.Paused, batch.State);
        Assert.Equal(0, batch.Added);
        Assert.Contains("not passed its automatic review", batch.Error);
        Assert.Equal(before, ScriptStructure.ContextFingerprint(_store.Document));
    }

    [Fact]
    public async Task BatchResponsesAreReadOnlyUntilTheBatchStops()
    {
        await SaveAcceptedSourceOutline();
        var panel = Panel();
        await panel.Find("#script-operation").ChangeAsync(new() { Value = "draft-remaining" });
        await panel.Find(".request-action-button").ClickAsync(new());
        var batch = (await DraftWorkflow.LoadAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        await WaitForBatchJob(batch.Pending!.Id);
        await Click(panel, "Requests");
        _dialogs.WaitForElement(".assistant-run");
        await Click(panel, "Open response");
        _dialogs.WaitForElement(".script-review-dialog");
        _dialogs.WaitForAssertion(() => {
            Assert.Contains("belongs to the screenplay batch", _dialogs.Find(".review-save-hint").TextContent);
            Assert.DoesNotContain(_dialogs.FindAll("button"), b => b.TextContent.Trim() is "Apply changes" or "Discard" or "New request");
        });
        await DraftWorkflow.StopAsync(_store.Document.ProjectId, batch.Id, _ct);
        _dialogs.WaitForAssertion(() => Assert.Contains(_dialogs.FindAll("button"), b => b.TextContent.Trim() == "Apply changes"));
        Assert.Empty(_applications);
        Assert.Equal(1, _assistant.Calls);
    }

    private Task<ScriptDraftBatches> StartBatch(SourceAnalysisRun plan) => DraftWorkflow.StartAsync(_store.Document.Copy(), plan.Id, plan.ReviewRevision,
        new(AiBackend.OpenRouter, "test/model", "Test model"), TextModelSelectionSource.GlobalDefault, Guid.NewGuid(), Guid.NewGuid(), "Keep the journey quiet.", null, [], _ct);
    private async Task WaitForBatchJob(Guid id)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while ((await _jobs.ReadAsync(timeout.Token)).Jobs.Single(j => j.Id == id).State is AiJobState.Waiting or AiJobState.Running)
            await Task.Delay(10, timeout.Token);
    }
    private ScriptDraftWorkflow ReopenBatchWorkflow() => new(Services.GetRequiredService<FileScriptDraftBatchStore>(), Services.GetRequiredService<FileSourceStore>(),
        _store, Services.GetRequiredService<AiTextJobCapture>(), Services.GetRequiredService<IAiSettingsStore>(), _jobs, _queue, _history);

    [Fact]
    public async Task BatchAppendsInOrderAndUsesSavedWritingAndCapturedSettingsForTheNextSequence()
    {
        var plan = await SaveAcceptedSourceOutline();
        var original = _store.Document.Copy();
        var batch = (await StartBatch(plan)).Runs.Last();
        Assert.Equal(0, _assistant.Calls);
        await DraftWorkflow.AdvanceAsync(original.ProjectId, _ct);
        await WaitForBatchJob(batch.Pending!.Id);
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>();
        settings.Value = settings.Value with { Temperature = .1f };
        batch = (await DraftWorkflow.AdvanceAsync(original.ProjectId, _ct)).Runs.Last();
        Assert.Equal(1, batch.Added);
        var next = batch.Pending!.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var request = next.Payload<ScriptAssistantRequest>();
        Assert.Equal(batch.Id, request.Run.DraftBatchId);
        Assert.Equal(1, request.Run.SourceScene!.Scene);
        Assert.Single(request.Script.SourceSceneLinks);
        Assert.Equal(ScriptStructure.ContextFingerprint(_store.Document), ScriptStructure.ContextFingerprint(request.Script));
        Assert.Equal("Keep the journey quiet.", request.Run.Instructions);
        Assert.Equal(.7f, next.Temperature);
        Assert.Equal(.7f, next.Settings.Temperature);
        Assert.Equal(ScriptStructure.Fingerprint(original.Blocks), ScriptStructure.Fingerprint(_store.Document.Blocks.Take(original.Blocks.Count)));
        await DraftWorkflow.AdvanceAsync(original.ProjectId, _ct);
        await WaitForBatchJob(batch.Pending.Id);
        batch = (await DraftWorkflow.AdvanceAsync(original.ProjectId, _ct)).Runs.Last();
        Assert.Equal(ScriptDraftBatchState.Completed, batch.State);
        Assert.Equal(2, batch.Added);
        Assert.Equal(2, _assistant.Calls);
        Assert.Equal(2, _store.Document.SourceSceneLinks.Count);
        Assert.Equal(2, (await _history.LoadAsync(original.ProjectId, _ct)).Runs.Count(r => r.Applied));
        Assert.Equal(ScriptStructure.Fingerprint(original.Blocks), ScriptStructure.Fingerprint(Assert.Single(_store.Versions).Document.Blocks));
        await ReopenBatchWorkflow().AdvanceAsync(original.ProjectId, _ct);
        Assert.Equal(2, _assistant.Calls);
    }

    [Fact]
    public async Task BatchPausesAfterItsCurrentSequenceAndResumesFromTheSavedCheckpoint()
    {
        var plan = await SaveAcceptedSourceOutline(); _assistant.Wait = true;
        var batch = (await StartBatch(plan)).Runs.Last();
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        await _assistant.Called();
        await DraftWorkflow.PauseAsync(_store.Document.ProjectId, batch.Id, _ct);
        _assistant.Release.TrySetResult();
        await WaitForBatchJob(batch.Pending!.Id);
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        var reopened = ReopenBatchWorkflow();
        batch = (await reopened.LoadAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.Equal(ScriptDraftBatchState.Paused, batch.State);
        Assert.Equal(1, batch.Added); Assert.Null(batch.Pending);
        await reopened.AdvanceAsync(_store.Document.ProjectId, _ct);
        Assert.Equal(1, _assistant.Calls);
        await reopened.ResumeAsync(_store.Document.ProjectId, batch.Id, _ct);
        batch = (await reopened.AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        await reopened.AdvanceAsync(_store.Document.ProjectId, _ct);
        await WaitForBatchJob(batch.Pending!.Id);
        batch = (await reopened.AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.Equal(ScriptDraftBatchState.Completed, batch.State);
        Assert.Equal(2, _assistant.Calls);
    }

    [Fact]
    public async Task BatchFailureDoesNotAppendAndExplicitRetryPreservesCompletedWork()
    {
        var plan = await SaveAcceptedSourceOutline(); _assistant.Invalid = true;
        var before = ScriptStructure.ContextFingerprint(_store.Document);
        var batch = (await StartBatch(plan)).Runs.Last();
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        await WaitForBatchJob(batch.Pending!.Id);
        batch = (await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.Equal(ScriptDraftBatchState.Paused, batch.State);
        Assert.Contains("Invalid mock output", batch.Error);
        Assert.Equal(before, ScriptStructure.ContextFingerprint(_store.Document));
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        Assert.Equal(1, _assistant.Calls);
        var first = batch.Pending!.Id; _assistant.Invalid = false;
        await DraftWorkflow.ResumeAsync(_store.Document.ProjectId, batch.Id, _ct);
        batch = (await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.NotEqual(first, batch.Pending!.Id);
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        await WaitForBatchJob(batch.Pending.Id);
        batch = (await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.Equal(1, batch.Added);
        var after = ScriptStructure.ContextFingerprint(_store.Document);
        await DraftWorkflow.StopAsync(_store.Document.ProjectId, batch.Id, _ct);
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        Assert.Equal(2, _assistant.Calls);
        Assert.Equal(after, ScriptStructure.ContextFingerprint(_store.Document));
    }

    [Fact]
    public async Task ResumingAnExplicitlyApprovedStageKeepsTheBatchRequestIdentity()
    {
        var plan = await SaveAcceptedSourceOutline(); _assistant.Invalid = true;
        var batch = (await StartBatch(plan)).Runs.Last();
        var id = batch.Pending!.Id;
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        await WaitForBatchJob(id);
        batch = (await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.Equal(ScriptDraftBatchState.Paused, batch.State);
        await _jobs.UpdateAsync(id, j => j with { Recovery = AiJobRecovery.ContinueRequest }, _ct);
        _assistant.Invalid = false;
        batch = (await DraftWorkflow.ResumeAsync(_store.Document.ProjectId, batch.Id, _ct)).Runs.Last();
        Assert.Equal(id, batch.Pending!.Id);
        await _assistant.Called(2);
        Assert.Equal(id, _assistant.JobId);
    }

    [Fact]
    public async Task BatchRecoveryDoesNotAppendTwiceWhenScriptWasSavedBeforeItsCheckpoint()
    {
        var plan = await SaveAcceptedSourceOutline();
        var batch = (await StartBatch(plan)).Runs.Last();
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        await WaitForBatchJob(batch.Pending!.Id);
        var run = (await _history.LoadAsync(_store.Document.ProjectId, _ct)).Runs.Single(r => r.Id == batch.Pending.Id);
        await _store.SaveAsync(ScriptProposals.Apply(_store.Document, run, plan), _store.Document.Revision, cancellationToken: _ct);
        var published = ScriptStructure.ContextFingerprint(_store.Document);
        batch = (await ReopenBatchWorkflow().AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.Equal(1, batch.Added);
        Assert.Equal(published, ScriptStructure.ContextFingerprint(_store.Document));
        Assert.Single(_store.Document.SourceSceneLinks);
        Assert.True((await _history.LoadAsync(_store.Document.ProjectId, _ct)).Runs.Single(r => r.Id == run.Id).Applied);
        Assert.Equal(1, _assistant.Calls);
    }

    [Fact]
    public async Task BatchKeepsConcurrentEditsAndRetriesAgainstTheNewSavedScript()
    {
        var plan = await SaveAcceptedSourceOutline();
        var batch = (await StartBatch(plan)).Runs.Last();
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        await WaitForBatchJob(batch.Pending!.Id);
        var edited = _store.Document.Copy(); edited.Blocks[1] = edited.Blocks[1] with { Spans = [new("A handwritten note rests by the gate.")] };
        await _store.SaveAsync(edited, edited.Revision, cancellationToken: _ct);
        var before = ScriptStructure.ContextFingerprint(_store.Document);
        batch = (await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.Equal(ScriptDraftBatchState.Paused, batch.State);
        Assert.Contains("script changed", batch.Error);
        Assert.Equal(before, ScriptStructure.ContextFingerprint(_store.Document));
        var first = batch.Pending!.Id;
        await DraftWorkflow.ResumeAsync(_store.Document.ProjectId, batch.Id, _ct);
        batch = (await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.NotEqual(first, batch.Pending!.Id);
        Assert.Equal(before, ScriptStructure.ContextFingerprint(batch.Pending.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<ScriptAssistantRequest>().Script));
        Assert.Equal(1, _assistant.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchKeepsCompletedResponseWhenEditorOnlyMergesIdenticallyStyledSpans(bool resume)
    {
        var plan = await SaveAcceptedSourceOutline();
        _store.Document.Blocks[1] = _store.Document.Blocks[1] with { Spans = [new("Eira "), new("follows the river."), new("", true)] };
        var batch = (await StartBatch(plan)).Runs.Last();
        var firstJob = batch.Pending!.Id;
        await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct);
        await WaitForBatchJob(firstJob);
        var normalized = _store.Document.Copy();
        normalized.Blocks[1] = normalized.Blocks[1] with { Spans = [new("Eira follows the river.")] };
        await _store.SaveAsync(normalized, normalized.Revision, cancellationToken: _ct);
        if (resume) {
            await DraftWorkflow.PauseAsync(_store.Document.ProjectId, batch.Id, _ct);
            batch = (await DraftWorkflow.ResumeAsync(_store.Document.ProjectId, batch.Id, _ct)).Runs.Last();
        }
        else batch = (await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.Equal(ScriptDraftBatchState.Running, batch.State);
        Assert.Equal(1, batch.Added);
        Assert.Null(batch.Error);
        Assert.Equal(1, _assistant.Calls);
        Assert.Contains(firstJob, _store.Document.AppliedProposalIds);
        Assert.True((await _history.LoadAsync(_store.Document.ProjectId, _ct)).Runs.Single(r => r.Id == firstJob).Applied);
        Assert.Equal(ScriptStructure.Fingerprint(normalized.Blocks), ScriptStructure.Fingerprint(_store.Document.Blocks.Take(normalized.Blocks.Count)));
    }

    [Fact]
    public async Task BatchSkipsAddedPrefixAndRejectsGapsOrChangedOutlines()
    {
        var plan = await SaveAcceptedSourceOutline();
        _store.Document = _store.Document with { SourceSceneLinks = [new(new(plan.Id, plan.ReviewRevision, 0, 1), _store.Document.Blocks.Select(b => b.Id).ToArray())] };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => StartBatch(plan));
        _store.Document = _store.Document with { SourceSceneLinks = [new(new(Guid.NewGuid(), plan.ReviewRevision, 0, 0), _store.Document.Blocks.Select(b => b.Id).ToArray())] };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => StartBatch(plan));
        _store.Document = _store.Document with { SourceSceneLinks = [new(new(plan.Id, plan.ReviewRevision, 0, 0), _store.Document.Blocks.Select(b => b.Id).ToArray())] };
        var batch = (await StartBatch(plan)).Runs.Last();
        Assert.Single(batch.Sequences); Assert.Equal(1, batch.Sequences[0].Scene);
        await Services.GetRequiredService<FileSourceStore>().UpdateAsync(_store.Document.ProjectId, d => Task.FromResult(d with { Runs = [plan with { ReviewRevision = plan.ReviewRevision + 1 }] }), _ct);
        batch = (await DraftWorkflow.AdvanceAsync(_store.Document.ProjectId, _ct)).Runs.Last();
        Assert.Equal(ScriptDraftBatchState.Paused, batch.State);
        Assert.Contains("outline changed", batch.Error);
        Assert.Empty((await _jobs.ReadAsync(_ct)).Jobs);
        Assert.Equal(0, _assistant.Calls);
    }
}
