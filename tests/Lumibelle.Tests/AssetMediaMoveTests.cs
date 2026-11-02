using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(AssetCategory.Character)] [InlineData(AssetCategory.Environment)]
    public async Task ReelMovePreservesRecipeReceiptsAndRecovery(AssetCategory category)
    {
        var f = Fixture(); var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Source", Category = category };
        var target = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Destination", Category = category };
        var other = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Third", Category = category };
        var wrong = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Prop", Category = AssetCategory.Prop };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner, target, other, wrong] }, 0, _ct);
        var recipe = ReferenceReels.NewDraft(owner);
        var candidate = new AssetReferenceReel { AssetId = owner.Id, Name = "Captured reel", UseGuidance = "Keep this guidance",
            Media = new(Guid.NewGuid(), new('A', 64), 8, 64, 64, 124, 24, 124 / 24d, false),
            Generation = new(recipe, Guid.NewGuid(), Guid.NewGuid(), 1, 1, new(f.Project.Id, 0, ReferenceReels.Inputs(recipe), "Exact prompt", "captured", "", new(), 64, 64, 124)) };
        library = await f.Assets.PublishReelAsync(f.Project.Id, candidate, _ct);
        var before = JsonSerializer.Serialize(candidate.Generation, AtomicJsonFile.Options);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.MoveReelAsync(f.Project.Id, owner.Id, candidate.Id, wrong.Id, library.Revision, _ct));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Assets.MoveReelAsync(f.Project.Id, owner.Id, candidate.Id, target.Id, library.Revision - 1, _ct));
        var manifest = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "assets.json");
        using (var locked = TestProjectDatabase.HoldReadOnly(Path.GetDirectoryName(manifest)!))
            await Assert.ThrowsAnyAsync<Exception>(() => f.Assets.MoveReelAsync(f.Project.Id, owner.Id, candidate.Id, target.Id, library.Revision, _ct));
        Assert.Equal(owner.Id, (await f.Assets.LoadAsync(f.Project.Id, _ct)).Reels.Single().AssetId);
        library = await f.Assets.MoveReelAsync(f.Project.Id, owner.Id, candidate.Id, target.Id, library.Revision, _ct);
        var moved = Assert.Single(library.Reels);
        Assert.Equal(target.Id, moved.AssetId); Assert.Null(moved.LookId); Assert.Equal(owner.Id, moved.OriginalAssetId);
        Assert.Equal(candidate.Media, moved.Media); Assert.Equal(candidate.UseGuidance, moved.UseGuidance);
        Assert.Equal(before, JsonSerializer.Serialize(moved.Generation, AtomicJsonFile.Options));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.EditReelDetailsAsync(f.Project.Id, candidate, "Stale edit", candidate.UseGuidance, library.Revision, _ct));
        library = await f.Assets.MoveReelAsync(f.Project.Id, target.Id, candidate.Id, other.Id, library.Revision, _ct);
        // Replayed publication cannot move a candidate back or duplicate it.
        var replay = await f.Assets.PublishReelAsync(f.Project.Id, candidate, _ct);
        Assert.Equal(library.Revision, replay.Revision); Assert.Equal(other.Id, Assert.Single(replay.Reels).AssetId);
        library = await f.Assets.DeleteAssetAsync(f.Project.Id, owner.Id, library.Revision, _ct);
        Assert.Single(library.Reels);
        library = await f.Assets.DeleteAssetAsync(f.Project.Id, other.Id, library.Revision, _ct);
        Assert.Empty(library.Reels); Assert.Equal(other.Id, Assert.Single(library.ReelTrash).Owner.Id);
        library = await f.Assets.RestoreReelAsync(f.Project.Id, candidate.Id, library.Revision, _ct);
        Assert.Equal(other.Id, Assert.Single(library.Reels).AssetId);
        Assert.Equal(before, JsonSerializer.Serialize(library.Reels[0].Generation, AtomicJsonFile.Options));
        library = await f.Assets.EditReelDetailsAsync(f.Project.Id, library.Reels[0], "Renamed after move", "Edited guidance", library.Revision, _ct);
        Assert.Equal("Renamed after move", library.Reels[0].Name);
    }

    [Fact]
    public async Task VoiceMoveRetainsBytesOldBindingsAndExcerptThroughRecovery()
    {
        var f = Fixture(); var owner = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Source", Category = AssetCategory.Character };
        var target = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Destination", Category = AssetCategory.Character };
        var wrong = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Room", Category = AssetCategory.Environment };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [owner, target, wrong] }, 0, _ct);
        var bytes = Wav(); using var input = new MemoryStream(bytes);
        library = await f.Assets.AddVoiceAsync(f.Project.Id, owner.Id, input, "voice.wav", "Recorded voice", .25, 1, new(), library.Revision, _ct);
        var voice = library.Voices.Single(); var binding = new ShotVoiceBinding { AssetId = owner.Id, VoiceId = voice.Id, Start = voice.Start, Duration = voice.ExcerptDuration, Speaker = "Source" };
        library = await f.Assets.SetDefaultVoiceAsync(f.Project.Id, owner.Id, voice.Id, library.Revision, _ct);
        var shot = Ready(); shot.Voices.Add(binding); shot.Characters.Add(new(Guid.NewGuid(), "Source")); shot.Dialogue.Add(new() { Speaker = "Source", Text = "Hello." });
        var snapshot = Snapshot(f.Project.Id, shot); var snapshotJson = JsonSerializer.Serialize(snapshot, AtomicJsonFile.Options);
        await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.MoveVoiceAsync(f.Project.Id, owner.Id, voice.Id, wrong.Id, library.Revision, _ct));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Assets.MoveVoiceAsync(f.Project.Id, owner.Id, voice.Id, target.Id, library.Revision - 1, _ct));
        var manifest = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "assets.json");
        using (var locked = TestProjectDatabase.HoldReadOnly(Path.GetDirectoryName(manifest)!))
            await Assert.ThrowsAnyAsync<Exception>(() => f.Assets.MoveVoiceAsync(f.Project.Id, owner.Id, voice.Id, target.Id, library.Revision, _ct));
        Assert.Equal(owner.Id, (await f.Assets.LoadAsync(f.Project.Id, _ct)).Voices.Single().AssetId);
        Assert.Equal(voice.Id, (await f.Assets.LoadAsync(f.Project.Id, _ct)).Assets.Single(a => a.Id == owner.Id).DefaultVoiceId);
        library = await f.Assets.MoveVoiceAsync(f.Project.Id, owner.Id, voice.Id, target.Id, library.Revision, _ct);
        Assert.Null(library.Assets.Single(a => a.Id == owner.Id).DefaultVoiceId);
        Assert.Equal(target.Id, library.Voices[0].AssetId); Assert.Equal(owner.Id, library.Voices[0].StorageAssetId);
        Assert.True(library.Voices[0].Matches(binding)); Assert.False(library.Voices[0].Matches(binding with { AssetId = Guid.NewGuid() }));
        Assert.True(library.Voices[0].Matches(binding with { AssetId = target.Id }));
        Assert.Equal(voice.Start, library.Voices[0].Start); Assert.Equal(voice.ExcerptDuration, library.Voices[0].ExcerptDuration);
        library = await f.Assets.DeleteAssetAsync(f.Project.Id, owner.Id, library.Revision, _ct);
        var generator = new ComfyH3Video(null!, null!, f.Assets, f.Assets, f.Shots, new ProductionMediaTools());
        await generator.ValidateInputsAsync(snapshot, _ct);
        Assert.Equal(snapshotJson, JsonSerializer.Serialize(snapshot, AtomicJsonFile.Options));
        Assert.Equal(owner.Id, (await f.Shots.LoadAsync(f.Project.Id, _ct)).Shots[0].Voices[0].AssetId);
        await using (var audio = await f.Assets.OpenVoiceAsync(f.Project.Id, voice.Id, ct: _ct))
        { using var result = new MemoryStream(); await audio!.Content.CopyToAsync(result, _ct); Assert.Equal(bytes, result.ToArray()); }
        library = await f.Assets.DiscardVoiceAsync(f.Project.Id, voice.Id, library.Revision, _ct);
        Assert.True(library.VoiceTrash[0].Voice.Matches(binding));
        library = await f.Assets.RestoreVoicesAsync(f.Project.Id, [library.VoiceTrash[0].Id], library.Revision, _ct);
        await generator.ValidateInputsAsync(snapshot, _ct);
        Assert.Equal(target.Id, library.Voices[0].AssetId);
    }

    [Fact]
    public void LegacyMediaSerializationOmitsMoveMetadata()
    {
        var json = JsonSerializer.Serialize(new VoiceReference(), AtomicJsonFile.Options);
        Assert.DoesNotContain("storageAssetId", json); Assert.DoesNotContain("previousAssetIds", json);
        var reel = new AssetReferenceReel { Media = new(Guid.NewGuid(), new('A', 64), 8, 64, 64, 124, 24, 124 / 24d, false) };
        Assert.DoesNotContain("originalAssetId", JsonSerializer.Serialize(reel, AtomicJsonFile.Options));
    }
}
