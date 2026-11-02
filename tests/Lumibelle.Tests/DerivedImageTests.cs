using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static DerivedImageRequest FrameCopy(ShotTake take, int index = 5) => new(Guid.NewGuid(),
        new(Guid.NewGuid(), NewAssetName: "Arrival state"), "Mouse after arrival", "Keep the hand position", take.Id, index);

    [Fact]
    public async Task SavedVideoFrameCanMoveToACharacterWithoutChangingItsFrameProvenance()
    {
        var f = Fixture(); var shot = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, f.Shots, shot); var take = d.Takes[0];
        var request = FrameCopy(take);
        var saved = await f.Assets.SaveDerivedImageAsync(f.Project.Id, request, 0, _ct);
        var original = saved.Library.Assets[0].Images[0];
        var target = new ImageDestination(Guid.NewGuid(), NewAssetName: "Juniper", NewAssetCategory: AssetCategory.Character);
        var moved = await f.Assets.MoveImagesAsync(f.Project.Id, saved.AssetId, [saved.ImageId], target, saved.Library.Revision, _ct);
        var image = moved.Assets.Single(a => a.Id == target.AssetId).Images.Single();
        Assert.Equal(original.Source, image.Source); Assert.Equal(original.Id, image.Id);
        Assert.Equal(AssetImageOrigin.VideoFrame, image.Origin);
        await using var pixels = await f.Assets.OpenImageAsync(f.Project.Id, target.AssetId, image.Id, _ct);
        await using var frame = await f.Shots.OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, 5, ct: _ct);
        using var a = await Image.LoadAsync<Rgb24>(pixels!.Content, _ct);
        using var b = await Image.LoadAsync<Rgb24>(frame!.Content, _ct); AssertPixels(a, b);
        var retry = await f.Assets.SaveDerivedImageAsync(f.Project.Id, request, 0, _ct);
        Assert.Equal(target.AssetId, retry.AssetId); Assert.True(retry.Available);
    }

    [Fact]
    public async Task FrameCopyPublishesPixelsAndAssetTogetherAndSurvivesSourcePurge()
    {
        var f = Fixture(); var shot = Ready();
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, f.Shots, shot); var take = d.Takes[0];
        var request = FrameCopy(take);
        var saved = await f.Assets.SaveDerivedImageAsync(f.Project.Id, request, 0, _ct);
        var asset = Assert.Single(saved.Library.Assets); var image = Assert.Single(asset.Images);
        Assert.Equal(1, saved.Library.Revision); Assert.Equal(AssetCategory.Reference, asset.Category);
        Assert.False(image.IsReference); Assert.False(image.IsCover); Assert.Null(image.LookId);
        Assert.Equal(request.Name, image.Name); Assert.Equal(request.Notes, image.PreservationGuidance);
        Assert.Equal(new VideoFrameSource(f.Project.Id, shot.Id, take.Id, 5, 5 / 24d, 24, 32, 32) { Lossless = true }, image.Source!.Frame);
        await using (var original = await f.Shots.OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, 5, ct: _ct))
        await using (var copy = await f.Assets.OpenImageAsync(f.Project.Id, asset.Id, image.Id, _ct))
        {
            using var a = await Image.LoadAsync<Rgb24>(original!.Content, _ct);
            using var b = await Image.LoadAsync<Rgb24>(copy!.Content, _ct);
            AssertPixels(a, b); Assert.Null(b.Metadata.ExifProfile); Assert.Empty(b.Metadata.GetPngMetadata().TextData);
        }
        d = await f.Shots.DiscardAsync(f.Project.Id, take.Id, ShotTrashKind.Take, d.Revision, _ct);
        await f.Shots.PurgeAsync(f.Project.Id, [d.Trash[0].Id], d.Revision, _ct);
        shot.Images = [new() { AssetId = asset.Id, MediaId = image.Id, Name = image.Name!, Role = "Continuity state" }];
        var generator = new MockVideoGenerator(f.Assets, f.Shots);
        await generator.ValidateInputsAsync(Snapshot(f.Project.Id, shot), _ct);
        var trashed = await f.Assets.DeleteImageAsync(f.Project.Id, asset.Id, image.Id, saved.Library.Revision, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => generator.ValidateInputsAsync(Snapshot(f.Project.Id, shot), _ct));
        var restored = await f.Assets.RestoreImagesAsync(f.Project.Id, [trashed.Library.Trash[0].Id], trashed.Library.Revision, _ct);
        Assert.Equal(image.Source, restored.Assets[0].Images[0].Source);
        await generator.ValidateInputsAsync(Snapshot(f.Project.Id, shot), _ct);
    }

    [Fact]
    public async Task CropUsesOriginalPixelsAndDestinationLookWithoutModifyingItsParent()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, f.Shots, shot);
        var source = await f.Assets.SaveDerivedImageAsync(f.Project.Id, FrameCopy(d.Takes[0]), 0, _ct);
        var look = new CharacterLook { Name = "Everyday" };
        var character = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Mouse", Category = AssetCategory.Character, Looks = [look] };
        var library = await f.Assets.SaveAsync(source.Library with { Assets = [.. source.Library.Assets, character] }, source.Library.Revision, _ct);
        var crop = new ImageCropRegion { X = .2, Y = .1, Width = .5, Height = .6 };
        var request = new DerivedImageRequest(Guid.NewGuid(), new(character.Id, look.Id), "Mouse crop", "Face", Parent: new(source.AssetId, source.ImageId), Crop: crop);
        var saved = await f.Assets.SaveDerivedImageAsync(f.Project.Id, request, library.Revision, _ct);
        var image = saved.Library.Assets.Single(a => a.Id == character.Id).Images.Single();
        Assert.Equal(look.Id, image.LookId); Assert.Equal(AssetImageOrigin.Cropped, image.Origin);
        Assert.Equal(new CroppedImageSource(f.Project.Id, source.AssetId, source.ImageId, crop, 32, 32), image.Source!.Crop);
        await using (var original = await f.Assets.OpenImageAsync(f.Project.Id, source.AssetId, source.ImageId, _ct))
        await using (var copy = await f.Assets.OpenImageAsync(f.Project.Id, character.Id, image.Id, _ct))
        {
            using var a = await Image.LoadAsync<Rgb24>(original!.Content, _ct);
            Assert.Equal(32, a.Width); Assert.Equal(32, a.Height);
            a.Mutate(c => c.Crop(new Rectangle(6, 3, 17, 20)));
            using var b = await Image.LoadAsync<Rgb24>(copy!.Content, _ct); AssertPixels(a, b);
        }
        var deleted = await f.Assets.DeleteAssetAsync(f.Project.Id, source.AssetId, saved.Library.Revision, _ct);
        await f.Assets.PurgeImagesAsync(f.Project.Id, deleted.Trash.Select(t => t.Id).ToArray(), deleted.Revision, _ct);
        await using var remaining = await f.Assets.OpenImageAsync(f.Project.Id, character.Id, image.Id, _ct);
        Assert.NotNull(remaining);
    }

    [Fact]
    public async Task CopyReceiptPreventsDuplicateRetriesEvenAfterTrashPurgeAndMetadataSaves()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, f.Shots, shot); var request = FrameCopy(d.Takes[0]);
        var copies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Assets.SaveDerivedImageAsync(f.Project.Id, request, 0, _ct)));
        Assert.All(copies, r => Assert.Equal(1, r.Library.Revision));
        var saved = copies[0];
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.SaveDerivedImageAsync(f.Project.Id, request with { Notes = "Different" }, 1, _ct));
        var library = await f.Assets.SaveAsync(saved.Library with { ImageCopyReceipts = [] }, 1, _ct);
        Assert.Single(library.ImageCopyReceipts);
        library = (await f.Assets.DeleteImageAsync(f.Project.Id, saved.AssetId, saved.ImageId, library.Revision, _ct)).Library;
        library = (await f.Assets.PurgeImagesAsync(f.Project.Id, [library.Trash[0].Id], library.Revision, _ct)).Library;
        var retry = await f.Assets.SaveDerivedImageAsync(f.Project.Id, request, 0, _ct);
        Assert.False(retry.Available); Assert.Empty(retry.Library.Assets[0].Images); Assert.Equal(library.Revision, retry.Library.Revision);
    }

    [Fact]
    public async Task CopyRejectsMissingOrTrashedSourcesConflictsAndInvalidLookOwnership()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, f.Shots, shot); var request = FrameCopy(d.Takes[0]);
        var look = new CharacterLook { Name = "Archived", Archived = true };
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Mouse", Category = AssetCategory.Character, Looks = [look] };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Assets.SaveDerivedImageAsync(f.Project.Id, request, 0, _ct));
        foreach (var invalid in new[] { request with { FrameIndex = 999 }, request with { TakeId = Guid.NewGuid() },
            request with { Destination = new(asset.Id, look.Id) }, request with { Destination = new(asset.Id, Guid.NewGuid()) } })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.SaveDerivedImageAsync(f.Project.Id, invalid, library.Revision, _ct));
        d = await f.Shots.DiscardAsync(f.Project.Id, d.Takes[0].Id, ShotTrashKind.Take, d.Revision, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.SaveDerivedImageAsync(f.Project.Id, request, library.Revision, _ct));
        Assert.Equal(library.Revision, (await f.Assets.LoadAsync(f.Project.Id, _ct)).Revision);
        shot.Images = [new() { Kind = ShotImageKind.ContinuityFrame, MediaId = Guid.NewGuid() }];
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => new MockVideoGenerator(f.Assets, f.Shots).ValidateInputsAsync(Snapshot(f.Project.Id, shot), _ct));
    }

    [Fact]
    public async Task FailedCopyPublicationLeavesNoAssetOrImageAndCanRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, f.Shots, shot); var request = FrameCopy(d.Takes[0]);
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id }, 0, _ct);
        var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        await using (var locked = TestProjectDatabase.HoldReadOnly(Path.Combine(dir)))
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.SaveDerivedImageAsync(f.Project.Id, request, library.Revision, _ct));
        var unchanged = await f.Assets.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(library.Revision, unchanged.Revision); Assert.Empty(unchanged.Assets); Assert.Empty(unchanged.ImageCopyReceipts);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "assets"), "*.png", SearchOption.AllDirectories));
        Assert.True((await f.Assets.SaveDerivedImageAsync(f.Project.Id, request, library.Revision, _ct)).Available);
    }

    [Fact]
    public async Task CropAppliesExifBeforeCoordinatesAndKeepsSourceMetadataImmutable()
    {
        var f = Fixture(); var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Oriented source", Category = AssetCategory.Reference };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        using var original = new Image<Rgb24>(9, 6);
        for (var y = 0; y < 6; y++) for (var x = 0; x < 9; x++) original[x, y] = new((byte)(x * 20), (byte)(y * 35), (byte)(x + y));
        original.Metadata.ExifProfile = new();
        original.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)6);
        await using var bytes = new MemoryStream(); await original.SaveAsPngAsync(bytes, _ct); bytes.Position = 0;
        library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, bytes, new("oriented.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        var parent = library.Assets[0].Images[0]; Assert.Equal((6, 9), (parent.Width, parent.Height));
        var crop = new ImageCropRegion { X = .2, Y = .1, Width = .5, Height = .6 };
        var request = new DerivedImageRequest(Guid.NewGuid(), new(asset.Id), "Crop", "", Parent: new(asset.Id, parent.Id), Crop: crop);
        var saved = await f.Assets.SaveDerivedImageAsync(f.Project.Id, request, library.Revision, _ct);
        await using var media = await f.Assets.OpenImageAsync(f.Project.Id, asset.Id, saved.ImageId, _ct);
        using var copy = await Image.LoadAsync<Rgb24>(media!.Content, _ct);
        Assert.Equal((4, 7), (copy.Width, copy.Height)); Assert.Null(copy.Metadata.ExifProfile);
        for (var y = 0; y < copy.Height; y++) for (var x = 0; x < copy.Width; x++) Assert.Equal(original[y, 4 - x], copy[x, y]);
        var tampered = saved.Library.Copy();
        var derived = tampered.Assets[0].Images[1];
        tampered.Assets[0].Images[1] = derived with { Source = derived.Source! with { Crop = derived.Source.Crop! with { ImageId = Guid.NewGuid() } } };
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.SaveAsync(tampered, saved.Library.Revision, _ct));
        foreach (var invalid in new[] { crop with { Width = double.NaN }, crop with { X = -1 }, crop with { Height = 1 } })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.SaveDerivedImageAsync(f.Project.Id, request with { ImageId = Guid.NewGuid(), Crop = invalid }, saved.Library.Revision, _ct));
    }

    [Fact]
    public async Task CroppedTakeFrameKeepsTakeProvenanceAndStaysAContinuityFrame()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, f.Shots, shot); var take = d.Takes[0];
        var crop = new ImageCropRegion { X = .2, Y = .1, Width = .5, Height = .6 };
        var saved = await f.Assets.SaveDerivedImageAsync(f.Project.Id, FrameCopy(take) with { Crop = crop }, 0, _ct);
        var image = Assert.Single(Assert.Single(saved.Library.Assets).Images);
        Assert.Equal(AssetImageOrigin.VideoFrame, image.Origin); Assert.Equal((17, 20), (image.Width, image.Height));
        Assert.Equal(new VideoFrameSource(f.Project.Id, shot.Id, take.Id, 5, 5 / 24d, 24, 32, 32) { Lossless = true, Crop = crop }, image.Source!.Frame);
        Assert.True(ReferenceSetups.IsContinuity(saved.AssetId, image.Id, saved.Library));
        await using (var original = await f.Shots.OpenAsync(f.Project.Id, take.Id, ShotTrashKind.Take, 5, ct: _ct))
        await using (var copy = await f.Assets.OpenImageAsync(f.Project.Id, saved.AssetId, image.Id, _ct))
        {
            using var a = await Image.LoadAsync<Rgb24>(original!.Content, _ct);
            a.Mutate(c => c.Crop(new Rectangle(6, 3, 17, 20)));
            using var b = await Image.LoadAsync<Rgb24>(copy!.Content, _ct); AssertPixels(a, b);
        }
    }

    [Fact]
    public async Task AFullFrameCropSavesExactlyLikeAnUncroppedFrameAndSharesItsReceipt()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, f.Shots, shot);
        var request = FrameCopy(d.Takes[0]);
        var saved = await f.Assets.SaveDerivedImageAsync(f.Project.Id, request with { Crop = new() { X = 0, Y = 0, Width = 1, Height = 1 } }, 0, _ct);
        var image = Assert.Single(saved.Library.Assets[0].Images);
        Assert.Null(image.Source!.Frame!.Crop); Assert.Equal((32, 32), (image.Width, image.Height));
        // An uncropped retry matches the same receipt rather than counting as different inputs.
        var retry = await f.Assets.SaveDerivedImageAsync(f.Project.Id, request, 0, _ct);
        Assert.Equal(image.Id, retry.ImageId); Assert.Single(retry.Library.Assets[0].Images);
    }

    [Fact]
    public void UncroppedFrameSourcesSerializeExactlyAsBefore()
    {
        var frame = new VideoFrameSource(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 5, 5 / 24d, 24, 32, 32) { Lossless = true };
        var reel = new ReelImageSource(Guid.NewGuid(), Guid.NewGuid(), "Turn", new(Guid.NewGuid(), new('A', 64), 3, .125), true, 32, 32);
        foreach (var json in new[] { System.Text.Json.JsonSerializer.Serialize(frame, AtomicJsonFile.Options), System.Text.Json.JsonSerializer.Serialize(reel, AtomicJsonFile.Options) })
            Assert.DoesNotContain("crop", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FrameImagesWithACropMustMatchTheCroppedPixelSize()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var d = await AddTake(f.Project.Id, f.Shots, shot);
        var crop = new ImageCropRegion { X = .2, Y = .1, Width = .5, Height = .6 };
        var saved = await f.Assets.SaveDerivedImageAsync(f.Project.Id, FrameCopy(d.Takes[0]) with { Crop = crop }, 0, _ct);
        var image = saved.Library.Assets[0].Images[0];
        foreach (var damaged in new ImageCropRegion?[] { null, crop with { Width = .25 }, crop with { Width = double.NaN }, crop with { X = .9 } })
        {
            var tampered = saved.Library.Copy();
            tampered.Assets[0].Images[0] = image with { Source = image.Source! with { Frame = image.Source.Frame! with { Crop = damaged } } };
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.SaveAsync(tampered, saved.Library.Revision, _ct));
        }
        // The recorded crop of the full frame is what makes the smaller image valid.
        var unchanged = saved.Library.Copy();
        Assert.Equal(saved.Library.Revision + 1, (await f.Assets.SaveAsync(unchanged, saved.Library.Revision, _ct)).Revision);
    }

    private static void AssertPixels(Image<Rgb24> a, Image<Rgb24> b)
    {
        Assert.Equal(a.Size, b.Size);
        for (var y = 0; y < a.Height; y++) for (var x = 0; x < a.Width; x++) Assert.Equal(a[x, y], b[x, y]);
    }
}
