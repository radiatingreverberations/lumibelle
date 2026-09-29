using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Projects;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Picture = SixLabors.ImageSharp.Image;

namespace Lumibelle.Tests;

public sealed class ProjectCompactionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumibelle-compact-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly Clock _clock = new();
    private readonly ApplicationPaths _paths;
    private readonly FileProjectStore _projects;
    private readonly ProjectFiles _files;
    private readonly FrameTools _tools = new();
    private readonly FileShotStore _shots;
    private readonly FileAssetStore _assets;
    private readonly FileReferenceVideoStore _reels;
    private readonly MediaTrashStore _trash;
    private readonly ProjectCompaction _compaction;

    public ProjectCompactionTests()
    {
        _paths = new(_directory); Directory.CreateDirectory(_paths.Projects);
        _projects = new(_paths, _clock, NullLogger<FileProjectStore>.Instance); _files = new(_paths, _projects);
        _shots = new(_files, _clock, mediaTools: _tools); _assets = new(_files, _clock);
        _reels = new(_files, _shots, _tools); _trash = new(_files, _assets, _assets, _assets, _shots, _clock);
        _compaction = new(_files, new NoFolders(), _shots, _reels, _trash, new FakeAiSettingsStore());
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public async Task CompactingRemovesReelArchivesButKeepsEveryChosenKeyframe()
    {
        var project = await _projects.CreateAsync(new("Compact"), _ct); var root = await _files.DirectoryAsync(project.Id, _ct);
        var media = await ReelWithArchive(project.Id);
        var catalog = await _reels.FrameCatalogAsync(project.Id, media, new(), _ct);
        ReelFrameIdentity Pick(int i) => new(media.Id, catalog.Source, i, catalog.Timestamps[i]);
        await _reels.PrepareFramesAsync(project.Id, [Pick(5)], new(), _ct);
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        var library = await _assets.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, _ct);
        library = await _assets.SaveReelAsync(project.Id, new AssetReferenceReel { AssetId = owner.Id, Media = media }, library.Revision, _ct);
        await _assets.SaveKeyframesAsync(project.Id, library.Reels.Single(),
            new() { Frames = [new() { Frame = Pick(5), Notes = "Doorway" }, new() { Frame = Pick(30), Notes = "Window" }] }, library.Revision, _ct);
        // A shot keeps its own copy of a keyframe the reel no longer lists.
        var binding = new ShotVideoBinding { Media = media, Visuals = ReelVisuals.Keyframes, Keyframes = new() { Frames = [new() { Frame = Pick(40), Notes = "Earlier pick" }] } };
        await AtomicJsonFile.WriteAsync(Path.Combine(root, "shots.json"), new ShotDocument { ProjectId = project.Id, Shots = [new() { Title = "Shot", Videos = [binding] }] }, _ct);
        var index = await File.ReadAllBytesAsync(Path.Combine(root, "reference-videos", media.Id.ToString("D"), "frame-archive.json"), _ct);

        var plan = await _compaction.InspectAsync(project.Id, _ct);
        Assert.Equal([media.Id], plan.Reels); Assert.True(plan.ReelBytes > 0); Assert.Empty(plan.Trash); Assert.Null(plan.Manifest);
        var result = await _compaction.CompactAsync(plan, new HashSet<CompactionPart> { CompactionPart.ReelArchives }, ct: _ct);
        Assert.Empty(result.Issues); Assert.Equal(plan.ReelBytes, result.ReclaimedBytes);

        var folder = Path.Combine(root, "reference-videos", media.Id.ToString("D"));
        Assert.False(Directory.Exists(Path.Combine(folder, "lossless")));
        Assert.Equal(index, await File.ReadAllBytesAsync(Path.Combine(folder, "frame-archive.json"), _ct));
        var removal = (await AtomicJsonFile.ReadAsync<ReelArchiveRemoval>(Path.Combine(folder, "lossless-removal.json"), _ct))!;
        Assert.NotNull(removal.CompletedUtc);
        Assert.Equal(new[] { 5, 30, 40 }.Select(i => FileReferenceVideoStore.FrameFileName(Pick(i))), removal.Keyframes);
        foreach (var i in new[] { 30, 40 })
        {
            await using var frame = await _reels.OpenFrameAsync(project.Id, Pick(i), new(), _ct);
            using var image = await Picture.LoadAsync<Rgb24>(frame.Content, _ct); Assert.Equal(new Rgb24((byte)i, 0, 200), image[0, 0]);
        }
        var video = await _reels.FrameCatalogAsync(project.Id, media, new(), _ct);
        Assert.False(video.Lossless);
        await using (var pick = await _reels.OpenFrameAsync(project.Id, new(media.Id, video.Source, 12, video.Timestamps[12]), new(), _ct)) Assert.True(pick.Content.Length > 0);
        Assert.Empty((await _compaction.InspectAsync(project.Id, _ct)).Reels);
    }

    [Fact]
    public async Task PreparingSeveralLosslessFramesDecodesTheSamePicturesAsOneAtATime()
    {
        var project = await _projects.CreateAsync(new("Compact"), _ct); var root = await _files.DirectoryAsync(project.Id, _ct);
        var media = await ReelWithArchive(project.Id);
        var catalog = await _reels.FrameCatalogAsync(project.Id, media, new(), _ct);
        var frames = new[] { 3, 7, 7, 30, 47 }.Select(i => new ReelFrameIdentity(media.Id, catalog.Source, i, catalog.Timestamps[i])).ToArray();
        await _reels.PrepareFramesAsync(project.Id, frames, new(), _ct);
        var folder = Path.Combine(root, "reference-videos", media.Id.ToString("D"));
        foreach (var frame in frames.Distinct())
        {
            await using var single = await LosslessFrameArchive.OpenFrameAsync(Path.Combine(folder, "lossless", LosslessFrameArchive.FileName(frame.Index / 24)), frame.Index % 24, media.Width, media.Height, _ct);
            Assert.Equal(single.ToArray(), await File.ReadAllBytesAsync(Path.Combine(folder, FileReferenceVideoStore.FrameFileName(frame)), _ct));
        }
    }

    [Fact]
    public async Task ReelArchiveRemovalKeepsTheArchiveWhenTheVideoCannotBeDecoded()
    {
        var project = await _projects.CreateAsync(new("Compact"), _ct); var root = await _files.DirectoryAsync(project.Id, _ct);
        var media = await ReelWithArchive(project.Id);
        _tools.Decodes = false;
        var plan = await _compaction.InspectAsync(project.Id, _ct);
        var result = await _compaction.CompactAsync(plan, new HashSet<CompactionPart> { CompactionPart.ReelArchives }, ct: _ct);
        Assert.Contains(result.Issues, i => i.Contains("FFmpeg"));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "reference-videos", media.Id.ToString("D"), "lossless")).Length);
        Assert.False(File.Exists(Path.Combine(root, "reference-videos", media.Id.ToString("D"), "lossless-removal.json")));
    }

    [Fact]
    public async Task RemovedReelsAreInTrashAndDeletingKeepsMediaStillInUse()
    {
        var project = await _projects.CreateAsync(new("Compact"), _ct); var root = await _files.DirectoryAsync(project.Id, _ct);
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        var library = await _assets.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, _ct);
        var unused = new AssetReferenceReel { AssetId = owner.Id, Media = await Import(project.Id), Name = "Unused" };
        var attached = new AssetReferenceReel { AssetId = owner.Id, Media = await Import(project.Id), Name = "Attached" };
        var restored = new AssetReferenceReel { AssetId = owner.Id, Media = await Import(project.Id), Name = "Restored" };
        foreach (var reel in new[] { unused, attached, restored }) library = await _assets.SaveReelAsync(project.Id, reel, library.Revision, _ct);
        foreach (var reel in new[] { unused, attached, restored }) library = await _assets.TrashReelAsync(project.Id, reel.Id, library.Revision, _ct);
        Assert.All(library.ReelTrash, t => Assert.Equal(t.DeletedUtc.AddDays(30), t.ExpiresUtc));
        await AtomicJsonFile.WriteAsync(Path.Combine(root, "shots.json"), new ShotDocument { ProjectId = project.Id, Shots = [new() { Title = "Shot", Videos = [new() { Media = attached.Media }] }] }, _ct);

        var rows = (await _trash.ListAsync(_ct)).Items.Where(r => r.Kind == MediaTrashKind.Reel).ToDictionary(r => r.Id);
        Assert.Equal(3, rows.Count);
        Assert.True(rows[unused.Id].Bytes > 0); Assert.Equal(0, rows[attached.Id].Bytes);
        Assert.Equal($"/media/projects/{project.Id}/reference-videos/{unused.Media.Id}/thumbnail", rows[unused.Id].ThumbnailUrl);
        var restore = await _trash.ChangeAsync([rows[restored.Id]], false, _ct);
        Assert.Equal([restored.Id], restore.Succeeded);
        var purge = await _trash.ChangeAsync((await _trash.ListAsync(_ct)).Items.Where(r => r.Kind == MediaTrashKind.Reel).ToArray(), true, _ct);
        Assert.Equal(2, purge.Succeeded.Count); Assert.Empty(purge.Issues);
        library = await _assets.LoadAsync(project.Id, _ct);
        Assert.Empty(library.ReelTrash); Assert.Equal(restored.Id, Assert.Single(library.Reels).Id);
        Assert.False(Directory.Exists(Path.Combine(root, "reference-videos", unused.Media.Id.ToString("D"))));
        Assert.True(File.Exists(Path.Combine(root, "reference-videos", attached.Media.Id.ToString("D"), "video.mp4")));
    }

    [Fact]
    public async Task ReelsRemovedBeforeTrashExpiryStayUntilDeleted()
    {
        var project = await _projects.CreateAsync(new("Compact"), _ct);
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        var library = await _assets.SaveAsync(new() { ProjectId = project.Id, Assets = [owner] }, 0, _ct);
        var legacy = new AssetReferenceReel { AssetId = owner.Id, Media = await Import(project.Id), Name = "Legacy" };
        var recent = new AssetReferenceReel { AssetId = owner.Id, Media = await Import(project.Id), Name = "Recent" };
        library = await _assets.SaveReelAsync(project.Id, legacy, library.Revision, _ct);
        library = await _assets.SaveReelAsync(project.Id, recent, library.Revision, _ct);
        library = await _assets.TrashReelAsync(project.Id, legacy.Id, library.Revision, _ct);
        library = await _assets.TrashReelAsync(project.Id, recent.Id, library.Revision, _ct);
        // Rewrite the first entry as it was saved before reels had a Trash period.
        var root = await _files.DirectoryAsync(project.Id, _ct);
        await AtomicJsonFile.WriteAsync(Path.Combine(root, "assets.json"), library with {
            ReelTrash = library.ReelTrash.Select(t => t.Reel.Id == legacy.Id ? new TrashedReferenceReel(t.Reel, t.Owner, t.DeletedUtc) : t).ToList() }, _ct);
        var row = (await _trash.ListAsync(_ct)).Items.Single(r => r.Id == legacy.Id);
        Assert.Equal("Kept until deleted", row.Remaining(_clock.GetUtcNow()));
        _clock.Now = _clock.Now.AddDays(31);
        Assert.Empty(await _trash.CleanupAsync(_ct));
        Assert.Equal(legacy.Id, Assert.Single((await _assets.LoadAsync(project.Id, _ct)).ReelTrash).Reel.Id);
    }

    private async Task<ReferenceVideoMedia> Import(Guid project)
    {
        await using var input = new MemoryStream([1, 2, 3, 4]);
        return await _reels.ImportAsync(project, input, "clip.mp4", new(), _ct);
    }
    private async Task<ReferenceVideoMedia> ReelWithArchive(Guid project)
    {
        var media = await Import(project);
        var source = Path.Combine(_directory, "archive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(source);
        var frames = await MockFrameArchive.WriteAsync(source, media.Frames, media.Width, media.Height, _ct);
        var shot = new Shot { Title = "Reel", Description = "A room", Duration = 2, SaveLosslessFrames = true };
        var output = new ShotTake { Width = media.Width, Height = media.Height, Frames = frames,
            Snapshot = new(project, 1, shot, "", "", "http://localhost:8188", new(), media.Width, media.Height, media.Frames) { OutputPolicy = new(true) } };
        await _reels.PublishArchiveAsync(project, media, output, source, _ct);
        return media;
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NoFolders : IProjectFolders
    {
        public Task<IReadOnlyList<ProjectLocation>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProjectLocation>>([]);
        public Task<ProjectLocation?> LocationAsync(Guid project, CancellationToken ct = default) => Task.FromResult<ProjectLocation?>(null);
        public Task<ProjectInfo> OpenAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectLocation> MoveOutAsync(Guid project, string parent, IProgress<ProjectPackageProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid project, CancellationToken ct = default) => throw new NotSupportedException();
    }
    // 32x32, 48 frames at 24 fps. Decodes = false simulates missing or broken FFmpeg.
    internal sealed class FrameTools : IProductionMediaTools
    {
        public bool Decodes = true;
        public Task<VideoFileInfo> VideoInfoAsync(string path, H3Settings settings, CancellationToken ct) => Task.FromResult(new VideoFileInfo(32, 32, 48, 24, true));
        public Task<double> AudioDurationAsync(string path, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
        public Task PrepareVoiceAsync(string source, string target, double start, double duration, H3Settings settings, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<double>> ReelFrameTimesAsync(string source, H3Settings settings, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<double>>(Enumerable.Range(0, 48).Select(i => i / 24d).ToArray());
        public async Task ExtractReelFramesAsync(string source, IReadOnlyList<int> indices, string directory, int maximumEdge, H3Settings settings, CancellationToken ct)
        {
            if (!Decodes) return;
            for (var i = 0; i < indices.Count; i++) { using var image = new Image<Rgb24>(32, 32); await image.SaveAsPngAsync(Path.Combine(directory, $"{i:D6}.png"), ct); }
        }
    }
}
