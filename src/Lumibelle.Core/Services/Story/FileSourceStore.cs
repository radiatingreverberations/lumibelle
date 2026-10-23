using lumibelle.Models;

namespace lumibelle.Services.Story;

public sealed class FileSourceStore(ProjectFiles files)
{
    public async Task<SourceDocument> LoadAsync(Guid project, CancellationToken ct = default)
    {
        var path = Path.Combine(await files.DirectoryAsync(project, ct), "source.json");
        using var gate = await ProjectFiles.LockAsync(path, ct);
        return await ReadAsync(path, project, ct);
    }
    public async Task<SourceDocument> UpdateAsync(Guid project, Func<SourceDocument, Task<SourceDocument>> update, CancellationToken ct = default)
    {
        var path = Path.Combine(await files.DirectoryAsync(project, ct), "source.json");
        using var gate = await ProjectFiles.LockAsync(path, ct);
        var current = await ReadAsync(path, project, ct);
        var next = await update(current);
        if (next == current) return current;
        next = next with { Revision = current.Revision + 1 };
        Validate(next, project);
        await AtomicJsonFile.WriteAsync(path, next, ct);
        return next;
    }
    public Task<SourceDocument> SaveAsync(Guid project, long sourceRevision, string title, string text, string direction, CancellationToken ct = default) =>
        UpdateAsync(project, doc => {
            if (doc.SourceRevision != sourceRevision) throw new WorkspaceConflictException();
            return Task.FromResult(doc with { Title = title, Text = text, Direction = direction, SourceRevision = doc.SourceRevision + 1 });
        }, ct);
    public Task<SourceDocument> ReviewAsync(Guid project, long revision, Guid runId, SourceOutline outline, bool accept, CancellationToken ct = default) =>
        UpdateAsync(project, doc => {
            var run = doc.Runs.SingleOrDefault(r => r.Id == runId) ?? throw new WorkspaceStoreException("Analysis not found.");
            if (run.ReviewRevision != revision) throw new WorkspaceConflictException();
            if (run.State is not (SourceRunState.Review or SourceRunState.Accepted)) throw new WorkspaceStoreException("Wait for the outline before reviewing it.");
            SourceAnalysis.ValidateOutline(outline, run.Chunks.Select(c => c.Number).ToHashSet());
            if (accept && SourceAnalysis.Stale(doc, run)) throw new WorkspaceStoreException("The source or adaptation direction changed. Analyze the saved source again before accepting an outline.");
            var copy = System.Text.Json.JsonSerializer.Deserialize<SourceOutline>(System.Text.Json.JsonSerializer.Serialize(outline, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
            return Task.FromResult(Replace(doc, run with { Reviewed = copy, ReviewRevision = run.ReviewRevision + 1, State = accept ? SourceRunState.Accepted : SourceRunState.Review }));
        }, ct);
    public static SourceDocument Replace(SourceDocument doc, SourceAnalysisRun run) =>
        doc.Runs.Any(r => r.Id == run.Id && r == run) ? doc :
            doc with { Runs = doc.Runs.Select(r => r.Id == run.Id ? run : r).ToList() };
    private static async Task<SourceDocument> ReadAsync(string path, Guid project, CancellationToken ct)
    {
        var doc = await AtomicJsonFile.ReadAsync<SourceDocument>(path, ct) ?? new() { ProjectId = project };
        Validate(doc, project);
        return doc;
    }
    public static void Validate(SourceDocument doc, Guid project)
    {
        if (doc.ProjectId != project || doc.SchemaVersion != 1 || doc.Revision < 0 || doc.SourceRevision < 0 ||
            doc.Title is null || doc.Title.Length > 200 || doc.Text is null || doc.Text.Length > SourceAnalysis.MaximumText ||
            doc.Direction is null || doc.Direction.Length > 4000 || doc.Runs is null || doc.Runs.Any(r => r is null) ||
            doc.Runs.Select(r => r.Id).Distinct().Count() != doc.Runs.Count ||
            doc.Runs.Count(r => r.State is SourceRunState.Running or SourceRunState.Paused) > 1)
            throw new WorkspaceStoreException("Source needs a title of up to 200 characters, text of up to 500,000 characters, and direction of up to 4,000 characters.");
        foreach (var run in doc.Runs) {
            if (run.AnalysisFromRunId is { } parentId) {
                var parentIndex = doc.Runs.FindIndex(r => r.Id == parentId);
                if (parentIndex < 0 || parentIndex >= doc.Runs.IndexOf(run) || doc.Runs[parentIndex].Text != run.Text)
                    throw new WorkspaceStoreException("The reused source analysis is unavailable or belongs to different text.");
            }
            if (run.Id == Guid.Empty || run.TabId == Guid.Empty || !Enum.IsDefined(run.State) || run.Text is null ||
                run.Title is null || run.Direction is null || run.Model is null || run.PromptBudget is < 3000 or > SourceAnalysis.MaximumPromptBudget ||
                run.Fingerprint != SourceAnalysis.Fingerprint(run.Title, run.Text, run.Direction) ||
                run.Chunks is not { Count: > 0 } || run.Notes is null || run.Combined is null || run.Jobs is null ||
                run.Notes.Count > run.Chunks.Count || run.ReviewRevision < 0 ||
                run.Chunks.Where((c, i) => c is null || c.Number != i + 1 || c.Heading is null || c.Start < 0 || c.Length <= 0 ||
                    (long)c.Start + c.Length > run.Text.Length || c.Start != (i == 0 ? 0 : run.Chunks[i - 1].Start + run.Chunks[i - 1].Length)).Any() ||
                run.Chunks[^1].Start + run.Chunks[^1].Length != run.Text.Length ||
                run.Notes.Concat(run.Level ?? []).Concat(run.Combined).Any(n => n is null || string.IsNullOrWhiteSpace(n.Text) || n.Text.Length > SourceAnalysis.MaximumNote ||
                    n.Sources is not { Length: > 0 } || n.Sources.Any(s => s < 1 || s > run.Chunks.Count)) ||
                run.CombineIndex < 0 || run.CombineIndex > (run.Level?.Count ?? 0))
                throw new WorkspaceStoreException("The saved source analysis is invalid. Its original material has not been changed.");
            if (run.Proposal is not null) SourceAnalysis.ValidateOutline(run.Proposal, run.Chunks.Select(c => c.Number).ToHashSet());
            if (run.Reviewed is not null) SourceAnalysis.ValidateOutline(run.Reviewed, run.Chunks.Select(c => c.Number).ToHashSet());
            if (run.Pending is { } pending && (pending.Kind != AiJobKind.SourceAnalysis || pending.Target != new AiJobTarget(project) ||
                lumibelle.Services.AI.AiTextJobHandler.Read(new() { Id = pending.Id, Kind = pending.Kind, Backend = pending.Backend,
                    Target = pending.Target, ProjectName = pending.ProjectName, TargetName = pending.TargetName, OriginTabId = pending.OriginTabId,
                    RequestFingerprint = "" }, pending.Snapshot).Payload<SourceAnalysisRequest>().RunId != run.Id))
                throw new WorkspaceStoreException("The pending source analysis belongs to another source.");
        }
    }
}
