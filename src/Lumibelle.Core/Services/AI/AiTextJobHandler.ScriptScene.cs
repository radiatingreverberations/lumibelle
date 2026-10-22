using System.Text;
using lumibelle.Models;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobHandler
{
    private const string ScenePlanOperation = "scene-plan";
    private async Task<(AiTextJobRequest Request, ScriptScenePlan Plan)> PrepareSceneAsync(AiJobContext context,
        AiTextJobRequest request, TextInactivityWatchdog timeout, CancellationToken ct)
    {
        var result = await context.ReadOperationAsync<AiTextJobResult>(ScenePlanOperation, AiOperationArtifact.Output, ct);
        if (result is not { Complete: true }) {
            var planning = request with { Messages = request.ScenePlanMessages!, ScenePlanMessages = null,
                ResponseSchema = request.ResponseSchema is null ? null : ScriptScenePlanning.PlanningSchema,
                ResponseGrammar = request.ResponseGrammar is null ? null : ScriptScenePlanning.PlanningGrammar,
                Model = request.Model with { MaxOutputTokens = Math.Min(request.Model.MaxOutputTokens ?? ScriptScenePlanning.PlanningReplyTokens, ScriptScenePlanning.PlanningReplyTokens) } };
            await context.SaveOperationAsync(ScenePlanOperation, AiOperationArtifact.Request, planning, ct);
            await context.ReportAsync(new(new(GenerationPhase.Preparing, "Step 1 of 2 · preparing sequence continuity and reveal boundaries…")), checkpoint: true);
            var text = new StringBuilder(); string? finish = null; DateTimeOffset? saved = null;
            await foreach (var update in GenerateAsync(context, planning, timeout, ct)) {
                timeout.Observe(update);
                if (update.Response?.FinishReason is { } reason) finish = reason.ToString();
                if (update.Progress is { } progress) await context.ReportAsync(new(progress with { Label = "Step 1 of 2 · " + progress.Label }));
                if (update.Response?.Text is not { Length: > 0 } part) continue;
                text.Append(part);
                if (saved is null || clock.GetUtcNow() - saved >= TimeSpan.FromSeconds(2)) {
                    try { await context.SaveResultAsync(new AiTextJobResult(text.ToString()) { ScenePlanning = true }, checkpoint: false); }
                    catch (WorkspaceStoreException) { /* Retain the stream; a checkpoint failure must not resubmit inference. */ }
                    await context.ReportAsync(new(new(GenerationPhase.Generating, "Step 1 of 2 · preparing sequence brief…", Current: text.Length, Unit: "characters")));
                    saved = clock.GetUtcNow();
                }
            }
            result = new(text.ToString(), true, finish);
            await PersistAsync(context, () => context.SaveOperationAsync(ScenePlanOperation, AiOperationArtifact.Output, result, ct), ct);
        }
        var plan = result.FinishReason == ChatFinishReason.Stop.ToString() ? ScriptScenePlanning.Parse(result.Raw) : null;
        if (plan is null) {
            const string error = "Sequence planning did not return a complete usable brief. Inspect the saved response and retry; nothing has been applied.";
            await context.SaveResultAsync(result with { Error = error, ScenePlanning = true });
            throw new AiGenerationException(error);
        }
        plan = ScriptScenePlanning.Ground(plan, request.Payload<ScriptAssistantRequest>().Adaptation!);
        var draft = request with { Messages = ScriptScenePlanning.WithPlan(request.Messages, plan) };
        await context.SaveOperationAsync("scene-draft", AiOperationArtifact.Request, draft, ct);
        await context.SaveResultAsync(new AiTextJobResult("") { ScenePlan = plan });
        await context.ReportAsync(new(new(GenerationPhase.Preparing, "Step 2 of 2 · writing from the focused sequence brief…")), checkpoint: true);
        return (draft, plan);
    }
}
