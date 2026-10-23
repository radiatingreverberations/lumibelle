using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Story;

public sealed class FileScriptDraftBatchStore(ProjectFiles files)
{
    public event Action<Guid>? Changed;
    public async Task<ScriptDraftBatches> LoadAsync(Guid project, CancellationToken ct = default)
    {
        var path = Path.Combine(await files.DirectoryAsync(project, ct), "script-draft-batches.json");
        using var gate = await ProjectFiles.LockAsync(path, ct);
        return await ReadAsync(path, project, ct);
    }
    public async Task<ScriptDraftBatches> UpdateAsync(Guid project, Func<ScriptDraftBatches, Task<ScriptDraftBatches>> update, CancellationToken ct = default)
    {
        var path = Path.Combine(await files.DirectoryAsync(project, ct), "script-draft-batches.json");
        ScriptDraftBatches next;
        using (await ProjectFiles.LockAsync(path, ct)) {
            var current = await ReadAsync(path, project, ct);
            next = await update(current);
            if (next == current) return current;
            next = next with { Revision = current.Revision + 1 };
            Validate(next, project);
            await AtomicJsonFile.WriteAsync(path, next, ct);
        }
        Changed?.Invoke(project);
        return next;
    }
    public static ScriptDraftBatches Replace(ScriptDraftBatches doc, ScriptDraftBatch run) =>
        doc.Runs.Any(r => r.Id == run.Id && r == run) ? doc : doc with { Runs = doc.Runs.Select(r => r.Id == run.Id ? run : r).ToArray() };
    private static async Task<ScriptDraftBatches> ReadAsync(string path, Guid project, CancellationToken ct)
    {
        var doc = await AtomicJsonFile.ReadAsync<ScriptDraftBatches>(path, ct) ?? new() { ProjectId = project };
        Validate(doc, project); return doc;
    }
    private static void Validate(ScriptDraftBatches doc, Guid project)
    {
        if (doc.ProjectId != project || doc.SchemaVersion != 1 || doc.Revision < 0 || doc.Runs is null || doc.Runs.Any(r => r is null) ||
            doc.Runs.Select(r => r.Id).Distinct().Count() != doc.Runs.Count || doc.Runs.Count(r => r.Active) > 1)
            throw new WorkspaceStoreException("The saved screenplay batch is invalid.");
        foreach (var run in doc.Runs) {
            if (run.Id == Guid.Empty || run.PlanId == Guid.Empty || run.PlanRevision < 0 || run.TabId == Guid.Empty || run.SessionId == Guid.Empty ||
                run.Model is null || run.Settings is null || run.Instructions is null || !Enum.IsDefined(run.State) || !Enum.IsDefined(run.SelectionSource) ||
                run.Sequences is not { Count: > 0 and <= 10000 } || run.Added < 0 || run.Added > run.Sequences.Count ||
                run.Sequences.Distinct().Count() != run.Sequences.Count || run.Sequences.Any(s => s is null || !ScriptAdaptation.Valid(s) || s.RunId != run.PlanId || s.ReviewRevision != run.PlanRevision) ||
                !run.Sequences.SequenceEqual(run.Sequences.OrderBy(s => s.Act).ThenBy(s => s.Scene)) || run.Jobs is null || run.Images is null || run.Conversation is null ||
                run.State == ScriptDraftBatchState.Completed && run.Added != run.Sequences.Count)
                throw new WorkspaceStoreException("The saved screenplay batch is invalid.");
            TextModelPolicy.Validate(run.Model);
            if (run.Pending is { } pending && (run.Added == run.Sequences.Count || pending.Kind != AiJobKind.ScriptAssistant || pending.Target != new AiJobTarget(project) ||
                pending.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)?.Payload<ScriptAssistantRequest>().Run is not { } request ||
                request.Id != pending.Id || request.DraftBatchId != run.Id || request.SourceScene != run.Sequences[run.Added] || request.Operation != WritingOperation.Continue))
                throw new WorkspaceStoreException("The pending screenplay request belongs to another batch.");
        }
    }
}
