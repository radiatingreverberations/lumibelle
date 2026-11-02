using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReelFrameExportIsExactIndependentAndRetryable(bool lossless)
    {
        var f = Fixture(); var tools = new FrameWorkerTools(); tools.Release.SetResult();
        var mediaStore = new FileReferenceVideoStore(f.Files, f.Shots, tools);
        var assets = new FileAssetStore(f.Files, _clock, referenceVideos: mediaStore);
        using var input = new MemoryStream([1, 2, 3]);
        var media = await mediaStore.ImportAsync(f.Project.Id, input, "reel.mp4", new(), _ct);
        if (lossless)
        {
            var directory = Path.Combine(_root, "reel-frame-export-source"); Directory.CreateDirectory(directory);
            var frames = await MockFrameArchive.WriteAsync(directory, media.Frames, media.Width, media.Height, _ct);
            var take = new ShotTake { Snapshot = ReferenceSnapshot(Ready() with { Duration = 3 }) with { FrameCount = media.Frames, OutputPolicy = new(true) },
                Width = media.Width, Height = media.Height, Frames = frames };
            await mediaStore.PublishArchiveAsync(f.Project.Id, media, take, directory, _ct);
        }
        var look = new CharacterLook { Name = "Evening" };
        var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character, Looks = [look] };
        var library = await assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner] }, 0, _ct);
        var reel = new AssetReferenceReel { AssetId = owner.Id, LookId = look.Id, Name = "Turn", Media = media };
        library = await assets.SaveReelAsync(f.Project.Id, reel, library.Revision, _ct);
        var catalog = await mediaStore.FrameCatalogAsync(f.Project.Id, media, new(), _ct);
        var frame = new ReelFrameIdentity(media.Id, catalog.Source, 37, catalog.Timestamps[37]);
        var request = new DerivedImageRequest(Guid.NewGuid(), new(owner.Id, look.Id), "Chosen angle", "Keep this arrangement") { ReelFrame = new(reel.Id, frame) };
        foreach (var invalid in new[] {
            request with { ReelFrame = new(reel.Id, frame with { Index = 999 }) },
            request with { ReelFrame = new(reel.Id, frame with { Seconds = 0 }) },
            request with { ReelFrame = new(reel.Id, frame with { Source = new('F', 64) }) },
            request with { ReelFrame = new(reel.Id, frame with { MediaId = Guid.NewGuid() }) },
            request with { TakeId = Guid.NewGuid(), FrameIndex = 0 },
            request with { Destination = new(owner.Id, Guid.NewGuid()) }
        }) await Assert.ThrowsAsync<WorkspaceStoreException>(() => assets.SaveDerivedImageAsync(f.Project.Id, invalid, library.Revision, _ct));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => assets.SaveDerivedImageAsync(f.Project.Id, request, 0, _ct));
        if (OperatingSystem.IsWindows())
        {
            await using (var locked = TestProjectDatabase.HoldReadOnly(Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct))))
                await Assert.ThrowsAsync<WorkspaceStoreException>(() => assets.SaveDerivedImageAsync(f.Project.Id, request, library.Revision, _ct));
            Assert.Empty((await assets.LoadAsync(f.Project.Id, _ct)).Assets[0].Images);
        }
        var saved = await assets.SaveDerivedImageAsync(f.Project.Id, request, library.Revision, _ct);
        var image = Assert.Single(saved.Library.Assets[0].Images);
        Assert.Equal(look.Id, image.LookId); Assert.False(image.IsReference); Assert.False(image.IsCover);
        Assert.Equal(AssetImageOrigin.VideoFrame, image.Origin); Assert.Equal(request.Notes, image.PreservationGuidance);
        Assert.Equal(new ReelImageSource(f.Project.Id, reel.Id, reel.Name, frame, lossless, 32, 32), image.Source!.ReelFrame);
        await using var original = await mediaStore.OpenFrameAsync(f.Project.Id, frame, new(), _ct);
        using var expected = await Image.LoadAsync<Rgb24>(original.Content, _ct);
        await assets.TrashReelAsync(f.Project.Id, reel.Id, saved.Library.Revision, _ct);
        var mediaDirectory = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "reference-videos", media.Id.ToString("D"));
        File.Delete(Path.Combine(mediaDirectory, "video.mp4"));
        await using var copy = await assets.OpenImageAsync(f.Project.Id, owner.Id, image.Id, _ct);
        using var pixels = await Image.LoadAsync<Rgb24>(copy!.Content, _ct); AssertPixels(expected, pixels);
        Assert.Null(pixels.Metadata.ExifProfile); Assert.Empty(pixels.Metadata.GetPngMetadata().TextData);
        var retry = await assets.SaveDerivedImageAsync(f.Project.Id, request, library.Revision, _ct);
        Assert.Equal(image.Id, retry.ImageId); Assert.Single(retry.Library.Assets[0].Images);
        Assert.Null(retry.Library.ReelTrash[0].Reel.Keyframes);
    }
}
