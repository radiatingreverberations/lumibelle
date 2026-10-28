namespace lumibelle.Services.Assets;

internal static class TrashRestoreOrder
{
    public static bool Valid(IReadOnlyList<Guid>? order, Guid owner) => order is null ||
        order.Contains(owner) && !order.Contains(Guid.Empty) && order.Distinct().Count() == order.Count;

    // Anchor each restored item to the nearest surviving neighbour. This also
    // works when a selection is restored in separate requests or in reverse order,
    // without sorting or undoing later author moves of the surviving items.
    public static void Insert<T>(List<T> items, T restored, IReadOnlyList<Guid> order, Func<T, Guid> identity)
    {
        var position = -1;
        for (var i = 0; i < order.Count; i++) if (order[i] == identity(restored)) { position = i; break; }
        if (position >= 0) {
            for (var i = position + 1; i < order.Count; i++) {
                var next = items.FindIndex(item => identity(item) == order[i]);
                if (next >= 0) { items.Insert(next, restored); return; }
            }
            for (var i = position - 1; i >= 0; i--) {
                var previous = items.FindIndex(item => identity(item) == order[i]);
                if (previous >= 0) { items.Insert(previous + 1, restored); return; }
            }
        }
        items.Add(restored);
    }
}
