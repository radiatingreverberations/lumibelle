using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;
using lumibelle.Services.Storage;

namespace lumibelle.Services.Shots;

// Best-effort upgrade for fully downloaded old takes. This never contacts a
// provider or starts inference; incomplete remote work remains explicitly legacy.
public sealed class LegacyVideoArchiveRecovery(ProjectFiles projects, IShotStore shots,
    ILogger<LegacyVideoArchiveRecovery> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            foreach (var project in (await projects.ListProjectsAsync(ct)).Projects)
                try { await RecoverProjectAsync(project.Id, ct); }
                catch (ProjectUpgradeRequiredException e)
                { logger.LogWarning("Earlier video archives in project {Project} are waiting for an upgrade. {Reason}", project.Id, e.Message); }
                catch (Exception e) when (e is WorkspaceStoreException or IOException)
                { logger.LogWarning(e, "Earlier video archives in project {Project} could not be published", project.Id); }
        }
        catch (Exception e) when (e is ProjectStoreException or WorkspaceStoreException or IOException)
        { logger.LogWarning(e, "Earlier video archive recovery could not read the project library"); }
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    internal async Task RecoverProjectAsync(Guid projectId, CancellationToken ct)
    {
        foreach (var run in await shots.RunsAsync(projectId, ct))
        {
            foreach (var candidate in run.Candidates.Where(c => c.State != VideoCandidateState.Complete && c.ArchivedTake is not null))
            {
                var take = candidate.ArchivedTake!;
                try
                {
                    if (take.Id != candidate.TakeId || take.RunId != run.Id || take.ShotId != run.Snapshot.Shot.Id || take.Candidate != candidate.Number ||
                        take.Seed != candidate.Seed || take.AiJobId is not null || !JsonElement.DeepEquals(
                            JsonSerializer.SerializeToElement(take.Snapshot, AtomicJsonFile.Options), JsonSerializer.SerializeToElement(run.Snapshot, AtomicJsonFile.Options)))
                        throw new WorkspaceStoreException("The earlier archive does not match its recorded candidate.");
                    var directory = await shots.RunDirectoryAsync(projectId, run.Id, ct);
                    await shots.PublishTakeAsync(projectId, take, Path.Combine(directory, "candidate-" + candidate.Number), ct);
                    candidate.State = VideoCandidateState.Complete; candidate.Error = null;
                    await shots.SaveRunAsync(run, ct);
                }
                catch (Exception e) when (e is WorkspaceStoreException or IOException)
                { logger.LogWarning(e, "Earlier video archive {Take} is retained for explicit recovery", candidate.TakeId); }
            }
        }
    }
}
