using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed class FileRefinementBatchStore(ProjectFiles files)
{
    public const int MaximumItems = 200;
    public event Action<Guid>? Changed;
    private async Task<string> PathAsync(Guid project, CancellationToken ct) => Path.Combine(await files.DirectoryAsync(project, ct), "refinement-batches.json");
    public async Task<RefinementBatches> LoadAsync(Guid project, CancellationToken ct = default)
    {
        var path = await PathAsync(project, ct); using var gate = await ProjectFiles.LockAsync(path, ct);
        return await ReadAsync(path, project, ct);
    }
    public async Task<RefinementBatches> UpdateAsync(Guid project, Func<RefinementBatches, Task<RefinementBatches>> update, CancellationToken ct = default)
    {
        var path = await PathAsync(project, ct); RefinementBatches next;
        using (await ProjectFiles.LockAsync(path, ct))
        {
            var current = await ReadAsync(path, project, ct); next = await update(current);
            // Compare content: the worker wakes on Changed, so an unchanged pass must not publish.
            if (next == current || Same(next, current)) return current;
            next = next with { Revision = current.Revision + 1 }; Validate(next, project);
            await AtomicJsonFile.WriteAsync(path, next, ct);
        }
        Changed?.Invoke(project); return next;
    }
    private static bool Same(RefinementBatches a, RefinementBatches b) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(a, AtomicJsonFile.Options).AsSpan().SequenceEqual(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(b, AtomicJsonFile.Options));
    public static RefinementBatches Replace(RefinementBatches doc, RefinementBatch run) =>
        doc.Runs.Any(r => r.Id == run.Id && r == run) ? doc : doc with { Runs = doc.Runs.Select(r => r.Id == run.Id ? run : r).ToArray() };
    public static RefinementBatch Replace(RefinementBatch run, RefinementBatchItem item) =>
        run with { Items = run.Items.Select(i => i.Id == item.Id ? item : i).ToArray() };
    private static async Task<RefinementBatches> ReadAsync(string path, Guid project, CancellationToken ct)
    {
        var doc = await AtomicJsonFile.ReadAsync<RefinementBatches>(path, ct) ?? new() { ProjectId = project };
        Validate(doc, project); return doc;
    }
    internal static void Validate(RefinementBatches doc, Guid project)
    {
        if (doc.SchemaVersion != 1 || doc.ProjectId != project || project == Guid.Empty || doc.Revision < 0 || doc.Runs is null ||
            doc.Runs.Any(r => r is null) || doc.Runs.Select(r => r.Id).Distinct().Count() != doc.Runs.Count ||
            doc.Runs.SelectMany(r => r.Items ?? []).Select(i => i?.Id).Distinct().Count() != doc.Runs.Sum(r => r.Items?.Count ?? 0))
            throw new WorkspaceStoreException("The saved refinement batches are invalid.");
        foreach (var run in doc.Runs)
        {
            if (run.Id == Guid.Empty || run.TabId == Guid.Empty || string.IsNullOrWhiteSpace(run.Name) || run.Name.Length > 200 || !Enum.IsDefined(run.State) ||
                run.Items is not { Count: > 0 and <= MaximumItems } || run.Items.Any(i => i is null) ||
                run.State == RefinementBatchState.Finished && run.Items.Any(i => i.State is RefinementItemState.Preparing or RefinementItemState.Ready))
                throw new WorkspaceStoreException("A saved refinement batch is invalid.");
            foreach (var item in run.Items)
            {
                if (item.Id == Guid.Empty || item.SourceId == Guid.Empty || item.OwnerId == Guid.Empty || !Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.State) ||
                    string.IsNullOrWhiteSpace(item.Name) || item.Options?.Draft?.Shot is null || item.Options.Seed is null ||
                    (item.Kind == RefinementSourceKind.Reel) != (item.Options.Draft.Reel is not null) ||
                    // Failed before capture has no submission; failed after its render (result gone) keeps it.
                    item.State == RefinementItemState.Preparing && item.Submission is not null ||
                    item.State is RefinementItemState.Ready or RefinementItemState.Submitted && item.Submission is null ||
                    item.Submission is { } submission && (submission.Id != item.Id || submission.Target?.ProjectId != project ||
                        submission.Kind != (item.Kind == RefinementSourceKind.Reel ? AiJobKind.ReelVideo : AiJobKind.Video)) ||
                    item.AcceptedUtc is not null && (item.ResultId is null || item.ReviewedUtc is null))
                    throw new WorkspaceStoreException("A saved refinement batch item is invalid.");
            }
        }
    }
}
