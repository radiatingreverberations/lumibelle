using lumibelle.Models;

namespace lumibelle.Services.Shots;

public static class ShotPlanningOrder
{
    // Insert new scene coverage without sorting or disturbing authored existing coverage.
    public static List<Shot> Insert(IReadOnlyList<Shot> existing, IReadOnlyList<Shot> proposed, IEnumerable<Guid> sceneIds)
    {
        var order = sceneIds.Distinct().Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        int Position(Guid? scene) => scene is { } id && order.TryGetValue(id, out var index) ? index : int.MaxValue;
        var result = existing.ToList();
        foreach (var group in proposed.GroupBy(s => s.SceneId).OrderBy(g => Position(g.Key)))
        {
            var position = Position(group.Key);
            var insertion = position == int.MaxValue ? result.Count : result.FindLastIndex(s => s.SceneId == group.Key) + 1;
            if (insertion == 0)
            {
                insertion = result.FindIndex(s => Position(s.SceneId) != int.MaxValue && Position(s.SceneId) > position);
                if (insertion < 0) insertion = result.Count;
            }
            result.InsertRange(insertion, group);
        }
        return result;
    }
}
