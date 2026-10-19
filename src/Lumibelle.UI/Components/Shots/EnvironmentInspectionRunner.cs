using lumibelle.Services.Story;

namespace lumibelle.Components.Shots;

public sealed record EnvironmentInspectionBatch<T>(IReadOnlyList<string> Inspected, IReadOnlyList<string> Winners, bool Final, T Result);

// Policies, capacity reduction and job storage belong to the caller. Both pickers share bounded traversal.
public static class EnvironmentInspectionRunner
{
    public static async Task<T?> RunAsync<T>(IReadOnlyList<string> pool,
        Func<IReadOnlyList<string>, bool, int, CancellationToken, Task<EnvironmentInspectionBatch<T>>> inspect,
        ISet<string> inspected, CancellationToken ct) where T : class
    {
        var active = pool.Distinct().ToList();
        for (var round = 1; active.Count > 0; round++) {
            List<string> winners = [];
            for (var offset = 0; offset < active.Count;) {
                ct.ThrowIfCancellationRequested();
                var batch = active.Skip(offset).Take(24).ToArray();
                var outcome = await inspect(batch, offset == 0 && batch.Length == active.Count, round, ct);
                if (outcome.Inspected.Count == 0 || !outcome.Inspected.SequenceEqual(batch.Take(outcome.Inspected.Count)) ||
                    outcome.Winners.Any(id => !outcome.Inspected.Contains(id))) throw new WorkspaceStoreException("Inspection batch identities do not match.");
                foreach (var id in outcome.Inspected) inspected.Add(id);
                if (outcome.Final) {
                    if (pool.Any(id => !inspected.Contains(id))) throw new WorkspaceStoreException("Some environment sources were not inspected. Retry or select views manually.");
                    return outcome.Result;
                }
                winners.AddRange(outcome.Winners); offset += outcome.Inspected.Count;
            }
            if (winners.Count == 0) return null;
            var next = winners.Distinct().ToList();
            if (next.Count >= active.Count) throw new WorkspaceStoreException("The model cannot reduce this pool to a comparable selection. Choose another model or select views manually.");
            active = next;
        }
        return null;
    }
}
