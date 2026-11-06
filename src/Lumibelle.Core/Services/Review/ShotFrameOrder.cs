using lumibelle.Models;

namespace lumibelle.Services.Review;

/// <summary>
/// The order shots must be rendered in. A shot that uses a frame from another shot's take, as a shot frame or as
/// its starting frame, can only be rendered after that shot has a take.
/// </summary>
public static class ShotFrameOrder
{
    public static ShotRenderOrder Build(ProjectReviewSnapshot snapshot) => Build(snapshot.Rows.Select(r => r.Id).ToArray(), Sources(snapshot));

    /// <summary>For each shot, the other shots whose takes it takes frames from.</summary>
    public static IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Sources(ProjectReviewSnapshot snapshot)
    {
        var owner = snapshot.Shots.Takes.ToDictionary(t => t.Id, t => t.ShotId);
        var result = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var row in snapshot.Rows)
        {
            var takes = row.Shot.ShotFrames.Select(f => f.TakeId);
            if (row.Shot.StartFrame is { } start) takes = takes.Append(start.TakeId);
            // A frame from the shot's own earlier take orders nothing.
            var sources = takes.Select(t => owner.TryGetValue(t, out var shot) ? shot : (Guid?)null).OfType<Guid>().Where(s => s != row.Id).Distinct().ToArray();
            if (sources.Length > 0) result[row.Id] = sources;
        }
        return result;
    }

    /// <summary>Sorts the given shots into waves: every source in an earlier wave than its dependants. Shots in a cycle are left out of the waves.</summary>
    public static ShotRenderOrder Build(IReadOnlyList<Guid> shots, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> sources)
    {
        var known = shots.ToHashSet();
        var pending = shots.ToDictionary(s => s, s => sources.GetValueOrDefault(s)?.Where(known.Contains).ToHashSet() ?? []);
        var waves = new List<IReadOnlyList<Guid>>();
        while (pending.Count > 0)
        {
            // Shot-list order within a wave, so the plan reads like the film.
            var ready = shots.Where(s => pending.TryGetValue(s, out var waiting) && waiting.Count == 0).ToArray();
            if (ready.Length == 0) break;
            waves.Add(ready);
            foreach (var shot in ready) pending.Remove(shot);
            foreach (var waiting in pending.Values) waiting.ExceptWith(ready);
        }
        return new(waves, Cycles(shots.Where(pending.ContainsKey).ToArray(), pending), sources);
    }

    // What is left after the sort is cycles and the shots downstream of them; report only the cycles themselves.
    private static IReadOnlyList<IReadOnlyList<Guid>> Cycles(IReadOnlyList<Guid> shots, Dictionary<Guid, HashSet<Guid>> waiting)
    {
        var cycles = new List<IReadOnlyList<Guid>>(); var reported = new HashSet<Guid>();
        foreach (var start in shots)
        {
            if (reported.Contains(start)) continue;
            var path = new List<Guid>(); var current = start;
            while (!path.Contains(current))
            {
                path.Add(current);
                var next = shots.FirstOrDefault(s => waiting[current].Contains(s));
                if (next == Guid.Empty) { path.Clear(); break; }
                current = next;
            }
            if (path.Count == 0) continue;
            var cycle = path.Skip(path.IndexOf(current)).ToArray();
            if (cycle.Any(reported.Contains)) continue;
            reported.UnionWith(cycle); cycles.Add(shots.Where(cycle.Contains).ToArray());
        }
        return cycles;
    }
}
