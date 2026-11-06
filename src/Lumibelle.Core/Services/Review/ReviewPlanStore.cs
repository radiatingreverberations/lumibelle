using lumibelle.Models;
using lumibelle.Services.Storage;
using lumibelle.Services.Story;

namespace lumibelle.Services.Review;

/// <summary>
/// Review plans, kept with their project in <c>project.sqlite</c>. Changes are functions over the stored plan, applied under the
/// project's save lock; a change must not call another store, which would wait on the same lock.
/// </summary>
public sealed class ReviewPlanStore(ProjectFiles files, TimeProvider clock)
{
    internal static readonly ProjectDocumentStore<ReviewPlanDocument> Documents = new(new()
    {
        Kind = "review-plans", Description = "the review plans", Empty = id => new() { ProjectId = id }, Lists = [new("plans")]
    });

    /// <summary>Raised after a plan is saved, with the project and plan ids.</summary>
    public event Action<Guid, Guid>? Changed;

    public async Task<IReadOnlyList<ReviewPlan>> ListAsync(Guid projectId, CancellationToken ct = default) =>
        (await ReadAsync(await files.DirectoryAsync(projectId, ct), projectId, ct)).Plans;

    public async Task<ReviewPlan> GetAsync(Guid projectId, Guid planId, CancellationToken ct = default) =>
        (await ListAsync(projectId, ct)).FirstOrDefault(p => p.Id == planId) ?? throw new WorkspaceStoreException("That plan is not in this project.");

    public async Task<ReviewPlan> CreateAsync(Guid projectId, string title, string proposedBy, CancellationToken ct = default)
    {
        title = (title ?? "").Trim();
        if (title.Length is 0 or > 200) throw new WorkspaceStoreException("Give the plan a title of up to 200 characters.");
        var plan = new ReviewPlan { Title = title, ProposedBy = proposedBy, CreatedUtc = clock.GetUtcNow(), Log = [new(clock.GetUtcNow(), proposedBy, "Started the plan.")] };
        var directory = await files.DirectoryAsync(projectId, ct);
        using (await ProjectFiles.LockAsync(directory, ct))
        {
            var document = await ReadAsync(directory, projectId, ct);
            if (document.Plans.Count >= 200) throw new WorkspaceStoreException("This project has 200 plans. Remove finished ones before starting another.");
            document.Plans.Add(plan); document.Revision++;
            await Documents.SaveAsync(directory, projectId, document, ct);
        }
        Changed?.Invoke(projectId, plan.Id);
        return plan;
    }

    /// <summary>Applies a change to the stored plan. <paramref name="proposal"/> says the change alters what is proposed or decided, which an approval is bound to.</summary>
    public async Task<ReviewPlan> UpdateAsync(Guid projectId, Guid planId, Func<ReviewPlan, ReviewPlan> change, bool proposal = false, CancellationToken ct = default)
    {
        var directory = await files.DirectoryAsync(projectId, ct); ReviewPlan next;
        using (await ProjectFiles.LockAsync(directory, ct))
        {
            var document = await ReadAsync(directory, projectId, ct);
            var index = document.Plans.FindIndex(p => p.Id == planId);
            if (index < 0) throw new WorkspaceStoreException("That plan is not in this project.");
            next = change(document.Plans[index]);
            if (ReferenceEquals(next, document.Plans[index])) return next;
            if (proposal) next = next with { Revision = next.Revision + 1 };
            if (next.Log.Count > 500) next = next with { Log = next.Log.Skip(next.Log.Count - 500).ToArray() };
            document.Plans[index] = next; document.Revision++;
            await Documents.SaveAsync(directory, projectId, document, ct);
        }
        Changed?.Invoke(projectId, planId);
        return next;
    }

    public async Task RemoveAsync(Guid projectId, Guid planId, CancellationToken ct = default)
    {
        var directory = await files.DirectoryAsync(projectId, ct);
        using (await ProjectFiles.LockAsync(directory, ct))
        {
            var document = await ReadAsync(directory, projectId, ct);
            var plan = document.Plans.FirstOrDefault(p => p.Id == planId);
            if (plan is null) return;
            if (plan.Active && plan.Steps.Any(s => s.State == ReviewStepState.Running)) throw new WorkspaceStoreException("This plan has a render running. Stop the plan first.");
            document.Plans.Remove(plan); document.Revision++;
            await Documents.SaveAsync(directory, projectId, document, ct);
        }
        Changed?.Invoke(projectId, planId);
    }

    private static async Task<ReviewPlanDocument> ReadAsync(string directory, Guid projectId, CancellationToken ct)
    {
        var document = await Documents.LoadAsync(directory, projectId, ct);
        if (document.SchemaVersion != 1 || document.ProjectId != projectId || document.Plans is null || document.Plans.Any(p => p is null || p.Id == Guid.Empty || p.Steps is null || p.Findings is null || p.Log is null) ||
            document.Plans.Select(p => p.Id).Distinct().Count() != document.Plans.Count)
            throw new WorkspaceStoreException("The review plans could not be read. They have not been replaced.");
        return document;
    }
}
