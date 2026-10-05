using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private sealed class LunaExportSettings : IAiSettingsStore
    {
        public Task<AiSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AiSettings());
        public Task<AiSettings> SaveAsync(AiSettings settings, string? replacementKey = null, bool removeKey = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> ReadOpenRouterKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
    private sealed class LunaExportClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class LunaExportMedia : IProductionMediaTools
    {
        public int Calls;
        public IReadOnlyList<CutExportSegment> Segments = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Hold;
        public Exception? Failure;
        public byte[][] CapturedInputs = [];
        public async Task ExportCutAsync(IReadOnlyList<CutExportSegment> segments, string target, H3Settings settings, CancellationToken ct)
        {
            Calls++; Segments = segments.ToArray(); Entered.TrySetResult();
            if (Hold is not null) await Hold.Task.WaitAsync(ct);
            CapturedInputs = await Task.WhenAll(segments.Select(s => File.ReadAllBytesAsync(s.Source, ct)));
            await File.WriteAllBytesAsync(target, [0, 1, 2, 3, 4, 5], ct);
            if (Failure is not null) throw Failure;
        }
        public Task<double> AudioDurationAsync(string path, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
        public Task PrepareVoiceAsync(string source, string target, double start, double duration, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
        public Task<VideoFileInfo> VideoInfoAsync(string path, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed record LunaExportFixture(Guid ProjectId, IShotStore Shots, ProjectFiles Files, FileCutStore Cuts,
        CutDocument Cut, ApplicationPaths Paths, CutExporter Exporter, LunaExportMedia Media, LunaExportClock Clock);
    private async Task<LunaExportFixture> CreateLunaExportFixture()
    {
        var f = Fixture(); var shot = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var document = await AddTake(f.Project.Id, f.Shots, shot);
        var cuts = new FileCutStore(f.Files, f.Shots, _clock);
        var a = CutClip.From(shot, document.Takes[0]); a.StartFrame = 1; a.EndFrameExclusive = 2;
        var b = CutClip.From(shot, document.Takes[0]); b.StartFrame = 2; b.EndFrameExclusive = 4;
        var cut = await cuts.SaveAsync(f.Project.Id, [a, b], 0, _ct);
        var paths = new ApplicationPaths(Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "export-test-data"));
        var media = new LunaExportMedia(); var clock = new LunaExportClock();
        var exporter = new CutExporter(f.Files, cuts, f.Shots, media, new LunaExportSettings(), paths, clock);
        return new(f.Project.Id, f.Shots, f.Files, cuts, cut, paths, exporter, media, clock);
    }
    private static string[] LunaExportFiles(ApplicationPaths paths) => Directory.Exists(paths.Temporary)
        ? Directory.GetFiles(paths.Temporary, "*.mp4", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task APartOfTheCutExportsOnlyItsClipsAndIsNamedForThem()
    {
        var f = await CreateLunaExportFixture(); await using var exporter = f.Exporter;
        var part = await exporter.ExportAsync(f.ProjectId, f.Cut.Revision, new CutExportRange(1, 1), _ct);
        var segment = Assert.Single(f.Media.Segments);
        Assert.Equal((2, 4), (segment.StartFrame, segment.EndFrameExclusive));
        Assert.Equal("cut-clips-2-2.mp4", part.FileName); Assert.Equal("clip 2", part.Range!.Label);
        // Every clip is the whole cut, named as such.
        var whole = await exporter.ExportAsync(f.ProjectId, f.Cut.Revision, new CutExportRange(0, 1), _ct);
        Assert.Equal(2, f.Media.Segments.Count); Assert.Null(whole.Range); Assert.Equal("cut.mp4", whole.FileName);
        foreach (var outside in new[] { new CutExportRange(1, 2), new CutExportRange(-1, 0), new CutExportRange(1, 0) })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => exporter.ExportAsync(f.ProjectId, f.Cut.Revision, outside, _ct));
    }

    [Fact]
    public async Task CutExportDownloadsHeadAndRangesReadOneStableArtifact()
    {
        var f = await CreateLunaExportFixture(); await using var exporter = f.Exporter;
        var result = await exporter.ExportAsync(f.ProjectId, f.Cut.Revision, _ct);
        Assert.Equal(f.Cut.Revision, result.Revision); Assert.Equal(f.ProjectId, result.ProjectId);
        Assert.Equal(f.Media.Segments[0].Source, f.Media.Segments[1].Source);
        Assert.Equal(1, f.Media.Segments[0].StartFrame); Assert.Equal(2, f.Media.Segments[0].EndFrameExclusive);
        Assert.Equal(2, f.Media.Segments[1].StartFrame); Assert.Equal(4, f.Media.Segments[1].EndFrameExclusive);
        Assert.Single(LunaExportFiles(f.Paths)); // No captured inputs remain after rendering.
        var resources = new MediaResources(null!, null!, f.Shots, null!, new LunaExportSettings(), cutExporter: exporter);
        await using (var head = await resources.GetAsync(result.Url, "HEAD", ct: _ct))
        {
            Assert.Equal(200, head.Status); Assert.Equal("6", head.Headers["Content-Length"]);
            Assert.Equal(0, head.Content.Length);
        }
        await using (var range = await resources.GetAsync(result.Url, headers: new Dictionary<string, string> { ["Range"] = "bytes=1-3" }, ct: _ct))
        {
            Assert.Equal(206, range.Status); Assert.Equal("bytes 1-3/6", range.Headers["Content-Range"]);
            using var copy = new MemoryStream(); await range.Content.CopyToAsync(copy, _ct);
            Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        }
        await f.Cuts.SaveAsync(f.ProjectId, [], f.Cut.Revision, _ct);
        await using (var download = await resources.GetAsync(result.Url, ct: _ct)) Assert.Equal(200, download.Status);
        await using (var legacy = await resources.GetAsync($"/downloads/projects/{f.ProjectId:D}/cut.mp4", ct: _ct)) Assert.Equal(404, legacy.Status);
        Assert.Null(await exporter.OpenAsync(Guid.NewGuid(), result.Id, _ct));
        Assert.Equal(1, f.Media.Calls);
    }

    [Fact]
    public async Task CutExportCapturedInputsSurviveSourcePurgeAndNewCutEdits()
    {
        var f = await CreateLunaExportFixture(); await using var exporter = f.Exporter;
        f.Media.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var render = exporter.ExportAsync(f.ProjectId, f.Cut.Revision, _ct);
        await f.Media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        var sourceBytes = await File.ReadAllBytesAsync(f.Media.Segments[0].Source, _ct);
        var document = await f.Shots.LoadAsync(f.ProjectId, _ct);
        var discarded = await f.Shots.DiscardAsync(f.ProjectId, f.Cut.Clips[0].TakeId, ShotTrashKind.Take, document.Revision, _ct);
        await f.Shots.PurgeAsync(f.ProjectId, [discarded.Trash[0].Id], discarded.Revision, _ct);
        await f.Cuts.SaveAsync(f.ProjectId, [], f.Cut.Revision, _ct);
        f.Media.Hold.SetResult();
        var result = await render;
        Assert.Equal(f.Cut.Revision, result.Revision);
        Assert.All(f.Media.CapturedInputs, bytes => Assert.Equal(sourceBytes, bytes));
    }

    [Fact]
    public async Task CutExportCancellationRemovesScratchAndReleasesTheRenderGate()
    {
        var f = await CreateLunaExportFixture(); await using var exporter = f.Exporter;
        f.Media.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var render = exporter.ExportAsync(f.ProjectId, f.Cut.Revision, cancel.Token);
        await f.Media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => render);
        Assert.Empty(LunaExportFiles(f.Paths));
        f.Media.Hold = null;
        var result = await exporter.ExportAsync(f.ProjectId, f.Cut.Revision, _ct);
        await using var media = await exporter.OpenAsync(f.ProjectId, result.Id, _ct);
        Assert.NotNull(media);
    }

    [Fact]
    public async Task CutExportFailurePreservesActionableErrorAndDeletesPartialOutput()
    {
        var f = await CreateLunaExportFixture(); await using var exporter = f.Exporter;
        f.Media.Failure = new WorkspaceStoreException("Encoder unavailable: diagnostic sentinel.");
        var error = await Assert.ThrowsAsync<WorkspaceStoreException>(() => exporter.ExportAsync(f.ProjectId, f.Cut.Revision, _ct));
        Assert.Contains("diagnostic sentinel", error.Message); Assert.Empty(LunaExportFiles(f.Paths));
    }

    [Fact]
    public async Task CutExportRejectsStaleRevisionEmptyCutsAndMissingMp4BeforeRendering()
    {
        var f = await CreateLunaExportFixture(); await using var exporter = f.Exporter;
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => exporter.ExportAsync(f.ProjectId, f.Cut.Revision - 1, _ct));
        var directory = await f.Files.DirectoryAsync(f.ProjectId, _ct);
        File.Delete(Path.Combine(directory, "shots", "takes", f.Cut.Clips[0].TakeId.ToString("D"), "video.mp4"));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => exporter.ExportAsync(f.ProjectId, f.Cut.Revision, _ct));
        var empty = await f.Cuts.SaveAsync(f.ProjectId, [], f.Cut.Revision, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => exporter.ExportAsync(f.ProjectId, empty.Revision, _ct));
        Assert.Equal(0, f.Media.Calls); Assert.Empty(LunaExportFiles(f.Paths));
    }

    [Fact]
    public async Task CutExportArtifactsExpireWithoutRerendering()
    {
        var f = await CreateLunaExportFixture(); await using var exporter = f.Exporter;
        var result = await exporter.ExportAsync(f.ProjectId, f.Cut.Revision, _ct);
        f.Clock.Now = result.ExpiresUtc;
        Assert.Null(await exporter.OpenAsync(f.ProjectId, result.Id, _ct));
        Assert.Equal(1, f.Media.Calls); Assert.Empty(LunaExportFiles(f.Paths));
    }

    [Fact]
    public async Task CutExporterDisposalCancelsActiveWorkAndCleansOwnedFiles()
    {
        var f = await CreateLunaExportFixture();
        f.Media.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var render = f.Exporter.ExportAsync(f.ProjectId, f.Cut.Revision, _ct);
        await f.Media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        await f.Exporter.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => render);
        Assert.Empty(LunaExportFiles(f.Paths));
    }
}
