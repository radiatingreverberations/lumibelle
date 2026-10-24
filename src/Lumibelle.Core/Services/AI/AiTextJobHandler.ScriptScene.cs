using System.Text;
using lumibelle.Models;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.AI;

public sealed partial class AiTextJobHandler
{
    private const string ScenePlanOperation = "scene-plan";
    private async Task<(AiTextJobRequest Request, ScriptScenePlan? Plan)> PrepareSceneAsync(AiJobContext context,
        AiTextJobRequest request, TextInactivityWatchdog timeout, CancellationToken ct)
    {
        var result = await context.ReadOperationAsync<AiTextJobResult>(ScenePlanOperation, AiOperationArtifact.Output, ct);
        if (result is not { Complete: true }) {
            var planning = request with { Messages = request.ScenePlanMessages!, ScenePlanMessages = null, ScenePlanReplyTokens = null,
                ScenePlanResponseSchema = null, ScenePlanResponseGrammar = null,
                ResponseSchema = request.ScenePlanResponseSchema ?? (request.ResponseSchema is null ? null : ScriptScenePlanning.PlanningSchema),
                ResponseGrammar = request.ScenePlanResponseGrammar ?? (request.ResponseGrammar is null ? null : ScriptScenePlanning.PlanningGrammar),
                Model = request.Model with { MaxOutputTokens = ScriptScenePlanning.CapturedReplyLimit(request) } };
            await context.SaveOperationAsync(ScenePlanOperation, AiOperationArtifact.Request, planning, ct);
            var steps = request.Profile == ScriptSequenceDrafting.Profile ? 3 : 2;
            await context.ReportAsync(new(new(GenerationPhase.Preparing, $"Step 1 of {steps} · preparing sequence continuity and reveal boundaries…")), checkpoint: true);
            var text = new StringBuilder(); string? finish = null; DateTimeOffset? saved = null;
            await foreach (var update in GenerateAsync(context, planning, timeout, ct)) {
                timeout.Observe(update);
                if (update.Response?.FinishReason is { } reason) finish = reason.ToString();
                if (update.Progress is { } progress) await context.ReportAsync(new(progress with { Label = $"Step 1 of {steps} · " + progress.Label }));
                if (update.Response?.Text is not { Length: > 0 } part) continue;
                text.Append(part);
                if (saved is null || clock.GetUtcNow() - saved >= TimeSpan.FromSeconds(2)) {
                    try { await context.SaveResultAsync(new AiTextJobResult(text.ToString()) { ScenePlanning = true }, checkpoint: false); }
                    catch (WorkspaceStoreException) { /* Retain the stream; a checkpoint failure must not resubmit inference. */ }
                    await context.ReportAsync(new(new(GenerationPhase.Generating, $"Step 1 of {steps} · preparing sequence brief…", Current: text.Length, Unit: "characters")));
                    saved = clock.GetUtcNow();
                }
            }
            result = new(text.ToString(), true, finish);
            await PersistAsync(context, () => context.SaveOperationAsync(ScenePlanOperation, AiOperationArtifact.Output, result, ct), ct);
        }
        var correction = (await context.ReadAsync<ScriptBriefCorrection[]>(AiJobArtifact.SequenceBriefCorrections, ct))?
            .LastOrDefault(c => c.OriginalFingerprint == ScriptScenePlanApproval.Fingerprint(result.Raw));
        if (correction is not null) result = result with { Raw = correction.Raw, FinishReason = "stop" };
        var inspected = ScriptScenePlanning.Inspect(result.Raw, result.FinishReason);
        var plan = result.FinishReason == ChatFinishReason.Stop.ToString() && inspected.CanUse ? inspected.Plan : null;
        if (plan is null) {
            var error = "Planning needs correction: " + string.Join(" ", inspected.Errors) + " Retry planning; your script is unchanged.";
            await context.SaveResultAsync(result with { Error = error, ScenePlanning = true });
            throw new AiGenerationException(error);
        }
        var approved = (await context.ReadAsync<ScriptBriefApproval>(AiJobArtifact.SequenceBriefApproval, ct))?.ResponseFingerprint == ScriptScenePlanApproval.Fingerprint(result.Raw);
        var needsApproval = inspected.Warnings.Length > 0 || correction is not null;
        if (needsApproval && !approved) {
            await context.SaveResultAsync(result with { Error = "The brief is complete but exceeds the recommended size or concision limits. Review its warnings, then use the brief or retry planning.", ScenePlanning = true });
            return (request, null);
        }
        plan = ScriptScenePlanning.Ground(plan, request.Payload<ScriptAssistantRequest>().Adaptation!);
        var draft = request with { Messages = request.Profile == ScriptSequenceDrafting.Profile
            ? ScriptSequenceDrafting.WithPlan(request.Messages, plan) : ScriptScenePlanning.WithPlan(request.Messages, plan) };
        if (needsApproval || approved) {
            try { await ValidateApprovedBriefCapacityAsync(draft, ct); }
            catch (AiGenerationException e) { await context.SaveResultAsync(result with { Error = e.Message, ScenePlanning = true }); throw; }
        }
        await context.SaveOperationAsync("scene-draft", AiOperationArtifact.Request, draft, ct);
        await context.SaveResultAsync(new AiTextJobResult("") { ScenePlan = plan });
        await context.ReportAsync(new(new(GenerationPhase.Preparing, $"Step 2 of {(request.Profile == ScriptSequenceDrafting.Profile ? 3 : 2)} · writing from the focused sequence brief…")), checkpoint: true);
        return (draft, plan);
    }

    private async Task ValidateApprovedBriefCapacityAsync(AiTextJobRequest draft, CancellationToken ct)
    {
        var check = await providers.CheckAsync(draft.Model.Backend, draft.Settings, cancellationToken: ct);
        if (TextModelPolicy.CatalogModel(draft.Model, check)?.Catalog?.ContextLength is not { } limit) return;
        long ascii = 0, other = 0;
        foreach (var rune in draft.Messages.SelectMany(m => m.Parts).Where(p => p.Text is not null).SelectMany(p => p.Text!.EnumerateRunes())) {
            if (rune.Value <= 127) ascii++; else if (!Rune.IsWhiteSpace(rune)) other++;
        }
        var imageCount = draft.Messages.SelectMany(m => m.Parts).Count(p => p.Image is not null);
        var reply = draft.Model.MaxOutputTokens ?? draft.Settings.MaxOutputTokens;
        var required = (long)Math.Ceiling(ascii / 4d) + other + imageCount * 2048L + reply + 2048;
        if (draft.Profile == ScriptSequenceDrafting.Profile) required += reply + ScriptSequenceDrafting.ReviewReplyTokens;
        if (required > limit) throw new AiGenerationException($"The approved brief and captured writing context need about {required:N0} tokens including reply/review reserves, beyond this model's {limit:N0}-token context window. Adjust the request or choose more context; no screenplay request was sent.");
    }
}
