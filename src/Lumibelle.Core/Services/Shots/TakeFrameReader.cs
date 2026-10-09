using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

// Shared by the player and asset derivation. Only in-flight extractions are retained.
public sealed class TakeFrameReader(IProductionMediaTools media)
{
    public static TakeFrameReader Shared { get; } = new(new ProductionMediaTools());
    private readonly object gate = new();
    private readonly SemaphoreSlim workers = new(2);
    private readonly Dictionary<string, Extraction> active = new(StringComparer.OrdinalIgnoreCase);
    private sealed class Extraction
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<byte[]> Task { get; set; } = null!;
        public int Readers { get; set; }
    }
    public async Task<Stream> OpenAsync(string directory, ShotTake take, int index, CancellationToken ct)
    {
        if (index < 0 || index >= take.FrameCount) throw new WorkspaceStoreException("Choose an available frame.");
        if (take.Composition is not null && take.FrameArchiveRemoval is null) {
            var (segment, localEnd) = H3Motion.Tail(take, index + 1);
            return await OpenAsync(Path.Combine(directory, "segments", segment.Key.ToString("D")), segment.Source, localEnd - 1, ct);
        }
        if (take.HasLosslessFrames)
        {
            var frame = take.Frames.ElementAtOrDefault(index) ?? throw new WorkspaceStoreException("The lossless frame archive is missing.");
            return await LosslessFrameArchive.OpenFrameAsync(Path.Combine(directory, frame.FileName), frame.ArchiveFrameIndex, take.Width, take.Height, ct);
        }
        var path = Path.GetFullPath(Path.Combine(directory, "video.mp4"));
        var key = path + "|" + index;
        Extraction entry;
        lock (gate)
        {
            if (!active.TryGetValue(key, out entry!))
            {
                entry = new(); active[key] = entry;
                entry.Task = ExtractAsync(path, take, index, entry.Cancellation.Token);
            }
            entry.Readers++;
        }
        try { return new MemoryStream(await entry.Task.WaitAsync(ct), writable: false); }
        finally
        {
            lock (gate)
            {
                if (--entry.Readers == 0)
                {
                    active.Remove(key); entry.Cancellation.Cancel();
                    _ = entry.Task.ContinueWith(t => { _ = t.Exception; entry.Cancellation.Dispose(); }, TaskScheduler.Default);
                }
            }
        }
    }
    private async Task<byte[]> ExtractAsync(string path, ShotTake take, int index, CancellationToken ct)
    {
        await workers.WaitAsync(ct);
        try { return await media.ExtractFrameAsync(path, index, take.Width, take.Height, take.Snapshot.Settings, ct); }
        finally { workers.Release(); }
    }
}
