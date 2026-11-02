using lumibelle.Models;
using lumibelle.Services.Storage;

namespace lumibelle.Services.Story;

/// <summary>Immutable script versions: captured sources, recovery drafts and approved snapshots.</summary>
internal sealed record ScriptSnapshots
{
    public Guid ProjectId { get; init; }
    public List<ScriptSourceSnapshot> Sources { get; init; } = [];
    public List<ScriptRecovery> History { get; init; } = [];
    public List<ApprovedScriptSnapshot> Approved { get; init; } = [];
}

public sealed class FileScriptStore(ProjectFiles files, TimeProvider clock) : IScriptStore
{
    internal static readonly ProjectDocumentStore<ScriptDocument> Documents = new(new()
    {
        Kind = "script", Description = "the script", Legacy = ["script.json"],
        Empty = id => new() { ProjectId = id }, Lists = [new("blocks"), new("appliedProposalIds", Key: null), new("sourceSceneLinks", Key: null)]
    });
    // Formerly one file each in script-sources/, script-history/ and script-approved/.
    internal static readonly ProjectDocumentStore<ScriptSnapshots> Snapshots = new(new()
    {
        Kind = "script_snapshots", Description = "the saved script versions", Legacy = ["script-sources", "script-history", "script-approved"],
        Empty = id => new() { ProjectId = id }, Lists = [new("sources"), new("history"), new("approved")],
        ReadLegacy = async (dir, id, ct) => new ScriptSnapshots
        {
            ProjectId = id,
            Sources = await ReadFolderAsync<ScriptSourceSnapshot>(dir, "script-sources", s => s.Id, ct),
            History = await ReadFolderAsync<ScriptRecovery>(dir, "script-history", r => r.Id, ct),
            Approved = await ReadFolderAsync<ApprovedScriptSnapshot>(dir, "script-approved", a => a.Id, ct)
        }
    });
    private static async Task<List<T>> ReadFolderAsync<T>(string dir, string folder, Func<T, Guid> id, CancellationToken ct)
    {
        var path = Path.Combine(dir, folder);
        if (!Directory.Exists(path)) return [];
        List<T> items = [];
        foreach (var file in Directory.GetFiles(path, "*.json").Order(StringComparer.Ordinal))
        {
            var item = await AtomicJsonFile.ReadAsync<T>(file, ct) ?? throw new WorkspaceStoreException("A saved script version is invalid.");
            if (id(item).ToString("D") != Path.GetFileNameWithoutExtension(file)) throw new WorkspaceStoreException("A saved script version is invalid.");
            items.Add(item);
        }
        return items;
    }
    /// <summary>Whether a captured source or approved snapshot with this id is saved.</summary>
    internal static async Task<bool> HasSnapshotAsync(string dir, Guid projectId, Guid id, CancellationToken ct) =>
        await Snapshots.FindAsync(dir, projectId, "sources", id.ToString("D"), attach: false, ct) is not null ||
        await Snapshots.FindAsync(dir, projectId, "approved", id.ToString("D"), attach: false, ct) is not null;
    internal static async Task AddSnapshotsAsync(string dir, Guid projectId, CancellationToken ct,
        IEnumerable<ScriptSourceSnapshot>? sources = null, ScriptRecovery? recovery = null, ApprovedScriptSnapshot? approved = null)
    {
        var snapshots = await Snapshots.LoadAsync(dir, projectId, ct);
        foreach (var source in sources ?? []) if (snapshots.Sources.All(s => s.Id != source.Id)) snapshots.Sources.Add(source);
        if (recovery is not null) snapshots.History.Add(recovery);
        if (approved is not null) snapshots.Approved.Add(approved);
        await Snapshots.SaveAsync(dir, projectId, snapshots, ct);
    }

    public async Task<ScriptSourceSnapshot?> CaptureSourceAsync(Guid projectId, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        var dir = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(dir, cancellationToken);
        var current = await ReadAsync(dir, projectId, cancellationToken);
        if (expectedRevision is { } revision && current.Revision != revision)
            throw new WorkspaceStoreException("The script changed. Reopen the scene selection to use the latest saved script.");
        if (!ScriptStructure.Sections(current.Blocks).Any(s => s.Kind == ScriptBlockKind.Scene)) return null;
        var id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{projectId:D}/{current.Revision}"))[..16]);
        var existing = await Snapshots.FindAsync<ScriptSourceSnapshot>(dir, projectId, "sources", id.ToString("D"), attach: false, cancellationToken);
        if (existing is not null)
        {
            if (existing.Id != id || existing.ProjectId != projectId || existing.SourceRevision != current.Revision || ScriptStructure.Fingerprint(existing.Blocks) != ScriptStructure.Fingerprint(current.Blocks))
                throw new WorkspaceStoreException("The captured script source is invalid.");
            return existing;
        }
        var snapshot = new ScriptSourceSnapshot(id, projectId, current.Revision, clock.GetUtcNow(), current.Blocks.Select(b => b.Copy()).ToList());
        await AddSnapshotsAsync(dir, projectId, cancellationToken, sources: [snapshot]);
        return snapshot;
    }
    public async Task<ScriptSourceSnapshot?> LoadSourceAsync(Guid projectId, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var dir = await files.DirectoryAsync(projectId, cancellationToken);
        var source = await Snapshots.FindAsync<ScriptSourceSnapshot>(dir, projectId, "sources", snapshotId.ToString("D"), attach: false, cancellationToken);
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
        var dir = await files.DirectoryAsync(projectId, cancellationToken);
        var result = (await Snapshots.LoadAsync(dir, projectId, cancellationToken)).History;
        foreach (var recovery in result)
        {
            if (recovery is null) throw new WorkspaceStoreException("A recovery version is invalid.");
            Validate(recovery.Document, projectId);
        }
        return result.OrderByDescending(r => r.CreatedUtc).ToList();
    }
    public async Task<ScriptDocument> RestoreAsync(Guid projectId, Guid recoveryId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var dir = await files.DirectoryAsync(projectId, cancellationToken);
        using var gate = await ProjectFiles.LockAsync(dir, cancellationToken);
        var current = await ReadAsync(dir, projectId, cancellationToken);
        if (current.Revision != expectedRevision) throw new WorkspaceConflictException();
        var recovery = await Snapshots.FindAsync<ScriptRecovery>(dir, projectId, "history", recoveryId.ToString("D"), attach: false, cancellationToken)
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
        await AddSnapshotsAsync(dir, projectId, cancellationToken, approved: snapshot);
        var saved = current with { ApprovedSnapshotId = snapshot.Id, Revision = current.Revision + 1, UpdatedUtc = clock.GetUtcNow() };
        await Documents.SaveAsync(dir, projectId, saved, cancellationToken);
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
        var snapshot = await Snapshots.FindAsync<ApprovedScriptSnapshot>(dir, projectId, "approved", id.Value.ToString("D"), attach: false, ct);
        if (snapshot is null || snapshot.Id != id || snapshot.ProjectId != projectId || snapshot.SourceRevision < 0 || snapshot.Duration is null || snapshot.ProductionProfile is null)
            throw new WorkspaceStoreException("The captured script source is unavailable or invalid.");
        ScriptStructure.ValidateBlocks(snapshot.Blocks); return snapshot;
    }
    private async Task<ScriptDocument> PublishAsync(string dir, ScriptDocument current, ScriptDocument next, string? reason, CancellationToken ct)
    {
        if (reason is not null)
        {
            var recovery = new ScriptRecovery(Guid.NewGuid(), reason, clock.GetUtcNow(), current);
            await AddSnapshotsAsync(dir, current.ProjectId, ct, recovery: recovery);
        }
        var saved = next with { SchemaVersion = 2, Revision = current.Revision + 1, UpdatedUtc = clock.GetUtcNow(), ApprovedSnapshotId = current.ApprovedSnapshotId };
        await Documents.SaveAsync(dir, saved.ProjectId, saved, ct); return saved;
    }
    private static async Task<ScriptDocument> ReadAsync(string dir, Guid id, CancellationToken ct)
    {
        var doc = await Documents.LoadAsync(dir, id, ct);
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
