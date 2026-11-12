using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Storage;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Projects;

public sealed record ProjectUpgradeStatus(int From, int To, IReadOnlyList<string> Steps, long BackupBytes, string? Busy)
{
    public bool ReconciliationPending { get; init; }
    public bool Needed => From < To || ReconciliationPending;
}

public interface IProjectUpgrades
{
    Task<ProjectUpgradeStatus> StatusAsync(Guid project, CancellationToken ct = default);
    Task UpgradeAsync(Guid project, IProgress<string>? progress = null, CancellationToken ct = default);
}

public sealed class ProjectUpgrades(ApplicationPaths paths, ProjectFiles files, ProjectLocations locations,
    IAiJobStore? jobs = null, IGenerationSetupStore? setups = null, LegacyVideoArchiveRecovery? archives = null) : IProjectUpgrades
{
    public async Task<ProjectUpgradeStatus> StatusAsync(Guid project, CancellationToken ct = default)
    {
        var directory = await files.LocateAsync(project, ct);
        var from = ProjectFormat.Inspect(directory);
        var pending = from == ProjectFormat.Current && ReconciliationPending(directory);
        var backup = Path.Combine(directory, ProjectDatabase.FileName);
        return new(from, ProjectFormat.Current,
            pending ? ["Reconnect this folder’s generation settings"] : ProjectUpgrader.Pending(from).Select(s => s.Summary).ToArray(),
            from < ProjectFormat.Current && File.Exists(backup) && !File.Exists(ProjectUpgrader.BackupPath(directory, from)) ? new FileInfo(backup).Length : 0,
            await ProjectActivity.BusyAsync(jobs, project, "upgrade it", ct)) { ReconciliationPending = pending };
    }

    public async Task UpgradeAsync(Guid project, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var directory = await files.LocateAsync(project, ct);
        using (await ProjectFiles.LockAsync(directory, ct))
        {
            var from = ProjectFormat.Inspect(directory);
            if (from < ProjectFormat.Current || ReconciliationPending(directory))
            {
                await ProjectActivity.RequireIdleAsync(jobs, project, "upgrade it", ct);
                var linked = await locations.FindAsync(project, ct) is not null;
                await ProjectUpgrader.RunAsync(directory, project, progress, ct, reconcileSetups: linked);
                using var scope = ProjectDatabase.AllowUpgrade(directory);
                if (ReconciliationPending(directory))
                {
                    progress?.Report("Reconnecting this folder’s generation settings…");
                    if (linked) await FileProductionStore.ReconcileFolderSetupsAsync(directory, project, setups ?? new FileGenerationSetupStore(paths), ct);
                    ProjectDatabase.For(directory).Write(s => s.Execute("DELETE FROM meta WHERE key = 'format_setup_reconciliation'"));
                }
            }
        }
        // Publishing recovered takes acquires the same project lock through the shot store.
        if (archives is not null) await archives.RecoverProjectSafelyAsync(project, ct);
    }

    private static bool ReconciliationPending(string directory) => ProjectFormat.ReconciliationPending(directory);
}
