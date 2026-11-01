using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact] public async Task ReelVoiceExtractionIsIndependentAndDefaultPublicationIsAtomicAndIdempotent()
    {
        var f = Fixture(); var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        var d = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner] }, 0, _ct);
        var path = Path.Combine(_root, "voice-source.mp4");
        await FfmpegFixture("-f", "lavfi", "-i", "color=size=32x32:rate=24:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=32000:duration=3",
            "-c:v", "libx264", "-threads", "1", "-c:a", "aac", "-shortest", path);
        var videos = new FileReferenceVideoStore(f.Files, f.Shots, new ProductionMediaTools());
        await using var source = File.OpenRead(path); var media = await videos.ImportAsync(f.Project.Id, source, "reel.mp4", new(), _ct);
        var reel = new AssetReferenceReel { AssetId = owner.Id, Name = "Elementalist", Media = media };
        d = await f.Assets.SaveReelAsync(f.Project.Id, reel, d.Revision, _ct);
        await using var savedReel = (await videos.OpenAsync(f.Project.Id, media.Id, _ct))!;
        var draft = await f.Assets.StageReelVoiceAsync(f.Project.Id, reel.Id, savedReel.Content, new(), _ct);
        Assert.Equal("audio/wav", draft.ContentType); Assert.Equal(reel.Id, draft.SourceReel!.ReelId);
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, _ct)).Voices);
        var manifest = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "assets.json");
        using (var locked = TestProjectDatabase.HoldReadOnly(Path.GetDirectoryName(manifest)!))
            await Assert.ThrowsAnyAsync<Exception>(() => f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Elementalist voice", .5, 2.5, true, d.Revision, _ct));
        var failed = await f.Assets.LoadAsync(f.Project.Id, _ct);
        Assert.Empty(failed.Voices); Assert.Null(failed.Assets[0].DefaultVoiceId);
        await using (var preview = await f.Assets.OpenVoicePreviewAsync(f.Project.Id, draft.Id, new(), staged: true, ct: _ct)) Assert.NotNull(preview);
        d = await f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Elementalist voice", .5, 2.5, true, d.Revision, _ct);
        Assert.Equal(draft.Id, d.Assets[0].DefaultVoiceId); Assert.Equal(.5, Assert.Single(d.Voices).Start);
        var again = await f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Elementalist voice", .5, 2.5, true, 0, _ct);
        Assert.Equal(d.Revision, again.Revision); Assert.Single(again.Voices);
        d = await f.Assets.TrashReelAsync(f.Project.Id, reel.Id, d.Revision, _ct);
        await using var recording = await f.Assets.OpenVoiceAsync(f.Project.Id, draft.Id, ct: _ct);
        Assert.NotNull(recording); Assert.Equal("audio/wav", recording.ContentType);
        var bytes = new byte[12]; await recording.Content.ReadExactlyAsync(bytes, _ct);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(media.Id, d.Voices[0].SourceReel!.MediaId);
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "Riley", Text = "Hello" }];
        CharacterVoices.Set(shot, CharacterVoices.Initial(shot, d.Assets[0], d), d);
        var run = new VideoRun { Snapshot = Snapshot(f.Project.Id, shot) };
        var directory = Path.Combine(_root, "captured-voice");
        await new ComfyH3Video(null!, null!, f.Assets, f.Assets, f.Shots, new ProductionMediaTools()).PrepareAsync(run, directory, _ct);
        var captured = Assert.Single(run.Inputs); Assert.True(captured.Audio);
        var capturedPath = Path.Combine(directory, "inputs", captured.FileName);
        var exactBytes = await File.ReadAllBytesAsync(capturedPath, _ct);
        Assert.InRange(await new ProductionMediaTools().AudioDurationAsync(capturedPath, new(), _ct), 1.99, 2.01);
        d = await f.Assets.DiscardVoiceAsync(f.Project.Id, draft.Id, d.Revision, _ct);
        Assert.Null(d.Assets[0].DefaultVoiceId);
        Assert.Equal(exactBytes, await File.ReadAllBytesAsync(capturedPath, _ct));
        Assert.Equal(.5, run.Snapshot.Shot.CharacterVoices![0].Recording!.Start);
    }

    [Fact] public async Task DefaultVoicesRequireOwnershipAndTrashRestoreNeverStealsANewerDefault()
    {
        var f = Fixture(); var riley = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        var other = riley with { Id = Guid.NewGuid(), Name = "Mira" };
        var d = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [riley, other] }, 0, _ct);
        using var input = new MemoryStream(Wav());
        d = await f.Assets.AddVoiceAsync(f.Project.Id, riley.Id, input, "one.wav", "First", 0, 1, new(), d.Revision, _ct);
        var first = d.Voices[0]; input.Position = 0;
        d = await f.Assets.AddVoiceAsync(f.Project.Id, riley.Id, input, "two.wav", "Second", 0, 1, new(), d.Revision, _ct);
        var second = d.Voices[1];
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.SetDefaultVoiceAsync(f.Project.Id, other.Id, first.Id, d.Revision, _ct));
        d = await f.Assets.SetDefaultVoiceAsync(f.Project.Id, riley.Id, first.Id, d.Revision, _ct);
        d = await f.Assets.SaveAsync(d with { Assets = d.Assets.Select(a => a with { Description = "Edited description" }).ToList() }, d.Revision, _ct);
        Assert.Equal(first.Id, d.Assets[0].DefaultVoiceId);
        d = await f.Assets.DiscardVoiceAsync(f.Project.Id, first.Id, d.Revision, _ct); Assert.Null(d.Assets[0].DefaultVoiceId);
        d = await f.Assets.SetDefaultVoiceAsync(f.Project.Id, riley.Id, second.Id, d.Revision, _ct);
        d = await f.Assets.RestoreVoicesAsync(f.Project.Id, [d.VoiceTrash[0].Id], d.Revision, _ct);
        Assert.Equal(second.Id, d.Assets[0].DefaultVoiceId);
        d = await f.Assets.SetDefaultVoiceAsync(f.Project.Id, riley.Id, null, d.Revision, _ct); Assert.Null(d.Assets[0].DefaultVoiceId);
    }

    [Fact] public async Task CancelledReelExtractionPublishesNothingAndRejectsMismatchedMedia()
    {
        var f = Fixture(); var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Riley", Category = AssetCategory.Character };
        var d = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner] }, 0, _ct);
        var path = Path.Combine(_root, "cancel-source.mp4");
        await FfmpegFixture("-f", "lavfi", "-i", "color=size=32x32:rate=24:duration=2", "-f", "lavfi", "-i", "sine=duration=2",
            "-c:v", "libx264", "-threads", "1", "-c:a", "aac", "-shortest", path);
        var videos = new FileReferenceVideoStore(f.Files, f.Shots, new ProductionMediaTools());
        await using var source = File.OpenRead(path); var media = await videos.ImportAsync(f.Project.Id, source, "clip.mp4", new(), _ct);
        var reel = new AssetReferenceReel { AssetId = owner.Id, Name = "Voice", Media = media };
        d = await f.Assets.SaveReelAsync(f.Project.Id, reel, d.Revision, _ct);
        using var wrong = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.StageReelVoiceAsync(f.Project.Id, reel.Id, wrong, new(), _ct));
        source.Position = 0; var draft = await f.Assets.StageReelVoiceAsync(f.Project.Id, reel.Id, source, new(), _ct);
        await f.Assets.CancelVoiceImportAsync(f.Project.Id, draft.Id, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "No", 0, 1, true, d.Revision, _ct));
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, _ct)).Voices);
        Assert.Empty(Directory.GetDirectories(Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "voice-imports")));
    }
}
