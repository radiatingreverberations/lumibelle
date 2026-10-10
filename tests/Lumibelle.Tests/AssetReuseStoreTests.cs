using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

// Uses the existing real file-store fixture. Media-copy tests do not invoke a model or FFmpeg.
public sealed partial class AssetStoreTests
{
    private async Task<(ProjectInfo Source, ProjectInfo Target, FileAssetStore Store, AssetLibrary Library)> AssetReuseFixture()
    {
        var (source, store) = CreateStore("Episode one"); var (target, _) = CreateStore("Episode two");
        var look = new CharacterLook { Name = "Everyday", Description = "Blue outfit" };
        var asset = Asset("Mira") with { Category = AssetCategory.Character, Looks = [look] };
        var library = await store.SaveAsync(new() { ProjectId = source.Id, Assets = [asset] }, 0, TestContext.Current.CancellationToken);
        using var png = new MemoryStream(Png(12, 8));
        library = await store.AddImageAsync(source.Id, asset.Id, png, new("mira.png", ["front"], AssetImageOrigin.Imported, LookId: look.Id), library.Revision, TestContext.Current.CancellationToken);
        var image = library.Assets[0].Images[0];
        library.Assets[0] = library.Assets[0] with { Description = "Main character", PreservationGuidance = "Keep the identity",
            Images = [image with { Name = "Front view", VisualDescription = "A detailed visual description", PreservationGuidance = "Keep glasses", IsReference = true, IsCover = true }],
            PreferredIdentityReferences = [new(image.Id, look.Id, "Face")] };
        library = await store.SaveAsync(library, library.Revision, TestContext.Current.CancellationToken);
        return (source, target, store, library);
    }
    private string AssetReuseProject(Guid project) => Path.Combine(_directory, "projects", project.ToString("D"));
    private static AssetReuseCommand AssetReuseRequest(AssetLibrary source, Guid target, AssetReuseKind kind = AssetReuseKind.Asset, bool move = false, Guid? targetAsset = null)
    {
        var owner = source.Assets[0];
        var selection = new AssetReuseSelection(source.ProjectId, owner.Id, kind, kind switch {
            AssetReuseKind.Image => owner.Images[0].Id, AssetReuseKind.Reel => source.Reels[0].Id, AssetReuseKind.Voice => source.Voices[0].Id, _ => null });
        return new(Guid.NewGuid(), selection, null, AssetReusePolicy.Hash(AssetReusePolicy.Capture(source, selection)), new(target, "Mira copy", targetAsset), move);
    }
    [Fact]
    public async Task AssetReuseCopyOwnsNewImageBytesAndSurvivesSourceDeletion()
    {
        var f = await AssetReuseFixture(); var command = AssetReuseRequest(f.Library, f.Target.Id);
        var copied = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken); var image = Assert.Single(Assert.Single(copied.Library.Assets).Images);
        Assert.True(copied.Available); Assert.NotEqual(f.Library.Assets[0].Id, copied.AssetId); Assert.Equal("A detailed visual description", image.VisualDescription);
        Assert.Null(image.StorageAssetId); Assert.Empty(image.PreviousAssetIds);
        await f.Store.DeleteAssetAsync(f.Source.Id, f.Library.Assets[0].Id, f.Library.Revision, TestContext.Current.CancellationToken);
        await using var media = await f.Store.OpenImageAsync(f.Target.Id, copied.AssetId, image.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Png(12, 8), await Bytes(media!.Content));
        Assert.NotEqual(f.Library.Assets[0].Looks[0].Id, copied.Library.Assets[0].Looks[0].Id);
    }
    [Fact]
    public async Task AssetReuseRetryUsesReceiptBeforeReadingDeletedSourceProjectFiles()
    {
        var f = await AssetReuseFixture(); var command = AssetReuseRequest(f.Library, f.Target.Id);
        var first = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        Directory.Delete(AssetReuseProject(f.Source.Id), true);
        var second = await CreateFreshStore().ReuseAsync(command, TestContext.Current.CancellationToken);
        Assert.Equal(first.AssetId, second.AssetId); Assert.Equal(first.Library.Revision, second.Library.Revision); Assert.Single(second.Library.Assets);
    }
    [Fact]
    public async Task AssetReuseNewCommandCreatesAnotherIndependentCopyButSameCommandCannotChangeInput()
    {
        var f = await AssetReuseFixture(); var command = AssetReuseRequest(f.Library, f.Target.Id);
        var first = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Store.ReuseAsync(command with { Destination = command.Destination with { Name = "Changed" } }, TestContext.Current.CancellationToken));
        var second = await f.Store.ReuseAsync(command with { Id = Guid.NewGuid() }, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.AssetId, second.AssetId); Assert.Equal(2, second.Library.Assets.Count);
    }
    [Fact]
    public async Task AssetReuseRepeatedCopyDoesNotResurrectADiscardedDestination()
    {
        var f = await AssetReuseFixture(); var command = AssetReuseRequest(f.Library, f.Target.Id);
        var first = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        await f.Store.DeleteAssetAsync(f.Target.Id, first.AssetId, first.Library.Revision, TestContext.Current.CancellationToken);
        var retry = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        Assert.False(retry.Available); Assert.Empty(retry.Library.Assets); Assert.Single(retry.Library.AssetReuseReceipts!);
    }
    [Fact]
    public async Task AssetReuseImageDuplicateIntoSameOwnerDoesNotDuplicateCoverOrAlterOriginal()
    {
        var f = await AssetReuseFixture(); var original = f.Library.Assets[0].Images[0];
        var command = AssetReuseRequest(f.Library, f.Source.Id, AssetReuseKind.Image, targetAsset: f.Library.Assets[0].Id);
        var result = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        var owner = Assert.Single(result.Library.Assets); Assert.Equal(2, owner.Images.Count); Assert.Single(owner.Images, i => i.IsCover);
        Assert.Equal(AssetReusePolicy.Hash(original), AssetReusePolicy.Hash(owner.Images[0])); Assert.Equal(original.LookId, owner.Images[1].LookId);
        Assert.Equal(original.VisualDescription, owner.Images[1].VisualDescription);
    }
    [Fact]
    public async Task AssetReuseMovePublishesDestinationThenTrashesSourceAndRecordsAcknowledgement()
    {
        var f = await AssetReuseFixture(); var command = AssetReuseRequest(f.Library, f.Target.Id, move: true);
        var result = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken); Assert.True(result.SourceRemoved);
        var source = await f.Store.LoadAsync(f.Source.Id, TestContext.Current.CancellationToken);
        Assert.Empty(source.Assets); var trashed = Assert.Single(source.Trash); Assert.Single(source.AssetMoveReceipts!);
        source = await f.Store.RestoreImagesAsync(f.Source.Id, [trashed.Id], source.Revision, TestContext.Current.CancellationToken);
        Assert.Single(source.Assets);
        var retry = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        Assert.True(retry.SourceRemoved); Assert.Single((await f.Store.LoadAsync(f.Source.Id, TestContext.Current.CancellationToken)).Assets);
        Assert.Single(retry.Library.Assets); // An acknowledged move cannot delete a later restoration.
    }
    [Theory]
    [InlineData("shots.json")] [InlineData("production.json")]
    public async Task AssetReuseReferencedSourceRemainsAndRetryFinishesWithoutAnotherCopy(string file)
    {
        var f = await AssetReuseFixture(); var command = AssetReuseRequest(f.Library, f.Target.Id, move: true);
        var path = Path.Combine(AssetReuseProject(f.Source.Id), file);
        await AtomicJsonFile.WriteAsync(path, new { historical = new { imageId = f.Library.Assets[0].Images[0].Id } }, TestContext.Current.CancellationToken);
        var first = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        Assert.True(first.Available); Assert.False(first.SourceRemoved); Assert.Contains("Saved shots", first.Notice);
        Assert.Single((await f.Store.LoadAsync(f.Source.Id, TestContext.Current.CancellationToken)).Assets);
        File.Delete(path); var retry = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        Assert.True(retry.SourceRemoved); Assert.Equal(first.AssetId, retry.AssetId); Assert.Single(retry.Library.Assets);
    }
    [Fact]
    public async Task AssetReuseMoveRefusesRemovalWhenSourceWasEditedAfterPartialCopy()
    {
        var f = await AssetReuseFixture(); var command = AssetReuseRequest(f.Library, f.Target.Id, move: true);
        var guard = Path.Combine(AssetReuseProject(f.Source.Id), "shots.json");
        await AtomicJsonFile.WriteAsync(guard, new { assetId = f.Library.Assets[0].Id }, TestContext.Current.CancellationToken);
        var first = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken); Assert.False(first.SourceRemoved);
        var changed = await f.Store.LoadAsync(f.Source.Id, TestContext.Current.CancellationToken);
        changed.Assets[0] = changed.Assets[0] with { Description = "Newer local description" };
        await f.Store.SaveAsync(changed, changed.Revision, TestContext.Current.CancellationToken); File.Delete(guard);
        var retry = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        Assert.False(retry.SourceRemoved); Assert.Contains("edited after copying", retry.Notice); Assert.Single(retry.Library.Assets);
        Assert.Equal("Newer local description", (await f.Store.LoadAsync(f.Source.Id, TestContext.Current.CancellationToken)).Assets[0].Description);
    }
    [Fact]
    public async Task AssetReuseMoveKeepsSourceWhenDestinationFileWasLostBeforeRemoval()
    {
        var f = await AssetReuseFixture(); var command = AssetReuseRequest(f.Library, f.Target.Id, move: true);
        var guard = Path.Combine(AssetReuseProject(f.Source.Id), "shots.json");
        await AtomicJsonFile.WriteAsync(guard, new { assetId = f.Library.Assets[0].Id }, TestContext.Current.CancellationToken);
        var first = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        var image = first.Library.Assets[0].Images[0];
        File.Delete(Path.Combine(AssetReuseProject(f.Target.Id), "assets", first.AssetId.ToString("D"), "images", image.FileName)); File.Delete(guard);
        var retry = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        Assert.False(retry.SourceRemoved); Assert.Contains("destination media", retry.Notice);
        Assert.Single((await f.Store.LoadAsync(f.Source.Id, TestContext.Current.CancellationToken)).Assets);
    }
    [Fact]
    public async Task AssetReuseMoveKeepsSourceWhileAnySourceProjectAiRequestIsRetained()
    {
        var f = await AssetReuseFixture(); var command = AssetReuseRequest(f.Library, f.Target.Id, move: true);
        var path = Path.Combine(_directory, "App_Data", "ai-jobs", "queue.json");
        var job = new AiJobHeader { Id = Guid.NewGuid(), Kind = AiJobKind.ScriptAssistant, Backend = AiBackend.ComfyUI, Target = new(f.Source.Id),
            ProjectName = "Episode one", TargetName = "Retained request", OriginTabId = Guid.NewGuid(), RequestFingerprint = new string('A', 64), CreatedUtc = DateTimeOffset.UtcNow };
        await AtomicJsonFile.WriteAsync(path, new AiQueueDocument { Jobs = [job] }, TestContext.Current.CancellationToken);
        var result = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        Assert.False(result.SourceRemoved); Assert.Contains("active or retained", result.Notice); Assert.Single(result.Library.Assets);
    }
    [Fact]
    public async Task AssetReuseSharedSnapshotSurvivesSourceProjectRemovalAndImportsRepeatedly()
    {
        var f = await AssetReuseFixture(); var selection = new AssetReuseSelection(f.Source.Id, f.Library.Assets[0].Id);
        var fingerprint = AssetReusePolicy.Hash(AssetReusePolicy.Capture(f.Library, selection)); var id = Guid.NewGuid();
        var entry = await f.Store.PublishSharedAsync(id, selection, fingerprint, "Mira / Season one", TestContext.Current.CancellationToken);
        Assert.Equal(1, entry.Images); Assert.Single(await f.Store.ListSharedAsync(TestContext.Current.CancellationToken));
        var repeated = await f.Store.PublishSharedAsync(id, selection, fingerprint, entry.Name, TestContext.Current.CancellationToken); Assert.Equal(entry, repeated);
        Directory.Delete(AssetReuseProject(f.Source.Id), true);
        var command = new AssetReuseCommand(Guid.NewGuid(), null, id, fingerprint, new(f.Target.Id, "Episode two Mira"));
        var one = await f.Store.ReuseAsync(command, TestContext.Current.CancellationToken);
        var two = await f.Store.ReuseAsync(command with { Id = Guid.NewGuid() }, TestContext.Current.CancellationToken);
        Assert.NotEqual(one.AssetId, two.AssetId); Assert.Equal(2, two.Library.Assets.Count);
        var image = two.Library.Assets.Single(a => a.Id == two.AssetId).Images[0];
        await using var bytes = await f.Store.OpenImageAsync(f.Target.Id, two.AssetId, image.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Png(12, 8), await Bytes(bytes!.Content));
    }
    [Fact]
    public async Task AssetReuseSharedHashFailureDoesNotPublishAnAsset()
    {
        var f = await AssetReuseFixture(); var source = new AssetReuseSelection(f.Source.Id, f.Library.Assets[0].Id);
        var fingerprint = AssetReusePolicy.Hash(AssetReusePolicy.Capture(f.Library, source)); var id = Guid.NewGuid();
        await f.Store.PublishSharedAsync(id, source, fingerprint, "Mira", TestContext.Current.CancellationToken);
        var file = Directory.GetFiles(Path.Combine(_directory, "App_Data", "shared-assets", id.ToString("D"), "images")).Single();
        await File.WriteAllBytesAsync(file, [1, 2, 3], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Store.ReuseAsync(new(Guid.NewGuid(), null, id, fingerprint, new(f.Target.Id, "Damaged")), TestContext.Current.CancellationToken));
        Assert.Empty((await f.Store.LoadAsync(f.Target.Id, TestContext.Current.CancellationToken)).Assets);
        Assert.Single((await f.Store.LoadAsync(f.Source.Id, TestContext.Current.CancellationToken)).Assets);
    }
    [Fact]
    public async Task AssetReuseMissingSourceFileAndChangedFingerprintLeaveDestinationEmpty()
    {
        var f = await AssetReuseFixture(); var command = AssetReuseRequest(f.Library, f.Target.Id);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Store.ReuseAsync(command with { SourceFingerprint = new string('0', 64) }, TestContext.Current.CancellationToken));
        File.Delete(Path.Combine(AssetReuseProject(f.Source.Id), "assets", f.Library.Assets[0].Id.ToString("D"), "images", f.Library.Assets[0].Images[0].FileName));
        await Assert.ThrowsAnyAsync<IOException>(() => f.Store.ReuseAsync(command with { Id = Guid.NewGuid() }, TestContext.Current.CancellationToken));
        Assert.Empty((await f.Store.LoadAsync(f.Target.Id, TestContext.Current.CancellationToken)).Assets);
    }
    [Fact]
    public async Task AssetReuseMetadataSaveCannotRemoveStoreOwnedReceipts()
    {
        var f = await AssetReuseFixture(); var copied = await f.Store.ReuseAsync(AssetReuseRequest(f.Library, f.Target.Id), TestContext.Current.CancellationToken);
        var saved = await f.Store.SaveAsync(copied.Library with { AssetReuseReceipts = null }, copied.Library.Revision, TestContext.Current.CancellationToken);
        Assert.Single(saved.AssetReuseReceipts!);
    }
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AssetReuseReelsCopyVideoLosslessArchiveKeyframesAndVoiceDefaultsWithoutOriginalJobs(bool keepArchive, bool keepPicture)
    {
        var f = await AssetReuseFixture(); var library = f.Library; var owner = library.Assets[0];
        byte[] video = [1, 2, 3, 4], sound = [5, 6, 7, 8], segment = [9, 10, 11];
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        var media = new ReferenceVideoMedia(Guid.NewGuid(), Hash(video), video.Length, 64, 48, 48, 24, 2, true);
        var reelRoot = Path.Combine(AssetReuseProject(f.Source.Id), "reference-videos", media.Id.ToString("D")); Directory.CreateDirectory(Path.Combine(reelRoot, "lossless"));
        await File.WriteAllBytesAsync(Path.Combine(reelRoot, "video.mp4"), video, TestContext.Current.CancellationToken);
        await AtomicJsonFile.WriteAsync(Path.Combine(reelRoot, "media.json"), media, TestContext.Current.CancellationToken);
        var archive = new ReelFrameArchive(media.Sha256, 64, 48, 48,
            Enumerable.Range(0, 48).Select(i => new ShotFrame(i, $"segment-{i / 24}.webp", segment.Length) { ArchiveFrameIndex = i % 24 }).ToArray(),
            [new("segment-0.webp", segment.Length, Hash(segment)), new("segment-1.webp", segment.Length, Hash(segment))]);
        if (keepArchive) foreach (var file in archive.Files) await File.WriteAllBytesAsync(Path.Combine(reelRoot, "lossless", file.FileName), segment, TestContext.Current.CancellationToken);
        await AtomicJsonFile.WriteAsync(Path.Combine(reelRoot, "frame-archive.json"), archive, TestContext.Current.CancellationToken);
        var reel = new AssetReferenceReel { AssetId = owner.Id, Name = "Turn", Media = media, CreatedUtc = DateTimeOffset.UtcNow,
            Keyframes = new() { Frames = [new() { Frame = new(media.Id, AssetReusePolicy.Hash(archive), 12, .5), Notes = "Profile", Crop = new() { Width = .5 } }] } };
        var pictureName = lumibelle.Services.Production.FileReferenceVideoStore.FrameFileName(reel.Keyframes.Frames[0].Frame);
        if (keepPicture) await File.WriteAllBytesAsync(Path.Combine(reelRoot, pictureName), Png(64, 48), TestContext.Current.CancellationToken);
        await AtomicJsonFile.WriteAsync(Path.Combine(reelRoot, "frame-times-v2.json"), new ReelFrameCatalog(media.Sha256, false, Enumerable.Range(0, 48).Select(i => i / 24d).ToArray()), TestContext.Current.CancellationToken);
        var voice = new VoiceReference { AssetId = owner.Id, Name = "Voice", Duration = 4, Start = .5, ExcerptDuration = 2, ContentType = "audio/wav", CreatedUtc = DateTimeOffset.UtcNow };
        voice.FileName = voice.Id.ToString("N") + ".wav";
        var voiceDir = Path.Combine(AssetReuseProject(f.Source.Id), "assets", owner.Id.ToString("D"), "voices"); Directory.CreateDirectory(voiceDir);
        await File.WriteAllBytesAsync(Path.Combine(voiceDir, voice.FileName), sound, TestContext.Current.CancellationToken);
        library = library with { Reels = [reel], Voices = [voice], Assets = [owner with { DefaultVoiceId = voice.Id }] };
        FileAssetStore.Validate(library, f.Source.Id);
        await AtomicJsonFile.WriteAsync(Path.Combine(AssetReuseProject(f.Source.Id), "assets.json"), library, TestContext.Current.CancellationToken);
        var result = await f.Store.ReuseAsync(AssetReuseRequest(library, f.Target.Id), TestContext.Current.CancellationToken);
        var copiedReel = Assert.Single(result.Library.Reels); var copiedVoice = Assert.Single(result.Library.Voices);
        Assert.NotEqual(reel.Id, copiedReel.Id); Assert.NotEqual(media.Id, copiedReel.Media.Id); Assert.Null(copiedReel.Generation);
        Assert.Equal(copiedVoice.Id, result.Library.Assets[0].DefaultVoiceId); Assert.Equal(voice.ExcerptDuration, copiedVoice.ExcerptDuration);
        Assert.Equal(copiedReel.Media.Id, copiedReel.Keyframes!.Frames[0].Frame.MediaId); Assert.Equal(keepArchive || keepPicture ? AssetReusePolicy.Hash(archive) : media.Sha256, copiedReel.Keyframes.Frames[0].Frame.Source);
        Assert.Equal("Profile", copiedReel.Keyframes.Frames[0].Notes); Assert.Equal(.5, copiedReel.Keyframes.Frames[0].Crop!.Width);
        var destination = Path.Combine(AssetReuseProject(f.Target.Id), "reference-videos", copiedReel.Media.Id.ToString("D"));
        Assert.Equal(video, await File.ReadAllBytesAsync(Path.Combine(destination, "video.mp4"), TestContext.Current.CancellationToken));
        if (keepArchive) Assert.Equal(segment, await File.ReadAllBytesAsync(Path.Combine(destination, "lossless", archive.Files[0].FileName), TestContext.Current.CancellationToken));
        else if (keepPicture) Assert.Equal(Png(64, 48), await File.ReadAllBytesAsync(Path.Combine(destination, pictureName), TestContext.Current.CancellationToken));
        else Assert.False(File.Exists(Path.Combine(destination, pictureName)));
        Assert.Equal(AssetReusePolicy.Hash(archive), reel.Keyframes.Frames[0].Frame.Source);
        Assert.Equal(copiedReel.Media, await AtomicJsonFile.ReadAsync<ReferenceVideoMedia>(Path.Combine(destination, "media.json"), TestContext.Current.CancellationToken));
        await using var copiedAudio = await f.Store.OpenVoiceAsync(f.Target.Id, copiedVoice.Id, ct: TestContext.Current.CancellationToken);
        Assert.Equal(sound, await Bytes(copiedAudio!.Content));
    }
}
