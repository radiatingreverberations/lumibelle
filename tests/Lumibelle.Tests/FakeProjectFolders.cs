using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Projects;

namespace Lumibelle.Tests;

// Every project is in the library folder unless a test adds a location.
internal sealed class FakeProjectFolders : IProjectFolders
{
    public List<ProjectLocation> Locations { get; } = [];
    public Task<IReadOnlyList<ProjectLocation>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProjectLocation>>(Locations.ToArray());
    public Task<ProjectLocation?> LocationAsync(Guid project, CancellationToken ct = default) => Task.FromResult(Locations.FirstOrDefault(l => l.ProjectId == project));
    public Task<ProjectInfo> OpenAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ProjectLocation> MoveOutAsync(Guid project, string parent, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RemoveAsync(Guid project, CancellationToken ct = default) { Locations.RemoveAll(l => l.ProjectId == project); return Task.CompletedTask; }
}

// Reports a fixed plan and records which parts a confirmation asked for.
internal sealed class FakeProjectCompaction : IProjectCompaction
{
    public ProjectCompactionPlan? Plan { get; set; }
    public IReadOnlySet<CompactionPart>? Compacted { get; private set; }
    public Task<ProjectCompactionPlan> InspectAsync(Guid project, CancellationToken ct = default) =>
        Task.FromResult(Plan ?? new(project, [], [], 0, [], 0, [], 0, null, 0));
    public Task<ProjectCompactionResult> CompactAsync(ProjectCompactionPlan plan, IReadOnlySet<CompactionPart> parts, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default)
    { Compacted = parts; return Task.FromResult(new ProjectCompactionResult(parts.Sum(plan.Bytes), [])); }
}
