using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using System.Text.Json;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData("conflict")] [InlineData("bad-video")] [InlineData("extraction")] [InlineData("cancelled")]
    public async Task ArchiveCleanupPreflightRetainsEveryFileOnFailure(string failure)
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var doc = await AddTake(f.Project.Id, f.Shots, shot); var take = doc.Takes.Single();
        var media = new CleanupMedia { InvalidVideo = failure == "bad-video", FailExtraction = failure == "extraction" };
        var store = new FileShotStore(f.Files, _clock, mediaTools: media);
        using var cancel = new CancellationTokenSource(); if (failure == "cancelled") cancel.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => store.RemoveFrameArchivesAsync(f.Project.Id, [take.Id], doc.Revision - (failure == "conflict" ? 1 : 0), failure == "cancelled" ? cancel.Token : _ct));
        var unchanged = await store.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(doc.Revision, unchanged.Revision); Assert.Null(unchanged.Takes.Single().FrameArchiveRemoval);
        var folder = FileShotStore.ArchiveDirectory(await f.Files.DirectoryAsync(f.Project.Id, _ct), take);
        Assert.All(take.Frames, frame => Assert.True(File.Exists(Path.Combine(folder, frame.FileName))));
    }

    [Fact]
    public async Task ArchiveCleanupPersistsIntentAndRetriesOnlyCapturedFilesAfterRestart()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var doc = await AddTake(f.Project.Id, f.Shots, shot); doc = await AddTake(f.Project.Id, f.Shots, shot, 2);
        var first = doc.Takes[0]; var other = doc.Takes[1];
        var folder = FileShotStore.ArchiveDirectory(await f.Files.DirectoryAsync(f.Project.Id, _ct), first);
        var package = Path.Combine(folder, "refinement.safetensors"); await File.WriteAllTextAsync(package, "preserved", _ct);
        var store = new FileShotStore(f.Files, _clock, mediaTools: new CleanupMedia());
        // A filesystem obstruction makes the second deletion fail on every OS.
        var obstructed = Path.Combine(folder, LosslessFrameArchive.FileName(1));
        File.Move(obstructed, obstructed + ".held"); Directory.CreateDirectory(obstructed);
        try
        {
            doc = await store.RemoveFrameArchivesAsync(f.Project.Id, [first.Id], doc.Revision, _ct);
            var pending = doc.Takes[0]; Assert.False(pending.HasLosslessFrames); Assert.Empty(pending.Frames);
            Assert.Null(pending.FrameArchiveRemoval!.CompletedUtc); Assert.NotNull(pending.FrameArchiveRemoval.Error);
            Assert.False(File.Exists(Path.Combine(folder, LosslessFrameArchive.FileName(0))));
        }
        finally { Directory.Delete(obstructed); File.Move(obstructed + ".held", obstructed); }
        store = new(f.Files, _clock, mediaTools: new CleanupMedia());
        doc = await store.ResumeFrameArchiveCleanupAsync(f.Project.Id, _ct);
        Assert.NotNull(doc.Takes[0].FrameArchiveRemoval!.CompletedUtc); Assert.Equal(3, doc.Takes[0].Bytes);
        Assert.True(doc.Takes[1].HasLosslessFrames); Assert.Equal(other.Frames.Count, doc.Takes[1].Frames.Count);
        Assert.Equal("preserved", await File.ReadAllTextAsync(package, _ct));
        Assert.True(File.Exists(Path.Combine(folder, "video.mp4"))); Assert.Empty(Directory.GetFiles(folder, "*.webp"));
        var revision = doc.Revision;
        Assert.Equal(revision, (await store.ResumeFrameArchiveCleanupAsync(f.Project.Id, _ct)).Revision);
        var cleanup = new FrameArchiveCleanupStore(f.Files, store); var rows = (await cleanup.ListAsync(_ct)).Items;
        Assert.Equal(other.Id, Assert.Single(rows).TakeId);
        Assert.Equal(other.Frames.DistinctBy(f => f.FileName).Sum(f => f.Bytes), rows.Single().Bytes);
        // Recovery and Trash readers retain the storage decision.
        doc = await store.DiscardAsync(f.Project.Id, first.Id, ShotTrashKind.Take, doc.Revision, _ct);
        doc = await store.RestoreAsync(f.Project.Id, [doc.Trash.Single().Id], doc.Revision, _ct);
        Assert.False(doc.Takes.Single(t => t.Id == first.Id).HasLosslessFrames);
    }

    [Fact]
    public async Task ArchiveCleanupCannotDeleteBeforePublishingIntent()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var doc = await AddTake(f.Project.Id, f.Shots, shot); var take = doc.Takes.Single();
        var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        FileStream? held = null;
        var media = new CleanupMedia { AfterExtraction = () => { held = TestProjectDatabase.HoldReadOnly(dir); } };
        var store = new FileShotStore(f.Files, _clock, mediaTools: media);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => store.RemoveFrameArchivesAsync(f.Project.Id, [take.Id], doc.Revision, _ct));
        }
        finally { held?.Dispose(); }
        Assert.Null((await store.LoadAsync(f.Project.Id, _ct)).Takes.Single().FrameArchiveRemoval);
        Assert.All(take.Frames, frame => Assert.True(File.Exists(Path.Combine(FileShotStore.ArchiveDirectory(dir, take), frame.FileName))));
    }

    [Fact]
    public void CleanupMetadataIsOptionalAndCannotNameOtherFiles()
    {
        var take = new ShotTake { Snapshot = PresetSnapshot("standard", true), Width = 32, Height = 32 };
        Assert.DoesNotContain("frameArchiveRemoval", JsonSerializer.Serialize(take, AtomicJsonFile.Options));
        foreach (var file in new[] { "video.mp4", "../archive-0000.webp", "refinement.safetensors", "archive-9999.webp" })
        {
            take.FrameArchiveRemoval = new(_clock.GetUtcNow(), [new(file, 1)]);
            Assert.Throws<WorkspaceStoreException>(() => FileShotStore.ValidateArchiveRemoval(take));
        }
    }

    private sealed class CleanupMedia : IProductionMediaTools
    {
        public bool InvalidVideo, FailExtraction;
        public Action? AfterExtraction;
        public Task<VideoFileInfo> VideoInfoAsync(string path, H3Settings settings, CancellationToken ct) => Task.FromResult(new VideoFileInfo(32, 32, InvalidVideo ? 1 : 39, 24, true));
        public Task<byte[]> ExtractFrameAsync(string path, int index, int width, int height, H3Settings settings, CancellationToken ct)
        { if (FailExtraction) throw new WorkspaceStoreException("Unavailable FFmpeg"); AfterExtraction?.Invoke(); return Task.FromResult(new byte[] { 1 }); }
        public Task<double> AudioDurationAsync(string path, H3Settings s, CancellationToken ct) => throw new NotSupportedException();
        public Task PrepareVoiceAsync(string source, string target, double start, double duration, H3Settings s, CancellationToken ct) => throw new NotSupportedException();
    }
}
