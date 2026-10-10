using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProjectCopyReelKeyframesAndManagedVoicesSurviveMissingArchives(bool keepPicture)
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await AssetReuseFixture(); var owner = f.Library.Assets[0];
        var environment = new AssetEnvironment { ContentRootPath = _directory };
        var options = Options.Create(new ProjectStorageOptions { RootDirectory = "projects" });
        var projects = new FileProjectStore(options, environment, _clock, NullLogger<FileProjectStore>.Instance);
        var files = new ProjectFiles(options, environment, projects);
        var shots = new FileShotStore(files, _clock); var scripts = new FileScriptStore(files, _clock);
        var videos = new FileReferenceVideoStore(files, shots, null!);
        byte[] video = [1, 2, 3, 4];
        var media = new ReferenceVideoMedia(Guid.NewGuid(), Convert.ToHexString(SHA256.HashData(video)), video.Length, 64, 48, 48, 24, 2, true);
        var root = Path.Combine(AssetReuseProject(f.Source.Id), "reference-videos", media.Id.ToString("D")); Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "video.mp4"), video, ct);
        await AtomicJsonFile.WriteAsync(Path.Combine(root, "media.json"), media, ct);
        var archive = new ReelFrameArchive(media.Sha256, 64, 48, 48,
            Enumerable.Range(0, 48).Select(i => new ShotFrame(i, $"segment-{i / 24}.webp", 3) { ArchiveFrameIndex = i % 24 }).ToArray(),
            [new("segment-0.webp", 3, new('A', 64)), new("segment-1.webp", 3, new('B', 64))]);
        await AtomicJsonFile.WriteAsync(Path.Combine(root, "frame-archive.json"), archive, ct);
        await AtomicJsonFile.WriteAsync(Path.Combine(root, "frame-times-v2.json"), new ReelFrameCatalog(media.Sha256, false, Enumerable.Range(0, 48).Select(i => i / 24d).ToArray()), ct);
        var reel = new AssetReferenceReel { AssetId = owner.Id, Name = "Turn", Media = media, CreatedUtc = DateTimeOffset.UtcNow,
            Keyframes = new() { Frames = [new() { Frame = new(media.Id, AssetReusePolicy.Hash(archive), 12, .5), Notes = "Profile", Crop = new() { Width = .5 } }] } };
        if (keepPicture) await File.WriteAllBytesAsync(Path.Combine(root, FileReferenceVideoStore.FrameFileName(reel.Keyframes.Frames[0].Frame)), Png(64, 48), ct);
        var voice = new VoiceReference { AssetId = owner.Id, Name = "Voice", Duration = 4, Start = .5, ExcerptDuration = 2, ContentType = "audio/wav", CreatedUtc = DateTimeOffset.UtcNow };
        voice.FileName = voice.Id.ToString("N") + ".wav";
        var voiceRoot = Path.Combine(AssetReuseProject(f.Source.Id), "assets", owner.Id.ToString("D"), "voices"); Directory.CreateDirectory(voiceRoot);
        await File.WriteAllBytesAsync(Path.Combine(voiceRoot, voice.FileName), [5, 6, 7, 8], ct);
        var library = f.Library with { Reels = [reel], Voices = [voice], Assets = [owner with { DefaultVoiceId = voice.Id }] };
        await AtomicJsonFile.WriteAsync(Path.Combine(AssetReuseProject(f.Source.Id), "assets.json"), library, ct);
        var shot = new Shot { Title = "Turn", Duration = 2, Description = "Mira turns.", Dialogue = [new() { Speaker = "Mira", Text = "Hello." }] };
        await shots.SaveAsync(f.Source.Id, [shot], 0, ct: ct);
        var production = new FileProductionStore(files, shots, f.Store, projects, _clock, new FileAiJobStore(Path.Combine(_directory, "jobs"), _clock), videos);
        var composition = (await production.InitializeAsync(f.Source.Id, ct)).Compositions[0];
        var recording = new ShotVoiceBinding { AssetId = owner.Id, CharacterAssetId = owner.Id, VoiceId = voice.Id, Speaker = "Mira", Start = .5, Duration = 2 };
        composition.Shot.Images = [new() { AssetId = owner.Id, MediaId = owner.Images[0].Id, LookId = owner.Images[0].LookId, Name = "Mira", InferUsage = true }];
        composition.Shot.Videos = [new() { Media = media, OwnerAssetId = owner.Id, Name = "Turn", Visuals = ReelVisuals.Keyframes, Keyframes = ShotCopy.Of(reel.Keyframes) }];
        composition.Shot.Voices = [recording];
        composition.Shot.CharacterVoices = [new() { AssetId = owner.Id, CharacterName = "Mira", Source = CharacterVoiceSource.Recording, Speaker = "Mira", SpeakerConfirmed = true, Recording = recording }];
        await production.SaveAsync(f.Source.Id, composition, composition.Version, ct);
        var copier = new ShotProjectCopyStore(files, shots, production, scripts, f.Store, f.Store, _clock, referenceVideos: videos);
        var preview = await copier.PreviewAsync(f.Source.Id, [shot.Id], ct);
        await copier.CopyAsync(new(Guid.NewGuid(), f.Source.Id, f.Target.Id, [shot.Id], preview.ShotRevision, preview.ProductionRevision, preview.ScriptRevision), ct: ct);
        var copied = (await production.LoadAsync(f.Target.Id, ct)).Compositions.Single().Shot;
        var copiedLibrary = await f.Store.LoadAsync(f.Target.Id, ct);
        CharacterVoices.Validate(copied); ReferenceVideos.Validate(copied);
        var copiedReel = copied.Videos.Single(); var frame = copiedReel.Keyframes!.Frames.Single();
        Assert.Equal(keepPicture ? AssetReusePolicy.Hash(archive) : media.Sha256, frame.Frame.Source);
        Assert.Equal(copiedReel.Media.Id, frame.Frame.MediaId);
        Assert.Equal("Profile", frame.Notes); Assert.Equal(.5, frame.Crop!.Width);
        Assert.Equal(copiedLibrary.Voices[0].Id, copied.Voices[0].VoiceId);
        Assert.Equal(copiedLibrary.Assets[0].Id, copied.CharacterVoices![0].AssetId);
        Assert.Equal(copiedLibrary.Voices[0].Id, copiedLibrary.Assets[0].DefaultVoiceId);
    }
}
