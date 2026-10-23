using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    private static string LongSyntheticBrief => JsonSerializer.Serialize(ScriptScenePlanning.Parse(PlannedScene)! with {
        Beats = [string.Join(" ", Enumerable.Repeat("Eira finds a brass key and follows the river.", 200))]
    }, AtomicJsonFile.Options);

    private Task BriefAttention(AiJobContext context, AiJobRecovery recovery = AiJobRecovery.ApproveBrief) =>
        Store.UpdateAsync(context.Job.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = recovery }, _ct);

    private async Task<AiJobContext> ContinueBrief(Guid id)
    {
        await Store.RequeueAsync(id, _ct);
        return Context((await Store.ClaimNextAsync(AiBackend.Vllm, 1, _ct))!, false);
    }

    [Fact]
    public async Task OversizedBriefNeedsApprovalThenContinuesWithoutPlanningAgainOrTruncatingIt()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        _providers.Chat.Outputs.Enqueue(LongSyntheticBrief);
        var context = await Claim(submission);
        var held = await Handler().ExecuteAsync(context, submission.Snapshot, _ct);
        Assert.Equal(AiJobRecovery.ApproveBrief, held.Recovery); Assert.Equal(1, _providers.Created);
        Assert.Null(await context.ReadOperationAsync<AiTextJobRequest>("scene-draft", AiOperationArtifact.Request, _ct));
        await BriefAttention(context);
        await ScriptScenePlanApproval.ApproveAsync(Store, context.Job.Id, _ct);
        Assert.Equal(1, _providers.Created);
        _providers.Chat.Outputs.Enqueue(ScriptSequenceDraftingTests.Candidate);
        _providers.Chat.Outputs.Enqueue(ScriptSequenceDraftingTests.Review(severity: "none"));
        var resumed = await ContinueBrief(context.Job.Id);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(resumed, submission.Snapshot, _ct)).State);
        Assert.Equal(3, _providers.Created);
        Assert.Equal(LongSyntheticBrief, (await context.ReadOperationAsync<AiTextJobResult>("scene-plan", AiOperationArtifact.Output, _ct))!.Raw);
        var result = (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!;
        Assert.Equal(ScriptScenePlanning.Inspect(LongSyntheticBrief).Plan!.Beats, result.ScenePlan!.Beats);
    }

    [Fact]
    public async Task ManualJsonCorrectionIsSavedSeparatelyAndOnlyItsApprovalStartsTheNextStep()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        var broken = PlannedScene.TrimEnd()[..^1];
        _providers.Chat.Outputs.Enqueue(broken);
        var context = await Claim(submission);
        await Assert.ThrowsAsync<AiGenerationException>(() => Handler().ExecuteAsync(context, submission.Snapshot, _ct));
        await BriefAttention(context, AiJobRecovery.GenerateAgain);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => ScriptScenePlanApproval.ApproveAsync(Store, context.Job.Id, _ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => ScriptScenePlanApproval.SaveCorrectionAsync(Store, context.Job.Id, broken, _ct));
        await ScriptScenePlanApproval.SaveCorrectionAsync(Store, context.Job.Id, PlannedScene, _ct);
        Assert.Equal(1, _providers.Created);
        Assert.Single((await context.ReadAsync<ScriptBriefCorrection[]>(AiJobArtifact.SequenceBriefCorrections, _ct))!);
        Assert.Equal(broken, (await context.ReadOperationAsync<AiTextJobResult>("scene-plan", AiOperationArtifact.Output, _ct))!.Raw);
        await ScriptScenePlanApproval.ApproveAsync(Store, context.Job.Id, _ct);
        _providers.Chat.Outputs.Enqueue(ScriptSequenceDraftingTests.Candidate);
        _providers.Chat.Outputs.Enqueue(ScriptSequenceDraftingTests.Review(severity: "none"));
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(await ContinueBrief(context.Job.Id), submission.Snapshot, _ct)).State);
        Assert.Equal(3, _providers.Created);
    }

    [Fact]
    public async Task AnotherManualCorrectionInvalidatesTheEarlierApproval()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        _providers.Chat.Outputs.Enqueue(LongSyntheticBrief);
        var context = await Claim(submission);
        await Handler().ExecuteAsync(context, submission.Snapshot, _ct); await BriefAttention(context);
        await ScriptScenePlanApproval.ApproveAsync(Store, context.Job.Id, _ct);
        await ScriptScenePlanApproval.SaveCorrectionAsync(Store, context.Job.Id, PlannedScene, _ct);
        var held = await Handler().ExecuteAsync(await ContinueBrief(context.Job.Id), submission.Snapshot, _ct);
        Assert.Equal(AiJobRecovery.ApproveBrief, held.Recovery); Assert.Equal(1, _providers.Created);
    }

    [Fact]
    public async Task ApprovalCannotSendAWriterRequestThatExceedsTheAvailableContext()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        _providers.Chat.Outputs.Enqueue(LongSyntheticBrief);
        var context = await Claim(submission);
        await Handler().ExecuteAsync(context, submission.Snapshot, _ct); await BriefAttention(context);
        await ScriptScenePlanApproval.ApproveAsync(Store, context.Job.Id, _ct);
        _providers.ContextLength = 1000;
        var resumed = await ContinueBrief(context.Job.Id);
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => Handler().ExecuteAsync(resumed, submission.Snapshot, _ct));
        Assert.Contains("context window", error.Message); Assert.Equal(1, _providers.Created);
    }

    [Fact]
    public async Task ThirteenContinuityFactsContinueAutomaticallyAndRemainInTheWriterInput()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        var plan = ScriptScenePlanning.Parse(PlannedScene)! with {
            Continuity = Enumerable.Range(1, 13).Select(i => $"The garden has marker {i}.").ToArray()
        };
        var raw = JsonSerializer.Serialize(plan, AtomicJsonFile.Options);
        foreach (var output in new[] { raw, ScriptSequenceDraftingTests.Candidate, ScriptSequenceDraftingTests.Review(severity: "none") })
            _providers.Chat.Outputs.Enqueue(output);
        var context = await Claim(submission);
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(context, submission.Snapshot, _ct)).State);
        Assert.Equal(3, _providers.Created);
        Assert.Null(await context.ReadAsync<ScriptBriefApproval>(AiJobArtifact.SequenceBriefApproval, _ct));
        var result = (await context.ReadAsync<AiTextJobResult>(AiJobArtifact.Result, _ct))!;
        Assert.Equal(plan.Continuity, result.ScenePlan!.Continuity);
        var draft = (await context.ReadOperationAsync<AiTextJobRequest>("scene-draft", AiOperationArtifact.Request, _ct))!;
        foreach (var fact in plan.Continuity)
            Assert.Contains(draft.Messages.SelectMany(m => m.Parts), p => p.Text?.Contains(fact, StringComparison.Ordinal) == true);
        Assert.Equal(raw, (await context.ReadOperationAsync<AiTextJobResult>("scene-plan", AiOperationArtifact.Output, _ct))!.Raw);
    }

    [Fact]
    public async Task APreviouslyPausedCountWarningStillChecksContextWhenExplicitlyContinued()
    {
        var submission = await LocalSceneRequest(reviewed: true);
        var plan = ScriptScenePlanning.Parse(PlannedScene)! with {
            Continuity = Enumerable.Range(1, 13).Select(i => $"The garden has marker {i}.").ToArray()
        };
        var context = await Claim(submission);
        var saved = new AiTextJobResult(JsonSerializer.Serialize(plan, AtomicJsonFile.Options), true, "stop");
        await context.SaveOperationAsync("scene-plan", AiOperationArtifact.Output, saved, _ct);
        await context.SaveResultAsync(saved with { ScenePlanning = true, Error = "The earlier continuity entry limit was exceeded." });
        await BriefAttention(context);
        Assert.Empty(ScriptScenePlanning.Inspect(saved.Raw).Warnings);
        await ScriptScenePlanApproval.ApproveAsync(Store, context.Job.Id, _ct);
        _providers.ContextLength = 1000;
        var resumed = await ContinueBrief(context.Job.Id);
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => Handler().ExecuteAsync(resumed, submission.Snapshot, _ct));
        Assert.Contains("context window", error.Message);
        Assert.Equal(0, _providers.Created);
        Assert.Null(await context.ReadOperationAsync<AiTextJobRequest>("scene-draft", AiOperationArtifact.Request, _ct));
        Assert.Equal(saved.Raw, (await context.ReadOperationAsync<AiTextJobResult>("scene-plan", AiOperationArtifact.Output, _ct))!.Raw);
    }
}
