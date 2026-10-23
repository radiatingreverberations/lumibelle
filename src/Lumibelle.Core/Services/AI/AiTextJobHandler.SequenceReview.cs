using System.Text;
using lumibelle.Models;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobHandler
{
    private async Task<AiJobOutcome> ReviewSequenceAsync(AiJobContext context, AiTextJobRequest request, AiTextJobResult original, bool allowInference, CancellationToken ct)
    {
        var plan = original.ScenePlan;
        if (plan is null || original.FinishReason != ChatFinishReason.Stop.ToString()) {
            var rejected = original with { Complete = true, Error = "The screenplay did not finish with a saved sequence brief. Its output is preserved; retry explicitly." };
            await PersistAsync(context, () => context.SaveResultAsync(rejected), ct);
            return Outcome(rejected);
        }
        var draft = await context.ReadOperationAsync<AiTextJobRequest>("scene-draft", AiOperationArtifact.Request, ct) ??
            request with { Messages = ScriptSequenceDrafting.WithPlan(request.Messages, plan) };
        var candidate = original;
        ScriptSequenceReview report = new(ScriptSequenceReviewState.Checking, 0, [], []);
        try {
            for (var attempt = 0; attempt <= ScriptSequenceDrafting.MaximumRepairs; attempt++) {
                ScriptSequenceCandidate? screenplay = null;
                ScriptSequenceFinding[] failures;
                try { screenplay = ScriptSequenceDrafting.Parse(candidate.Raw); failures = ScriptSequenceDrafting.CheckStructure(screenplay, plan); }
                catch (WorkspaceStoreException e) { failures = [ScriptSequenceDrafting.Problem("screenplay", e.Message)]; }
                report = new(ScriptSequenceReviewState.Checking, attempt, [], failures);
                await PublishReviewProgress(context, candidate, plan, report, "Step 3 of 3 · checking beat coverage, source facts and continuity…", ct);
                if (screenplay is not null) {
                    var reviewRequest = draft with {
                        Messages = ScriptSequenceDrafting.ReviewMessages(draft, screenplay, plan),
                        Model = draft.Model with { MaxOutputTokens = Math.Min(draft.Model.MaxOutputTokens ?? ScriptSequenceDrafting.ReviewReplyTokens, ScriptSequenceDrafting.ReviewReplyTokens) },
                        ResponseSchema = draft.ResponseSchema is null ? null : ScriptSequenceDrafting.ReviewSchemaFor(plan),
                        ResponseGrammar = draft.ResponseGrammar is null ? null : ScriptSequenceDrafting.ReviewGrammarFor(plan)
                    };
                    string? reviewError = null;
                    for (var check = 0; check < 2; check++) {
                        var auditRequest = check == 0 ? reviewRequest : reviewRequest with { Messages = [.. reviewRequest.Messages,
                            AiTextMessage.Capture(new(ChatRole.User, "Correct the review evidence and return the complete review object. " + reviewError))] };
                        var audit = await SequenceOperationAsync(context, $"sequence-review-{attempt}-{check}", auditRequest, candidate, plan, report,
                            "Step 3 of 3 · reviewing the screenplay", allowInference, ct);
                        if (audit.FinishReason != ChatFinishReason.Stop.ToString()) throw new WorkspaceStoreException("The review did not finish normally. The screenplay remains saved and has not been approved.");
                        try { report = ScriptSequenceDrafting.ParseReview(audit.Raw, screenplay, plan, draft.Payload<ScriptAssistantRequest>().Adaptation!, attempt); reviewError = null; break; }
                        catch (WorkspaceStoreException e) { reviewError = e.Message; report = report with { RawResponse = audit.Raw, ValidationError = e.Message }; }
                    }
                    if (reviewError is not null) throw new WorkspaceStoreException(reviewError);
                }
                else report = report with { State = ScriptSequenceReviewState.NeedsAttention };

                if (report.State == ScriptSequenceReviewState.Passed) {
                    var parsed = AiTextResults.Parse(draft, candidate.Raw, candidate.FinishReason) with { ScenePlan = plan, SequenceReview = report, OpenRouterUsage = candidate.OpenRouterUsage };
                    await PersistAsync(context, () => context.SaveResultAsync(parsed), ct);
                    return Outcome(parsed);
                }
                if (attempt == ScriptSequenceDrafting.MaximumRepairs) break;
                report = report with { State = ScriptSequenceReviewState.Repairing };
                await PublishReviewProgress(context, candidate, plan, report, "Step 3 of 3 · repairing the cited findings…", ct);
                var repairRequest = draft with { Messages = ScriptSequenceDrafting.RepairMessages(draft, candidate.Raw, report.Findings) };
                candidate = await SequenceOperationAsync(context, $"sequence-repair-{attempt + 1}", repairRequest, candidate, plan, report,
                    "Step 3 of 3 · repairing the screenplay", allowInference, ct);
                if (candidate.FinishReason != ChatFinishReason.Stop.ToString()) throw new WorkspaceStoreException("The repair did not finish normally. Its saved output cannot be applied.");
            }
            report = report with { State = ScriptSequenceReviewState.NeedsAttention };
            var result = candidate with { Complete = true, ScenePlan = plan, SequenceReview = report, Value = null,
                Error = "The sequence still has material review findings after one automatic repair. Inspect the saved draft and review before retrying." };
            await PersistAsync(context, () => context.SaveResultAsync(result), ct);
            return Outcome(result);
        }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException or SequenceContinuationException) {
            report = report with { State = ScriptSequenceReviewState.NeedsAttention,
                ValidationError = report.Findings.Any(f => f.Severity == "repair") ? null : e.Message };
            var stopped = candidate with { Complete = true, ScenePlan = plan, SequenceReview = report, Value = null, Error = e.Message };
            await PersistAsync(context, () => context.SaveResultAsync(stopped), ct);
            return e is AiGenerationException or SequenceContinuationException
                ? AiJobOutcome.Attention(e.Message, AiJobRecovery.ContinueRequest) : Outcome(stopped);
        }
    }

    private async Task PublishReviewProgress(AiJobContext context, AiTextJobResult candidate, ScriptScenePlan plan, ScriptSequenceReview report, string label, CancellationToken ct)
    {
        await PersistAsync(context, () => context.SaveResultAsync(candidate with { Complete = false, Value = null, Error = null, ScenePlan = plan, SequenceReview = report }), ct);
        await context.ReportAsync(new(new(GenerationPhase.Generating, label)), checkpoint: true);
    }

    private async Task<AiTextJobResult> SequenceOperationAsync(AiJobContext context, string operation, AiTextJobRequest request,
        AiTextJobResult candidate, ScriptScenePlan plan, ScriptSequenceReview report, string label, bool allowInference, CancellationToken ct)
    {
        if (await context.ReadOperationAsync<AiTextJobResult>(operation, AiOperationArtifact.Output, ct) is { Complete: true } saved) return saved;
        if (!allowInference) throw new SequenceContinuationException("The sequence stopped during its automatic review or repair. Its draft and completed checks are saved; retry explicitly to continue.");
        // A continued stage uses its original prompt/format even if the current
        // builder has changed since the request was submitted.
        request = await context.ReadOperationAsync<AiTextJobRequest>(operation, AiOperationArtifact.Request, ct) ?? request;
        await context.SaveOperationAsync(operation, AiOperationArtifact.Request, request, ct);
        using var timeout = new TextInactivityWatchdog(request.Settings.TimeoutSeconds, ct, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var output = new StringBuilder(); string? finish = null; DateTimeOffset? progressAt = null;
        try {
            await foreach (var update in GenerateAsync(context, request, timeout, linked.Token)) {
                timeout.Observe(update);
                if (update.Response?.FinishReason is { } reason) finish = reason.ToString();
                if (update.Response?.Text is not { Length: > 0 } text) continue;
                output.Append(text);
                if (progressAt is null || clock.GetUtcNow() - progressAt >= TimeSpan.FromSeconds(2)) {
                    var current = report.State == ScriptSequenceReviewState.Repairing
                        ? candidate with { Raw = output.ToString(), Complete = false, ScenePlan = plan, SequenceReview = report }
                        : candidate with { Complete = false, ScenePlan = plan, SequenceReview = report with { RawResponse = output.ToString() } };
                    await context.SaveResultAsync(current);
                    await context.ReportAsync(new(new(GenerationPhase.Generating, label + " · " + request.Model.Name + "…", Current: output.Length, Unit: "characters")));
                    progressAt = clock.GetUtcNow();
                }
            }
            linked.Token.ThrowIfCancellationRequested(); timeout.Stop();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            throw new SequenceContinuationException(timeout.Expired ? timeout.Message : "The automatic review timed out. Saved output remains available.");
        }
        var completed = new AiTextJobResult(output.ToString(), true, finish) { ScenePlan = plan };
        await PersistAsync(context, () => context.SaveOperationAsync(operation, AiOperationArtifact.Output, completed, ct), ct);
        return completed;
    }
    private sealed class SequenceContinuationException(string message) : Exception(message);
}
