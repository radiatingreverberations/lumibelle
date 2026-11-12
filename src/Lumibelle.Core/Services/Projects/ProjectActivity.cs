using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Projects;

internal static class ProjectActivity
{
    public static async Task<string?> BusyAsync(IAiJobStore? jobs, Guid project, string action, CancellationToken ct) =>
        jobs is not null && (await jobs.ReadAsync(ct)).Jobs.Any(j => j.Target.ProjectId == project && j.LocksTarget)
            ? $"This project has queued or working AI requests. Wait for them to finish, or cancel them, then {action}." : null;

    public static async Task RequireIdleAsync(IAiJobStore? jobs, Guid project, string action, CancellationToken ct)
    {
        if (await BusyAsync(jobs, project, action, ct) is { } busy) throw new WorkspaceStoreException(busy);
    }
}
