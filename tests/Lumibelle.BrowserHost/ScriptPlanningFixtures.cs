using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

public static class ScriptPlanningFixtures
{
    public static void MapScriptPlanningFixtures(this WebApplication app)
    {
        app.MapPost("/fixtures/{id:guid}/planning-review", async (Guid id, FileSourceStore sources, IScriptStore scripts,
            AiTextJobCapture capture, IAiJobStore store, AiJobCoordinator queue, ScriptReviewPreviews previews, IAiSettingsStore settings, string stage = "planning") =>
        {
            if (stage is not ("planning" or "writing" or "complete" or "failed" or "checking" or "repairing" or "review-interrupted" or "review-invalid" or "brief-warning" or "brief-json" or "brief-count" or "stream-planning" or "stream-writing" or "stream-review")) return Results.BadRequest();
            var streaming = stage.StartsWith("stream-", StringComparison.Ordinal);
            var approval = stage is "brief-warning" or "brief-json" or "brief-count";
            var reviewed = approval || stage is "checking" or "repairing" or "review-interrupted" or "review-invalid" or "stream-review";
            var interrupted = stage == "review-interrupted";
            var invalidReview = stage == "review-invalid";
            const string title = "The river key";
            const string text = "Chapter 1\nEira inherits a brass key and follows the river to a sheltered garden.";
            var source = await sources.SaveAsync(id, 0, title, text, "Follow Eira's point of view.");
            var model = new TextModelReference(reviewed ? AiBackend.Vllm : AiBackend.OpenRouter, "mock/script", "Sample text model") { VllmUrl = reviewed ? "http://llm-server:8000/v1" : null };
            if (reviewed) await settings.SaveAsync((await settings.LoadAsync()) with { VllmUrl = model.VllmUrl! });
            var outline = new SourceOutline { Title = title, Premise = "A key leads Eira to a hidden garden.", Approach = "Follow the journey.",
                Acts = [new() { Title = "The discovery", Purpose = "Find the gate.", Scenes = [new() { Title = "The river", Summary = "Eira follows the river and finds the gate.", Sources = [1] }] }] };
            var plan = new SourceAnalysisRun { Title = title, Text = text, Direction = source.Direction, Model = model, TabId = Guid.NewGuid(),
                Fingerprint = SourceAnalysis.Fingerprint(title, text, source.Direction), PromptBudget = 3000,
                Chunks = SourceAnalysis.Split(text, 1000), State = SourceRunState.Accepted, Proposal = outline };
            await sources.UpdateAsync(id, d => Task.FromResult(d with { Runs = [plan] }));
            var script = await scripts.LoadAsync(id);
            // Match the editor's initial empty paragraph, including its stable identity.
            script = await scripts.SaveAsync(script with { Blocks = [ScriptBlock.Create(ScriptBlockKind.Action, "") with { Spans = [] }] }, script.Revision);
            var run = new AssistantRun { Operation = WritingOperation.Continue, Backend = model.Backend, Model = model.Model,
                Target = ScriptStructure.Capture(script, ScriptScope.Document, null), SourceRevision = script.Revision,
                SourceFingerprint = ScriptStructure.ContextFingerprint(script), Instructions = "Keep the journey quiet and observant." };
            var submission = await capture.ScriptAsync(run.Id, Guid.NewGuid(), new(run, script, [], model) {
                OutlineSelection = new(plan.Id, plan.ReviewRevision, 0, 0, true) }, false);
            var brief = new ScriptScenePlan([
                "Eira unwraps the brass key left by her grandmother and notices a river drawn on its paper wrapping.",
                "She follows the river out of town, comparing the bends in the water with the faded marks on the paper.",
                "At a washed-out footbridge, she decides to continue along the bank rather than turn back.",
                "A ferryman offers a crossing. Eira chooses to trust him after recognizing the garden emblem on his boat.",
                "On the far bank, she finds a gate hidden behind willow branches and tests the key in its lock.",
                "The lock turns. Eira pauses before opening the gate, hearing birdsong on the other side."
            ], ["The key stays with Eira throughout the journey.", "The garden has not been shown yet.", "The ferryman does not explain the key's history."],
                ["The person waiting in the garden."], "Eira opens the gate and steps toward the birdsong.", "", ["A brass key wrapped in a hand-drawn river map."], [new(1, new string('x', 1210))]);
            const string error = "Sequence planning did not return a complete usable brief. Inspect the saved response and retry; nothing has been applied.";
            if (stage == "brief-warning") brief = brief with { Beats = [string.Join(" ", Enumerable.Repeat("Eira studies the river map and carries the brass key toward the garden gate.", 180))] };
            if (stage == "brief-count") brief = brief with { Continuity = Enumerable.Range(1, 13).Select(i => $"The garden has marker {i}.").ToArray() };
            if (approval) brief = brief with { Evidence = [] };
            await queue.SetPausedAsync(model.Backend, true);
            try {
                var held = streaming || stage == "writing" || reviewed && !interrupted && !approval && !invalidReview;
                if (held || interrupted || approval) previews.Add(submission.Id, brief, reviewed ? new(stage == "repairing" ? ScriptSequenceReviewState.Repairing : ScriptSequenceReviewState.Checking,
                    stage == "repairing" ? 1 : 0, [], []) : null, streaming ? stage : null);
                await store.EnqueueAsync(submission);
                if (!held) {
                    var stopped = stage is "planning" or "failed" || interrupted || approval || invalidReview;
                    var result = stage == "planning" || approval
                        ? new AiTextJobResult(JsonSerializer.Serialize(brief, AtomicJsonFile.Options), true, "stop", Error: error) { ScenePlanning = true }
                        : ScriptReviewPreviews.Result(brief, stage == "failed" ? "The screenplay response was interrupted." : null);
                    if (stage == "brief-json") result = result with { Raw = result.Raw.TrimEnd()[..^1] };
                    if (approval) {
                        var lease = Guid.NewGuid();
                        await store.UpdateAsync(submission.Id, j => j with { State = AiJobState.Running, LeaseId = lease, StartedUtc = DateTimeOffset.UtcNow });
                        await store.WriteOperationAsync(submission.Id, lease, "scene-plan", AiOperationArtifact.Output, result);
                        previews.Complete(submission.Id);
                    }
                    if (interrupted) result = ScriptReviewPreviews.ReviewedResult(brief) with { Value = null, Error = "The automatic review connection was interrupted. The saved screenplay is preserved.",
                        SequenceReview = new(ScriptSequenceReviewState.NeedsAttention, 0, [], []) };
                    if (invalidReview) {
                        var draft = ScriptReviewPreviews.ReviewedResult(brief);
                        var checks = draft.SequenceReview!.Beats.ToArray();
                        checks[0] = checks[0] with { Evidence = [.. Enumerable.Repeat(checks[0].Evidence[0], 18), new(1, 999, "A misplaced quotation.")] };
                        var rawReview = JsonSerializer.Serialize(new { beats = checks, findings = draft.SequenceReview.Findings }, AtomicJsonFile.Options);
                        var lease = Guid.NewGuid();
                        await store.UpdateAsync(submission.Id, j => j with { State = AiJobState.Running, LeaseId = lease, StartedUtc = DateTimeOffset.UtcNow });
                        await store.WriteOperationAsync(submission.Id, lease, "text", AiOperationArtifact.Output, draft);
                        await store.WriteOperationAsync(submission.Id, lease, "sequence-review-0-0", AiOperationArtifact.Output, new AiTextJobResult(rawReview, true, "stop"));
                        result = draft with { Value = null, Error = "Beat check 1 must use ID B1, a reason, and exact quotations with Scene and Block numbers copied from the numbered candidate.",
                            SequenceReview = new(ScriptSequenceReviewState.NeedsAttention, 0, [], [], rawReview) };
                    }
                    await store.WriteArtifactAsync(submission.Id, AiJobArtifact.Result, result);
                    await store.UpdateAsync(submission.Id, j => j with { State = stopped ? AiJobState.NeedsAttention : AiJobState.Completed, Recovery = approval ? AiJobRecovery.ApproveBrief : interrupted ? AiJobRecovery.ContinueRequest : stopped ? AiJobRecovery.GenerateAgain : AiJobRecovery.None, Error = result.Error, FinishedUtc = DateTimeOffset.UtcNow });
                }
            }
            finally { await queue.SetPausedAsync(model.Backend, false); }
            return Results.Ok(new { submission.Id });
        });
        app.MapPost("/fixtures/{id:guid}/complete-writing-preview", (Guid id, ScriptReviewPreviews previews) => { previews.Complete(id); return Results.Ok(); });
        app.MapPost("/fixtures/{id:guid}/advance-streaming-preview", (Guid id, ScriptReviewPreviews previews) => { previews.Advance(id); return Results.Ok(); });
    }
}

public sealed class ScriptReviewPreviews
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (ScriptScenePlan Brief, TaskCompletionSource Complete, ScriptSequenceReview? Review, string? StreamingStage, TaskCompletionSource Advance)> _requests = new();
    public void Add(Guid id, ScriptScenePlan brief, ScriptSequenceReview? review = null, string? streamingStage = null) => _requests[id] = (brief, new(TaskCreationOptions.RunContinuationsAsynchronously), review, streamingStage, new(TaskCreationOptions.RunContinuationsAsynchronously));
    public bool Contains(Guid id) => _requests.ContainsKey(id);
    public (ScriptScenePlan Brief, TaskCompletionSource Complete, ScriptSequenceReview? Review, string? StreamingStage, TaskCompletionSource Advance) Get(Guid id) => _requests[id];
    public void Complete(Guid id) => _requests[id].Complete.TrySetResult();
    public void Advance(Guid id) => _requests[id].Advance.TrySetResult();
    public static AiTextJobResult StreamingResult(string stage, ScriptScenePlan brief, bool advanced)
    {
        var suffix = advanced ? "ver to the garden gate." : "";
        if (stage == "stream-planning") return new("""{"beats":["Eira finds the brass key.","She follows the ri""" + suffix) { ScenePlanning = true };
        if (stage == "stream-review") return ReviewedResult(brief) with { Complete = false, Value = null,
            SequenceReview = new(ScriptSequenceReviewState.Checking, 0, [], [],
                """{"beats":[{"id":"B1","covered":true,"reason":"The key is shown.","evidence":[]},{"id":"B2","covered":true,"reason":"She follows the ri""" + suffix) };
        return new("""{"scenes":[{"heading":"EXT. RIVER BANK - DAY","blocks":[{"kind":"Action","spans":[{"text":"Eira finds the brass key."}]}]},{"heading":"EXT. GARDEN GATE - DAY","blocks":[{"kind":"Character","spans":[{"text":"EIRA"}]},{"kind":"Dialogue","spans":[{"text":"She follows the ri""" + suffix) { ScenePlan = brief };
    }
    public static AiTextJobResult Result(ScriptScenePlan brief, string? error = null)
    {
        List<ScriptBlock> blocks = [ScriptBlock.Create(ScriptBlockKind.Scene, "EXT. RIVER BANK - DAY"), ScriptBlock.Create(ScriptBlockKind.Action, "Eira follows the paper map along the river. At a willow-covered gate, she tries the brass key. The lock turns.")];
        return new(JsonSerializer.Serialize(blocks, AtomicJsonFile.Options), true, "stop",
            JsonSerializer.SerializeToElement(new AssistantUpdate(Complete: true, Blocks: error is null ? blocks : null, ValidationError: error), AtomicJsonFile.Options), error) { ScenePlan = brief };
    }
    public static AiTextJobResult ReviewedResult(ScriptScenePlan brief, int repairs = 0)
    {
        var result = Result(brief);
        var report = new ScriptSequenceReview(ScriptSequenceReviewState.Passed, repairs,
            ScriptSequenceDrafting.RequiredBeats(brief).Select(b => new ScriptBeatReview(b.Id, true, "Supported by the saved draft.", [new(1, 1, "Eira follows the paper map along the river.")])).ToArray(),
            [new("screenplay", "note", "The route is conveyed through *action* and a short pause at the gate.", [new(1, 1, "Eira follows the paper map along the river.")], []),
                new("continuity", "note", "The key remains with Eira from the riverbank to the gate.", [new(1, 1, "she tries the brass key.")], [])]);
        return result with { SequenceReview = report, Raw = JsonSerializer.Serialize(new { scenes = new[] { new { heading = "EXT. RIVER BANK - DAY", beatIds = report.Beats.Select(b => b.Id),
            blocks = result.Read<AssistantUpdate>()!.Blocks!.Where(b => b.Kind != ScriptBlockKind.Scene).Select(b => new { b.Kind, b.Spans }) } } }, AtomicJsonFile.Options) };
    }
}

// Hold only explicitly registered synthetic review fixtures. All ordinary browser
// scenarios still use the real handler with mocked providers.
public sealed class ScriptReviewPreviewHandler(AiTextJobHandler inner, ScriptReviewPreviews previews) : IAiJobHandler
{
    public IReadOnlyCollection<AiJobKind> Kinds => inner.Kinds;
    public async Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        if (!previews.Contains(context.Job.Id)) return await inner.ExecuteAsync(context, snapshot, ct);
        var preview = previews.Get(context.Job.Id);
        if (await context.ReadOperationAsync<AiTextJobResult>("scene-plan", AiOperationArtifact.Output, ct) is { } original) {
            var correction = (await context.ReadAsync<ScriptBriefCorrection[]>(AiJobArtifact.SequenceBriefCorrections, ct))?.LastOrDefault();
            var raw = correction?.Raw ?? original.Raw;
            var approved = await context.ReadAsync<ScriptBriefApproval>(AiJobArtifact.SequenceBriefApproval, ct);
            if (approved?.ResponseFingerprint != ScriptScenePlanApproval.Fingerprint(raw)) throw new AiGenerationException("The synthetic brief was not approved.");
            preview.Brief = ScriptScenePlanning.Inspect(raw).Plan!;
        }
        var result = ScriptReviewPreviews.Result(preview.Brief);
        if (preview.Review is not null) result = ScriptReviewPreviews.ReviewedResult(preview.Brief, preview.Review.Repairs);
        await context.SaveResultAsync(preview.StreamingStage is { } streamStage ? ScriptReviewPreviews.StreamingResult(streamStage, preview.Brief, false) :
            new AiTextJobResult(preview.Review is null ? result.Raw[..100] : result.Raw) { ScenePlan = preview.Brief, SequenceReview = preview.Review });
        await context.ReportAsync(new(new(GenerationPhase.Generating, preview.Review is null ? "Step 2 of 2 · receiving screenplay from Sample text model…" :
            preview.Review.State == ScriptSequenceReviewState.Repairing ? "Step 3 of 3 · repairing cited findings…" : "Step 3 of 3 · checking the screenplay…", Current: 100, Unit: "characters")), checkpoint: true);
        if (preview.StreamingStage is { } streamingStage) {
            await Task.WhenAny(preview.Advance.Task, preview.Complete.Task).WaitAsync(ct);
            if (preview.Advance.Task.IsCompleted && !preview.Complete.Task.IsCompleted) {
                await context.SaveResultAsync(ScriptReviewPreviews.StreamingResult(streamingStage, preview.Brief, true));
                await context.ReportAsync(new(new(GenerationPhase.Generating, "Receiving response…", Current: 200, Unit: "characters")), checkpoint: true);
            }
        }
        await preview.Complete.Task.WaitAsync(ct);
        await context.SaveResultAsync(result);
        return AiJobOutcome.Complete();
    }
    public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => inner.RecoverAsync(context, snapshot, ct);
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => previews.Contains(context.Job.Id) ? Task.FromResult(true) : inner.CancelRemoteAsync(context, snapshot, ct);
}
