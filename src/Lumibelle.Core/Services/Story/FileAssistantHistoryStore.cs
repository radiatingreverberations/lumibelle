using lumibelle.Models;

namespace lumibelle.Services.Story;

public sealed class FileAssistantHistoryStore(ProjectFiles files, ApplicationSession session) : IAssistantHistoryStore
{
    public async Task<AssistantHistory> LoadAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var history = await ReadAsync(directory, projectId, cancellationToken);
        if (history.Runs.Any(run => run.JobId is null && run.Status == AssistantRunStatus.Running && run.SessionId != session.Id))
        {
            history = history with { Revision = history.Revision + 1, Runs = history.Runs.Select(run =>
                run.JobId is null && run.Status == AssistantRunStatus.Running && run.SessionId != session.Id
                ? run with { Status = AssistantRunStatus.Interrupted, Revision = run.Revision + 1, Error = "The application restarted before this request finished." } : run).ToList() };
            await AtomicJsonFile.WriteAsync(Path.Combine(directory, "script-assistant.json"), history, cancellationToken);
        }
        return history;
    }

    public async Task<AssistantRun> SaveRunAsync(Guid projectId, AssistantRun run, CancellationToken cancellationToken = default)
    {
        ValidateRun(run);
        var directory = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(directory, cancellationToken);
        var history = await ReadAsync(directory, projectId, cancellationToken);
        var existing = history.Runs.SingleOrDefault(item => item.Id == run.Id);
        if ((existing?.Revision ?? 0) != run.Revision) throw new WorkspaceConflictException();
        var saved = run with { Revision = run.Revision + 1 };
        var runs = history.Runs.ToList();
        if (existing is null) runs.Add(saved); else runs[runs.IndexOf(existing)] = saved;
        await AtomicJsonFile.WriteAsync(Path.Combine(directory, "script-assistant.json"), history with { Revision = history.Revision + 1, Runs = runs }, cancellationToken);
        return saved;
    }

    private static async Task<AssistantHistory> ReadAsync(string directory, Guid id, CancellationToken ct)
    {
        var history = await AtomicJsonFile.ReadAsync<AssistantHistory>(Path.Combine(directory, "script-assistant.json"), ct)
            ?? new AssistantHistory { ProjectId = id };
        if (history.ProjectId != id || history.SchemaVersion != 1 || history.Revision < 0 || history.Runs is null ||
            history.Runs.Any(run => run is null) || history.Runs.Select(run => run.Id).Distinct().Count() != history.Runs.Count)
            throw new WorkspaceStoreException("The assistant history is invalid or uses an unsupported format.");
        foreach (var run in history.Runs) ValidateRun(run);
        return history;
    }

    private static void ValidateRun(AssistantRun run)
    {
        if (run.Id == Guid.Empty || run.JobId == Guid.Empty || run.DraftBatchId == Guid.Empty || run.CorrectedFromRunId == Guid.Empty || run.CorrectedFromRunId == run.Id ||
            run.CorrectedFromRunId is not null && run.JobId is not null || run.Revision < 0 || run.Target is null || run.SourceFingerprint is null || run.Output is null || run.Instructions is null ||
            run.Model is null || !Enum.IsDefined(run.Status) || !Enum.IsDefined(run.Operation) || !Enum.IsDefined(run.Backend) || !Enum.IsDefined(run.Target.Scope))
            throw new WorkspaceStoreException("The assistant history contains an invalid request.");
        ScriptStructure.ValidateTarget(run.Target);
        if (run.NormalizationNotes is { } notes && (notes.Count > 12 || notes.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 500)))
            throw new WorkspaceStoreException("The screenplay format notices are invalid.");
        if (run.SourceScene is { } scene && (!ScriptAdaptation.Valid(scene) || run.Operation is not (WritingOperation.Continue or WritingOperation.Rewrite) || run.Target.Scope != ScriptScope.Document || run.EditFormat != 0))
            throw new WorkspaceStoreException("The saved source scene request is invalid.");
        if (run.AppliedTarget is not null) ScriptStructure.ValidateTarget(run.AppliedTarget);
        if (run.Proposal is not null) ScriptStructure.ValidateBlocks(run.Proposal);
    }
}
