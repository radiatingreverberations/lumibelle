using lumibelle.Models;
using lumibelle.Services.Storage;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public interface ICutStore
{
    Task<CutDocument> LoadAsync(Guid projectId, CancellationToken ct = default);
    Task<CutDocument> SaveAsync(Guid projectId, IReadOnlyList<CutClip> clips, long expectedRevision, CancellationToken ct = default);
}

public sealed class FileCutStore(ProjectFiles files, IShotStore shots, TimeProvider clock) : ICutStore
{
    internal static readonly ProjectDocumentStore<CutDocument> Documents = new(new()
    {
        Kind = "cut", Description = "the cut", Legacy = ["cut.json"],
        Empty = id => new() { ProjectId = id }, Lists = [new("clips")]
    });

    public async Task<CutDocument> LoadAsync(Guid projectId, CancellationToken ct = default)
    {
        var directory = await files.DirectoryAsync(projectId, ct);
        return await Read(directory, projectId, ct);
    }

    private static async Task<CutDocument> Read(string directory, Guid projectId, CancellationToken ct)
    {
        var cut = await Documents.LoadAsync(directory, projectId, ct);
        Validate(cut, projectId);
        return cut;
    }

    public static void Validate(CutDocument cut, Guid projectId)
    {
        if (projectId == Guid.Empty || cut.ProjectId != projectId || cut.SchemaVersion != 1 || cut.Revision < 0 || cut.Clips is null ||
            cut.Clips.Any(c => c is null || c.Id == Guid.Empty || c.ShotId == Guid.Empty || c.TakeId == Guid.Empty ||
                c.ShotTitle is null || c.TakeLabel is null || !double.IsFinite(c.Fps) || c.Fps <= 0 || !double.IsFinite(c.Duration) || c.FrameCount < 1 ||
                c.StartFrame < 0 || c.EndFrameExclusive <= c.StartFrame || c.EndFrameExclusive > c.FrameCount) ||
            cut.Clips.Select(c => c.Id).Distinct().Count() != cut.Clips.Count)
            throw new WorkspaceStoreException("The cut contains an invalid clip or frame range. Its saved version has not been replaced.");
    }

    public async Task<CutDocument> SaveAsync(Guid projectId, IReadOnlyList<CutClip> clips, long expectedRevision, CancellationToken ct = default)
    {
        var captured = ShotCopy.Of(clips.ToList());
        var next = new CutDocument { ProjectId = projectId, Clips = captured, Revision = expectedRevision };
        Validate(next, projectId);
        var directory = await files.DirectoryAsync(projectId, ct);
        using var gate = await ProjectFiles.LockAsync(directory, ct);
        var previous = await Read(directory, projectId, ct);
        if (previous.Revision != expectedRevision) throw new WorkspaceConflictException();
        var library = await shots.LoadAsync(projectId, ct);
        foreach (var clip in captured)
        {
            var take = library.Takes.FirstOrDefault(t => t.Id == clip.TakeId && t.ShotId == clip.ShotId);
            if (take is not null)
            {
                if (!library.Shots.Any(s => s.Id == clip.ShotId) || clip.FrameCount != take.FrameCount || clip.Fps != take.Fps)
                    throw new WorkspaceStoreException("The chosen take does not match this shot or frame range. Refresh the available takes.");
            }
            else
            {
                // Retain broken links for repair, but never introduce an invented or cross-project source.
                var old = previous.Clips.FirstOrDefault(c => c.Id == clip.Id);
                if (old is null || old.TakeId != clip.TakeId || old.ShotId != clip.ShotId || old.FrameCount != clip.FrameCount || old.Fps != clip.Fps)
                    throw new WorkspaceStoreException("A chosen take is no longer available. Restore it or remove the clip before saving.");
            }
        }
        next.Revision++; next.UpdatedUtc = clock.GetUtcNow();
        await Documents.SaveAsync(directory, projectId, next, ct);
        return next.Copy();
    }
}
