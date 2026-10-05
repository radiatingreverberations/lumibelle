using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed record CutExportResult(Guid Id, Guid ProjectId, long Revision, DateTimeOffset ExpiresUtc)
{
    public string Url => $"/downloads/projects/{ProjectId:D}/cuts/{Id:D}.mp4";
    /// <summary>The exported clips when only part of the cut was exported.</summary>
    public CutExportRange? Range { get; init; }
    public string FileName => Range is { } range ? $"cut-clips-{range.First + 1}-{range.Last + 1}.mp4" : "cut.mp4";
}

/// <summary>A run of clips to export, by zero-based position in the cut, both ends included.</summary>
public sealed record CutExportRange(int First, int Last)
{
    public bool Covers(int count) => First == 0 && Last == count - 1;
    public string Label => First == Last ? $"clip {First + 1}" : $"clips {First + 1}–{Last + 1}";
}

public interface ICutExporter
{
    Task<CutExportResult> ExportAsync(Guid projectId, long expectedRevision, CancellationToken ct = default) => ExportAsync(projectId, expectedRevision, null, ct);
    /// <param name="range">Only these clips, such as a second part; null exports the whole cut.</param>
    Task<CutExportResult> ExportAsync(Guid projectId, long expectedRevision, CutExportRange? range, CancellationToken ct = default);
    Task<AssetMedia?> OpenAsync(Guid projectId, Guid exportId, CancellationToken ct = default);
}

// Rendering is an explicit operation. Resource requests only open completed artifacts.
// Artifacts are private to this process, bounded, and reusable for HEAD/ranges/retries.
public sealed class CutExporter(ProjectFiles files, ICutStore cuts, IShotStore shots, IProductionMediaTools media,
    IAiSettingsStore settings, ApplicationPaths paths, TimeProvider clock) : ICutExporter, IDisposable, IAsyncDisposable
{
    private sealed record Artifact(CutExportResult Result, string Directory, DateTimeOffset CreatedUtc);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private const int MaximumArtifacts = 8;
    private readonly string _root = Path.Combine(paths.Temporary, "cut-exports", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<Guid, Artifact> _artifacts = [];
    private readonly object _sync = new();
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public Task<CutExportResult> ExportAsync(Guid projectId, long expectedRevision, CancellationToken ct = default) => ExportAsync(projectId, expectedRevision, null, ct);
    public async Task<CutExportResult> ExportAsync(Guid projectId, long expectedRevision, CutExportRange? range, CancellationToken ct = default)
    {
        if (projectId == Guid.Empty || expectedRevision < 0)
            throw new WorkspaceStoreException("Choose a saved cut before exporting.");
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var token = lifetime.Token;
        // Do not launch an unbounded number of FFmpeg encoders from several tabs.
        await _renderGate.WaitAsync(token).ConfigureAwait(false);
        var id = Guid.NewGuid();
        var directory = Path.Combine(_root, id.ToString("N"));
        var published = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var configuration = (await settings.LoadAsync(token).ConfigureAwait(false)).H3;
            Directory.CreateDirectory(directory);
            var (segments, exported) = await CaptureAsync(projectId, expectedRevision, range, directory, token).ConfigureAwait(false);
            var output = Path.Combine(directory, "cut.mp4");
            await media.ExportCutAsync(segments, output, configuration, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new WorkspaceStoreException("FFmpeg did not produce an export. Check its configuration and try again.");
            DeleteDirectory(Path.Combine(directory, "inputs"));
            var now = clock.GetUtcNow();
            var result = new CutExportResult(id, projectId, expectedRevision, now + Retention) { Range = exported };
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                RemoveExpired();
                while (_artifacts.Count >= MaximumArtifacts)
                    Remove(_artifacts.Values.MinBy(a => a.CreatedUtc)!);
                _artifacts.Add(id, new(result, directory, now));
                published = true;
            }
            return result;
        }
        finally
        {
            if (!published) DeleteDirectory(directory);
            _renderGate.Release();
        }
    }

    private async Task<(IReadOnlyList<CutExportSegment> Segments, CutExportRange? Range)> CaptureAsync(Guid projectId, long expectedRevision,
        CutExportRange? range, string exportDirectory, CancellationToken ct)
    {
        var projectDirectory = await files.DirectoryAsync(projectId, ct).ConfigureAwait(false);
        // Capture the revision and source files together. Release the project lock
        // before rendering; subsequent edits/discard/purge cannot alter this export.
        using var gate = await ProjectFiles.LockAsync(projectDirectory, ct).ConfigureAwait(false);
        var cut = await cuts.LoadAsync(projectId, ct).ConfigureAwait(false);
        FileCutStore.Validate(cut, projectId);
        if (cut.Revision != expectedRevision) throw new WorkspaceConflictException();
        if (cut.Clips.Count == 0)
            throw new WorkspaceStoreException("Add at least one available take to the cut before exporting.");
        if (range is { } chosen && (chosen.First < 0 || chosen.Last >= cut.Clips.Count || chosen.First > chosen.Last))
            throw new WorkspaceStoreException("Choose clips that are in the cut, the first before the last.");
        // A range covering every clip is the whole cut.
        if (range?.Covers(cut.Clips.Count) == true) range = null;
        var clips = range is { } part ? cut.Clips.Skip(part.First).Take(part.Last - part.First + 1).ToList() : cut.Clips;
        var document = await shots.LoadAsync(projectId, ct).ConfigureAwait(false);
        var inputs = Path.Combine(exportDirectory, "inputs");
        Directory.CreateDirectory(inputs);
        var captured = new Dictionary<Guid, string>();
        var segments = new List<CutExportSegment>(clips.Count);
        foreach (var clip in clips)
        {
            ct.ThrowIfCancellationRequested();
            var take = document.Takes.SingleOrDefault(t => t.Id == clip.TakeId)
                ?? throw new WorkspaceStoreException("The cut contains an unavailable take. Restore it or remove its clip before exporting.");
            if (!document.Shots.Any(s => s.Id == take.ShotId) || take.FrameCount != clip.FrameCount || take.Fps != clip.Fps)
                throw new WorkspaceStoreException("A take no longer matches the cut. Refresh its available takes before exporting.");
            if (!captured.TryGetValue(take.Id, out var target))
            {
                // Store validation uses the take ID as its directory identity. Do not
                // trust an arbitrary path from a document at this boundary.
                if (!Guid.TryParse(take.Directory, out var directoryId) || directoryId != take.Id)
                    throw new WorkspaceStoreException("The selected take has an invalid media directory.");
                var source = Path.Combine(projectDirectory, "shots", "takes", take.Directory, "video.mp4");
                target = Path.Combine(inputs, $"{take.Id:N}.mp4");
                try
                {
                    await using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await from.CopyToAsync(to, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    throw new WorkspaceStoreException("A selected take is missing its MP4. Restore it or replace the clip before exporting.", e);
                }
                captured.Add(take.Id, target);
            }
            segments.Add(new(target, clip.StartFrame, clip.EndFrameExclusive, clip.Fps));
        }
        return (segments, range);
    }

    public Task<AssetMedia?> OpenAsync(Guid projectId, Guid exportId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RemoveExpired();
            if (!_artifacts.TryGetValue(exportId, out var artifact) || artifact.Result.ProjectId != projectId)
                return Task.FromResult<AssetMedia?>(null);
            try
            {
                var stream = new FileStream(Path.Combine(artifact.Directory, "cut.mp4"), FileMode.Open,
                    FileAccess.Read, FileShare.Read | FileShare.Delete, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                return Task.FromResult<AssetMedia?>(new(stream, "video/mp4", artifact.CreatedUtc));
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                Remove(artifact);
                return Task.FromResult<AssetMedia?>(null);
            }
        }
    }

    // Called under _sync. Open streams permit deletion and retain their handles.
    private void RemoveExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var artifact in _artifacts.Values.Where(a => a.Result.ExpiresUtc <= now).ToArray()) Remove(artifact);
    }
    private void Remove(Artifact artifact)
    {
        _artifacts.Remove(artifact.Result.Id);
        DeleteDirectory(artifact.Directory);
    }
    private static void DeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _renderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync) _artifacts.Clear();
            DeleteDirectory(_root);
        }
        finally
        {
            _renderGate.Release();
            _renderGate.Dispose();
            _shutdown.Dispose();
        }
    }
}
