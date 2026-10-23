using lumibelle.Models;

namespace lumibelle.Services.Story;

public sealed class FileScriptStore(ProjectFiles files, TimeProvider clock) : IScriptStore
{
    public async Task<ScriptSourceSnapshot?> CaptureSourceAsync(Guid projectId, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        var dir = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(dir, cancellationToken);
        var current = await ReadAsync(dir, projectId, cancellationToken);
        if (expectedRevision is { } revision && current.Revision != revision)
            throw new WorkspaceStoreException("The script changed. Reopen the scene selection to use the latest saved script.");
        if (!ScriptStructure.Sections(current.Blocks).Any(s => s.Kind == ScriptBlockKind.Scene)) return null;
        var id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{projectId:D}/{current.Revision}"))[..16]);
        var path = Path.Combine(dir, "script-sources", $"{id:D}.json");
        var existing = Directory.Exists(Path.GetDirectoryName(path)) ? await AtomicJsonFile.ReadAsync<ScriptSourceSnapshot>(path, cancellationToken) : null;
        if (existing is not null)
        {
            if (existing.Id != id || existing.ProjectId != projectId || existing.SourceRevision != current.Revision || ScriptStructure.Fingerprint(existing.Blocks) != ScriptStructure.Fingerprint(current.Blocks))
                throw new WorkspaceStoreException("The captured script source is invalid.");
            return existing;
        }
        var snapshot = new ScriptSourceSnapshot(id, projectId, current.Revision, clock.GetUtcNow(), current.Blocks.Select(b => b.Copy()).ToList());
        await AtomicJsonFile.WriteAsync(path, snapshot, cancellationToken);
        return snapshot;
    }
    public async Task<ScriptSourceSnapshot?> LoadSourceAsync(Guid projectId, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var dir = await files.DirectoryAsync(projectId, cancellationToken);
        var path = Path.Combine(dir, "script-sources", $"{snapshotId:D}.json");
        var source = Directory.Exists(Path.GetDirectoryName(path)) ? await AtomicJsonFile.ReadAsync<ScriptSourceSnapshot>(path, cancellationToken) : null;
        if (source is null) return await ReadApprovedAsync(dir, projectId, snapshotId, cancellationToken);
        if (source.Id != snapshotId || source.ProjectId != projectId || source.SourceRevision < 0)
            throw new WorkspaceStoreException("The captured script source is invalid.");
        ScriptStructure.ValidateBlocks(source.Blocks);
        return source;
    }
    public async Task<ScriptDocument> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await ReadAsync(await files.DirectoryAsync(projectId, cancellationToken), projectId, cancellationToken);
    public async Task<ScriptDocument> SaveAsync(ScriptDocument document, long expectedRevision, string? recoveryReason = null, CancellationToken cancellationToken = default)
    {
        Validate(document, document.ProjectId);
        var dir = await files.DirectoryAsync(document.ProjectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(dir, cancellationToken);
        var current = await ReadAsync(dir, document.ProjectId, cancellationToken);
        if (current.Revision != expectedRevision) throw new WorkspaceConflictException();
        return await PublishAsync(dir, current, document, recoveryReason, cancellationToken);
    }
    public async Task<IReadOnlyList<ScriptRecovery>> ListRecoveryAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(await files.DirectoryAsync(projectId, cancellationToken), "script-history");
        if (!Directory.Exists(dir)) return [];
        List<ScriptRecovery> result = [];
        foreach (var path in Directory.GetFiles(dir, "*.json"))
        {
            var recovery = await AtomicJsonFile.ReadAsync<ScriptRecovery>(path, cancellationToken)
                ?? throw new WorkspaceStoreException("A recovery version is invalid.");
            if (recovery.Id.ToString("D") != Path.GetFileNameWithoutExtension(path)) throw new WorkspaceStoreException("A recovery version is invalid.");
            Validate(recovery.Document, projectId); result.Add(recovery);
        }
        return result.OrderByDescending(r => r.CreatedUtc).ToList();
    }
    public async Task<ScriptDocument> RestoreAsync(Guid projectId, Guid recoveryId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var dir = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(dir, cancellationToken);
        var current = await ReadAsync(dir, projectId, cancellationToken);
        if (current.Revision != expectedRevision) throw new WorkspaceConflictException();
        var recovery = await AtomicJsonFile.ReadAsync<ScriptRecovery>(Path.Combine(dir, "script-history", $"{recoveryId:D}.json"), cancellationToken)
            ?? throw new WorkspaceStoreException("That recovery version is unavailable.");
        if (recovery.Id != recoveryId) throw new WorkspaceStoreException("That recovery version is invalid.");
        Validate(recovery.Document, projectId);
        return await PublishAsync(dir, current, recovery.Document with { AppliedProposalIds = current.AppliedProposalIds.Union(recovery.Document.AppliedProposalIds).ToList() },
            "Before restoring a draft", cancellationToken);
    }
    public async Task<ScriptDocument> ApproveAsync(Guid projectId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var dir = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(dir, cancellationToken);
        var current = await ReadAsync(dir, projectId, cancellationToken);
        if (current.Revision != expectedRevision) throw new WorkspaceConflictException();
        var sections = ScriptStructure.Sections(current.Blocks);
        if (!sections.Any(s => s.Kind == ScriptBlockKind.Scene) || !current.Blocks.Any(b => b.Kind is ScriptBlockKind.Action or ScriptBlockKind.Dialogue && !string.IsNullOrWhiteSpace(b.Text)) ||
            current.Blocks.Any(b => b.Kind is ScriptBlockKind.Scene or ScriptBlockKind.Act && string.IsNullOrWhiteSpace(b.Text)))
            throw new WorkspaceStoreException("Add a named scene and some action or dialogue before approving the script.");
        var approved = await ReadApprovedAsync(dir, projectId, current.ApprovedSnapshotId, cancellationToken);
        if (ScriptStructure.SameProduction(current, approved)) return current;
        var snapshot = new ApprovedScriptSnapshot(Guid.NewGuid(), projectId, current.Revision, clock.GetUtcNow(), current.Brief.Duration,
            current.Brief.ProductionProfile, current.Blocks.Select(b => b.Copy()).ToList());
        await AtomicJsonFile.WriteAsync(Path.Combine(dir, "script-approved", $"{snapshot.Id:D}.json"), snapshot, cancellationToken);
        var saved = current with { ApprovedSnapshotId = snapshot.Id, Revision = current.Revision + 1, UpdatedUtc = clock.GetUtcNow() };
        await AtomicJsonFile.WriteAsync(Path.Combine(dir, "script.json"), saved, cancellationToken);
        return saved;
    }
    public async Task<ApprovedScriptSnapshot?> LoadApprovedAsync(Guid projectId, Guid? snapshotId = null, CancellationToken cancellationToken = default)
    {
        var dir = await files.DirectoryAsync(projectId, cancellationToken);
        var id = snapshotId ?? (await ReadAsync(dir, projectId, cancellationToken)).ApprovedSnapshotId;
        return await ReadApprovedAsync(dir, projectId, id, cancellationToken);
    }
    private static async Task<ApprovedScriptSnapshot?> ReadApprovedAsync(string dir, Guid projectId, Guid? id, CancellationToken ct)
    {
        if (id is null) return null;
        var snapshot = await AtomicJsonFile.ReadAsync<ApprovedScriptSnapshot>(Path.Combine(dir, "script-approved", $"{id:D}.json"), ct);
        if (snapshot is null || snapshot.Id != id || snapshot.ProjectId != projectId || snapshot.SourceRevision < 0 || snapshot.Duration is null || snapshot.ProductionProfile is null)
            throw new WorkspaceStoreException("The captured script source is unavailable or invalid.");
        ScriptStructure.ValidateBlocks(snapshot.Blocks); return snapshot;
    }
    private async Task<ScriptDocument> PublishAsync(string dir, ScriptDocument current, ScriptDocument next, string? reason, CancellationToken ct)
    {
        if (reason is not null)
        {
            var recovery = new ScriptRecovery(Guid.NewGuid(), reason, clock.GetUtcNow(), current);
            await AtomicJsonFile.WriteAsync(Path.Combine(dir, "script-history", $"{recovery.Id:D}.json"), recovery, ct);
        }
        var saved = next with { SchemaVersion = 2, Revision = current.Revision + 1, UpdatedUtc = clock.GetUtcNow(), ApprovedSnapshotId = current.ApprovedSnapshotId };
        await AtomicJsonFile.WriteAsync(Path.Combine(dir, "script.json"), saved, ct); return saved;
    }
    private static async Task<ScriptDocument> ReadAsync(string dir, Guid id, CancellationToken ct)
    {
        var doc = await AtomicJsonFile.ReadAsync<ScriptDocument>(Path.Combine(dir, "script.json"), ct) ?? new ScriptDocument { ProjectId = id };
        Validate(doc, id); return doc;
    }
    public static void Validate(ScriptDocument doc, Guid id)
    {
        if (doc is null || id == Guid.Empty || doc.ProjectId != id || doc.SchemaVersion is not (1 or 2) || doc.Revision < 0 || doc.Brief is null ||
            doc.Brief.Idea is null || doc.Brief.Duration is null || doc.Brief.Style is null || doc.Brief.ProductionProfile is null || doc.AppliedProposalIds is null)
            throw new WorkspaceStoreException("The script file is invalid or uses an unsupported format. It has not been replaced.");
        ScriptStructure.ValidateBlocks(doc.Blocks);
        ScriptAdaptation.ValidateLinks(doc);
    }
}
