using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Story;

// Each model call is an ordinary durable queue request. The source record checkpoints the
// sequence independently of browser visits; completed steps are never generated again on resume.
public sealed class SourceWorkflow(FileSourceStore store, AiTextJobCapture capture, IAiJobStore jobs, AiJobCoordinator queue, ILogger<SourceWorkflow>? logger = null)
{
    public async Task<SourceDocument> StartAsync(Guid project, long sourceRevision, TextModelReference model, Guid tab, CancellationToken ct = default)
    {
        model = await capture.SourceModelAsync(model, ct);
        var budget = await capture.SourceBudgetAsync(model, ct);
        return await store.UpdateAsync(project, async doc => {
            if (doc.SourceRevision != sourceRevision) throw new WorkspaceConflictException();
            await EnsureCanStartAsync(doc, project, ct);
            var overhead = SourceAnalysis.RequestTokens(new(project, Guid.NewGuid(), SourceAnalysisStage.Read, doc.Title, doc.Direction, [], new string('x', SourceAnalysis.TargetNote)));
            var chunks = SourceAnalysis.Split(doc.Text, Math.Min(5000, budget - overhead - 256));
            var run = new SourceAnalysisRun {
                Title = doc.Title, Text = doc.Text, Direction = doc.Direction, Model = model, TabId = tab, PromptBudget = budget,
                Fingerprint = SourceAnalysis.Fingerprint(doc.Title, doc.Text, doc.Direction), Chunks = chunks
            };
            return doc with { Runs = [.. doc.Runs, run] };
        }, ct);
    }
    public async Task<SourceDocument> ReplanAsync(Guid project, long sourceRevision, Guid fromRunId,
        TextModelReference model, Guid tab, CancellationToken ct = default)
    {
        model = await capture.SourceModelAsync(model, ct);
        var budget = await capture.SourceBudgetAsync(model, ct);
        return await store.UpdateAsync(project, async doc => {
            if (doc.SourceRevision != sourceRevision) throw new WorkspaceConflictException();
            await EnsureCanStartAsync(doc, project, ct);
            var previous = doc.Runs.SingleOrDefault(r => r.Id == fromRunId);
            if (previous is null || previous.State is not (SourceRunState.Review or SourceRunState.Accepted) ||
                previous.Notes.Count != previous.Chunks.Count || previous.Proposal is null)
                throw new WorkspaceStoreException("Choose a completed analysis to reuse its story notes.");
            if (previous.Text != doc.Text)
                throw new WorkspaceStoreException("The original text changed. Analyze the source again before making another outline.");
            var run = new SourceAnalysisRun {
                AnalysisFromRunId = previous.Id, Title = doc.Title, Text = doc.Text, Direction = doc.Direction,
                Model = model, TabId = tab, PromptBudget = budget,
                Fingerprint = SourceAnalysis.Fingerprint(doc.Title, doc.Text, doc.Direction),
                Chunks = previous.Chunks, Notes = previous.Notes, Level = previous.Level ?? previous.Notes
            };
            // Prefer the complete evidence when it fits, including when switching to
            // a larger model after an earlier run had to condense its notes.
            var fullNotes = new SourceAnalysisRequest(project, run.Id, SourceAnalysisStage.Outline,
                run.Title, run.Direction, run.Notes);
            if (SourceAnalysis.RequestTokens(fullNotes) <= budget) run = run with { Level = run.Notes };
            // A smaller model may require another combining pass. Check before publishing.
            _ = Next(project, run);
            return doc with { Runs = [.. doc.Runs, run] };
        }, ct);
    }
    private async Task EnsureCanStartAsync(SourceDocument doc, Guid project, CancellationToken ct)
    {
        if (doc.Runs.Any(r => r.State is SourceRunState.Running or SourceRunState.Paused) ||
            (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Kind == AiJobKind.SourceAnalysis && j.Target.ProjectId == project && j.LocksTarget))
            throw new WorkspaceStoreException("Pause and discard the unfinished analysis, or let it finish, before starting another.");
    }
    public Task<SourceDocument> PauseAsync(Guid project, Guid runId, CancellationToken ct = default) => Change(project, runId,
        r => r.State == SourceRunState.Running ? r with { State = SourceRunState.Paused } : r, ct);
    public Task<SourceDocument> ResumeAsync(Guid project, Guid runId, CancellationToken ct = default) =>
        ResumeCoreAsync(project, runId, null, ct);
    public Task<SourceDocument> ResumeAsync(Guid project, Guid runId, TextModelReference model, CancellationToken ct = default) =>
        ResumeCoreAsync(project, runId, model, ct);
    private Task<SourceDocument> ResumeCoreAsync(Guid project, Guid runId, TextModelReference? model, CancellationToken ct) =>
        store.UpdateAsync(project, async doc => {
            var run = doc.Runs.Single(r => r.Id == runId);
            if (run.State != SourceRunState.Paused) return doc;
            if (SourceAnalysis.Stale(doc, run)) throw new WorkspaceStoreException("The saved source changed. Discard this analysis and analyze the current source.");
            model = model is null ? run.Model : await capture.SourceModelAsync(model, ct);
            var changingModel = model != run.Model;
            if (run.Pending is { } pending) {
                var job = (await jobs.ReadAsync(ct)).Jobs.SingleOrDefault(j => j.Id == pending.Id);
                if (job?.RemoteUnconfirmed == true) throw new WorkspaceStoreException("Resolve the active provider request in AI activity before resuming.");
                if (job?.State == AiJobState.Completed) {
                    run = await ConsumeCompletedAsync(run, pending, ct);
                } else if (changingModel) {
                    if (job?.State is AiJobState.Waiting or AiJobState.Running || job?.ComfyControl is not null)
                        throw new WorkspaceStoreException("Wait for the current request to finish before changing the analysis model.");
                    // Replace an unsubmitted or failed request. Its original snapshot
                    // remains in the request history.
                    run = run with { Pending = null };
                } else if (job?.State is AiJobState.NeedsAttention or AiJobState.Cancelled) {
                    if (job.CanRetryCaptured) {
                        await queue.ResumeAsync(job.Id, ct);
                    } else run = run with { Pending = null };
                }
            }
            if (run.State == SourceRunState.Review) return FileSourceStore.Replace(doc, run);
            // An explicit resume rechecks advertised capacity even for the same model:
            // server context limits can change while an analysis is paused.
            var budget = await capture.SourceBudgetAsync(model, ct);
            if (changingModel || budget != run.PromptBudget) {
                run = run with { Model = model, PromptBudget = budget };
                // Keep section numbers and source citations stable. A smaller model must fit
                // the remaining sections; never silently truncate or renumber the source.
                foreach (var chunk in run.Chunks.Skip(run.Notes.Count)) {
                    var request = new SourceAnalysisRequest(project, run.Id, SourceAnalysisStage.Read, run.Title, run.Direction,
                        [new(run.Text.Substring(chunk.Start, chunk.Length), [chunk.Number])]);
                    if (SourceAnalysis.RequestTokens(request) > run.PromptBudget)
                        throw new WorkspaceStoreException("This model cannot fit the remaining source sections. Choose a model with a larger context window. Completed notes are preserved.");
                }
                _ = Next(project, run);
            }
            return FileSourceStore.Replace(doc, run with { State = SourceRunState.Running, Error = null });
        }, ct);
    public async Task<SourceDocument> DiscardAsync(Guid project, Guid runId, CancellationToken ct = default)
    {
        // Stop the chain durably before trying to stop the in-flight provider request.
        var doc = await Change(project, runId, r => r with { State = SourceRunState.Discarded }, ct);
        if (doc.Runs.Single(r => r.Id == runId).Pending is { } pending &&
            (await jobs.ReadAsync(ct)).Jobs.FirstOrDefault(j => j.Id == pending.Id) is { LocksTarget: true })
            await queue.CancelAsync(pending.Id, ct);
        return doc;
    }
    private Task<SourceDocument> Change(Guid project, Guid id, Func<SourceAnalysisRun, SourceAnalysisRun> update, CancellationToken ct) =>
        store.UpdateAsync(project, doc => Task.FromResult(FileSourceStore.Replace(doc, update(doc.Runs.Single(r => r.Id == id)))), ct);

    public Task<SourceDocument> AdvanceAsync(Guid project, CancellationToken ct = default) =>
        store.UpdateAsync(project, async doc => {
            var run = doc.Runs.LastOrDefault(r => r.State is SourceRunState.Running or SourceRunState.Paused);
            if (run is null) return doc;
            try {
                if (run.Pending is { } pending) {
                    var job = (await jobs.ReadAsync(ct)).Jobs.FirstOrDefault(j => j.Id == pending.Id);
                    if (job is null) {
                        if (SourceAnalysis.Stale(doc, run))
                            return FileSourceStore.Replace(doc, run with { State = SourceRunState.Paused, Error = "The source changed before this request was queued. Discard this analysis and analyze the saved source." });
                        if (run.State == SourceRunState.Running) await queue.EnqueueAsync(pending, ct);
                        return doc;
                    }
                    if (job.State is AiJobState.Waiting or AiJobState.Running) return doc;
                    if (job.State != AiJobState.Completed) {
                        if (run.State == SourceRunState.Paused && run.Error == job.Error) return doc;
                        return FileSourceStore.Replace(doc, run with { State = SourceRunState.Paused, Error = job.Error ?? "The request stopped. Resume to retry this step." });
                    }
                    run = await ConsumeCompletedAsync(run, pending, ct);
                }
                if (run.State != SourceRunState.Running) return FileSourceStore.Replace(doc, run);
                if (SourceAnalysis.Stale(doc, run)) return FileSourceStore.Replace(doc, run with { State = SourceRunState.Paused, Error = "The source or adaptation direction changed. Completed analysis is preserved; start a new analysis for the saved source." });
                var next = Next(project, run);
                run = next.Run;
                var submission = await capture.SourceAsync(Guid.NewGuid(), run.TabId, next.Request, run.Model, run.PromptBudget, ct);
                // Enqueue on the next tick, only after publishing this exact submission.
                return FileSourceStore.Replace(doc, run with { Pending = submission, Jobs = [.. run.Jobs, submission.Id] });
            } catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException or ProjectStoreException) {
                return FileSourceStore.Replace(doc, run with { State = SourceRunState.Paused, Error = e.Message });
            }
        }, ct);

    private async Task<SourceAnalysisRun> ConsumeCompletedAsync(SourceAnalysisRun run, AiJobSubmission pending, CancellationToken ct)
    {
        var result = await jobs.ReadArtifactAsync<AiTextJobResult>(pending.Id, AiJobArtifact.Result, ct);
        var parsed = result is { Complete: true, Error: null } ? result.Read<SourceAnalysisResult>() : null;
        if (parsed is null) throw new WorkspaceStoreException("The saved analysis result is unavailable. Inspect this request in AI activity.");
        var request = pending.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<SourceAnalysisRequest>();
        if (parsed.Summary is { Length: > SourceAnalysis.TargetNote } longSummary)
            logger?.LogWarning("Source analysis {Job} returned {Length} characters against a target of {Target}; preserving the complete note.",
                pending.Id, longSummary.Length, SourceAnalysis.TargetNote);
        return Consume(run, request, parsed) with { Pending = null, Error = null };
    }

    public static SourceAnalysisRun Consume(SourceAnalysisRun run, SourceAnalysisRequest request, SourceAnalysisResult result)
    {
        var note = new SourceNote(result.Summary, request.Inputs.SelectMany(n => n.Sources).Distinct().Order().ToArray());
        return request.Stage switch {
            SourceAnalysisStage.Read => run with { Notes = [.. run.Notes, note] },
            SourceAnalysisStage.Combine => run with { Combined = [.. run.Combined, note], CombineIndex = run.CombineIndex + request.Inputs.Count },
            _ => run with { Proposal = result.Outline, State = SourceRunState.Review }
        };
    }
    public static (SourceAnalysisRun Run, SourceAnalysisRequest Request) Next(Guid project, SourceAnalysisRun run)
    {
        SourceAnalysisRequest Request(SourceAnalysisStage stage, List<SourceNote> inputs, string previous = "") =>
            new(project, run.Id, stage, run.Title, run.Direction, inputs, previous);
        if (run.Notes.Count < run.Chunks.Count) {
            var chunk = run.Chunks[run.Notes.Count];
            var read = Request(SourceAnalysisStage.Read, [new(run.Text.Substring(chunk.Start, chunk.Length), [chunk.Number])], run.Notes.LastOrDefault()?.Text ?? "");
            return (run, FitPreviousContext(read, run.PromptBudget));
        }
        run = run with { Level = run.Level ?? run.Notes };
        if (run.CombineIndex == run.Level.Count) run = run with { Level = run.Combined, Combined = [], CombineIndex = 0 };
        var outline = Request(SourceAnalysisStage.Outline, run.Level!);
        if (run.CombineIndex == 0 && SourceAnalysis.RequestTokens(outline) <= run.PromptBudget)
            return (run, outline);
        var inputs = new List<SourceNote>();
        foreach (var note in run.Level!.Skip(run.CombineIndex)) {
            if (inputs.Count >= 4 || SourceAnalysis.RequestTokens(Request(SourceAnalysisStage.Combine, [.. inputs, note])) > run.PromptBudget) break;
            inputs.Add(note);
        }
        if (inputs.Count == 0 || inputs.Count == 1 && run.CombineIndex == 0)
            throw new WorkspaceStoreException("The source notes cannot be combined within this model's context. Choose a larger context or shorten the adaptation direction.");
        return (run, Request(SourceAnalysisStage.Combine, inputs));
    }
    private static SourceAnalysisRequest FitPreviousContext(SourceAnalysisRequest request, int budget)
    {
        if (SourceAnalysis.RequestTokens(request) <= budget) return request;
        // Orientation is optional. Preserve the entire source section and saved note;
        // only shorten the previous note's copy sent alongside the next source section.
        const string label = "Excerpt from previous section notes (full notes retained for planning):\n";
        var previous = request.PreviousContext;
        SourceAnalysisRequest Excerpt(int length) {
            var start = previous.Length - length;
            if (start > 0 && start < previous.Length && char.IsLowSurrogate(previous[start]) && char.IsHighSurrogate(previous[start - 1])) start++;
            return request with { PreviousContext = length == 0 || start == previous.Length ? "" : label + previous[start..] };
        }
        var low = 0; var high = previous.Length;
        while (low < high) {
            var mid = low + (high - low + 1) / 2;
            if (SourceAnalysis.RequestTokens(Excerpt(mid)) <= budget) low = mid; else high = mid - 1;
        }
        // SourceAsync still checks the full serialized request if the source alone is too large.
        return Excerpt(low);
    }
}

public sealed class SourceWorkflowWorker(IProjectStore projects, SourceWorkflow workflow, ILogger<SourceWorkflowWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken)) {
            try {
                foreach (var project in (await projects.ListAsync(stoppingToken)).Projects) {
                    try { await workflow.AdvanceAsync(project.Id, stoppingToken); }
                    catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException) { logger.LogWarning(e, "Source analysis checkpoint unavailable for {Project}", project.Id); }
                }
            } catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or IOException) { logger.LogWarning(e, "Source analysis discovery unavailable"); }
        }
    }
}
