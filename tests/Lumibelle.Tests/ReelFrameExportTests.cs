using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private sealed record ReelExport(ProjectInfo Project, ProjectFiles Files, FileReferenceVideoStore MediaStore, FileAssetStore Assets, AssetLibrary Library,
        ReferenceAsset Owner, CharacterLook Look, AssetReferenceReel Reel, ReferenceVideoMedia Media, ReelFrameIdentity Frame);

    // A character with one reel, optionally with a lossless archive, and frame 37 of it.
    private async Task<ReelExport> ReelExportFixture(bool lossless)
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
        return new(f.Project, f.Files, mediaStore, assets, library, owner, look, reel, media, new(media.Id, catalog.Source, 37, catalog.Timestamps[37]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReelFrameExportIsExactIndependentAndRetryable(bool lossless)
    {
        var (project, files, mediaStore, assets, library, owner, look, reel, media, frame) = await ReelExportFixture(lossless);
        var request = new DerivedImageRequest(Guid.NewGuid(), new(owner.Id, look.Id), "Chosen angle", "Keep this arrangement") { ReelFrame = new(reel.Id, frame) };
        foreach (var invalid in new[] {
            request with { ReelFrame = new(reel.Id, frame with { Index = 999 }) },
            request with { ReelFrame = new(reel.Id, frame with { Seconds = 0 }) },
            request with { ReelFrame = new(reel.Id, frame with { Source = new('F', 64) }) },
            request with { ReelFrame = new(reel.Id, frame with { MediaId = Guid.NewGuid() }) },
            request with { TakeId = Guid.NewGuid(), FrameIndex = 0 },
            request with { Destination = new(owner.Id, Guid.NewGuid()) }
        }) await Assert.ThrowsAsync<WorkspaceStoreException>(() => assets.SaveDerivedImageAsync(project.Id, invalid, library.Revision, _ct));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => assets.SaveDerivedImageAsync(project.Id, request, 0, _ct));
        if (OperatingSystem.IsWindows())
        {
            await using (var locked = TestProjectDatabase.HoldReadOnly(Path.Combine(await files.DirectoryAsync(project.Id, _ct))))
                await Assert.ThrowsAsync<WorkspaceStoreException>(() => assets.SaveDerivedImageAsync(project.Id, request, library.Revision, _ct));
            Assert.Empty((await assets.LoadAsync(project.Id, _ct)).Assets[0].Images);
        }
        var saved = await assets.SaveDerivedImageAsync(project.Id, request, library.Revision, _ct);
        var image = Assert.Single(saved.Library.Assets[0].Images);
        Assert.Equal(look.Id, image.LookId); Assert.False(image.IsReference); Assert.False(image.IsCover);
        Assert.Equal(AssetImageOrigin.VideoFrame, image.Origin); Assert.Equal(request.Notes, image.PreservationGuidance);
        Assert.Equal(new ReelImageSource(project.Id, reel.Id, reel.Name, frame, lossless, 32, 32), image.Source!.ReelFrame);
        await using var original = await mediaStore.OpenFrameAsync(project.Id, frame, new(), _ct);
        using var expected = await Image.LoadAsync<Rgb24>(original.Content, _ct);
        await assets.TrashReelAsync(project.Id, reel.Id, saved.Library.Revision, _ct);
        var mediaDirectory = Path.Combine(await files.DirectoryAsync(project.Id, _ct), "reference-videos", media.Id.ToString("D"));
        File.Delete(Path.Combine(mediaDirectory, "video.mp4"));
        await using var copy = await assets.OpenImageAsync(project.Id, owner.Id, image.Id, _ct);
        using var pixels = await Image.LoadAsync<Rgb24>(copy!.Content, _ct); AssertPixels(expected, pixels);
        Assert.Null(pixels.Metadata.ExifProfile); Assert.Empty(pixels.Metadata.GetPngMetadata().TextData);
        var retry = await assets.SaveDerivedImageAsync(project.Id, request, library.Revision, _ct);
        Assert.Equal(image.Id, retry.ImageId); Assert.Single(retry.Library.Assets[0].Images);
        Assert.Null(retry.Library.ReelTrash[0].Reel.Keyframes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CroppedReelFrameExportRecordsTheFullFrameAndCropAndSavesOnlyTheCroppedPixels(bool lossless)
    {
        var x = await ReelExportFixture(lossless);
        var crop = new ImageCropRegion { X = .25, Y = 0, Width = .5, Height = 1 };
        var request = new DerivedImageRequest(Guid.NewGuid(), new(x.Owner.Id, x.Look.Id), "Riley only", "Her face and jacket", Crop: crop) { ReelFrame = new(x.Reel.Id, x.Frame) };
        foreach (var invalid in new[] { crop with { Width = 0 }, crop with { X = .75 }, crop with { Height = double.NaN } })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => x.Assets.SaveDerivedImageAsync(x.Project.Id, request with { Crop = invalid }, x.Library.Revision, _ct));
        var saved = await x.Assets.SaveDerivedImageAsync(x.Project.Id, request, x.Library.Revision, _ct);
        var image = Assert.Single(saved.Library.Assets[0].Images);
        Assert.Equal(AssetImageOrigin.VideoFrame, image.Origin); Assert.Equal((16, 32), (image.Width, image.Height));
        // The provenance keeps the full frame's size, so the same crop can be taken again from it.
        Assert.Equal(new ReelImageSource(x.Project.Id, x.Reel.Id, x.Reel.Name, x.Frame, lossless, 32, 32) { Crop = crop }, image.Source!.ReelFrame);
        await using (var original = await x.MediaStore.OpenFrameAsync(x.Project.Id, x.Frame, new(), _ct))
        await using (var copy = await x.Assets.OpenImageAsync(x.Project.Id, x.Owner.Id, image.Id, _ct))
        {
            using var expected = await Image.LoadAsync<Rgb24>(original.Content, _ct);
            expected.Mutate(c => c.Crop(new Rectangle(8, 0, 16, 32)));
            using var pixels = await Image.LoadAsync<Rgb24>(copy!.Content, _ct); AssertPixels(expected, pixels);
        }
        var retry = await x.Assets.SaveDerivedImageAsync(x.Project.Id, request, x.Library.Revision, _ct);
        Assert.Equal(image.Id, retry.ImageId); Assert.Single(retry.Library.Assets[0].Images);
    }
}
