using System.Diagnostics;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;
public sealed partial class ShotTests
{
    [Theory]
    [InlineData("wav")] [InlineData("mp3")] [InlineData("flac")] [InlineData("m4a")] [InlineData("ogg")]
    public async Task StagedVoiceFormatsPreviewBeforeExplicitPublicationAndRetainOriginals(string format)
    {
        var f = Fixture(); var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Mira", Category = AssetCategory.Character };
        var d = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        var source = Path.Combine(_root, "source.wav"); await File.WriteAllBytesAsync(source, Wav(), _ct);
        var file = Path.Combine(_root, "recording." + format);
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", source, file }) info.ArgumentList.Add(arg);
        using (var p = Process.Start(info)!) { var error = p.StandardError.ReadToEndAsync(_ct); await p.WaitForExitAsync(_ct); Assert.True(p.ExitCode == 0, await error); }
        var bytes = await File.ReadAllBytesAsync(file, _ct);
        await using var input = new MemoryStream(bytes);
        var draft = await f.Assets.StageVoiceAsync(f.Project.Id, asset.Id, input, "Mira warm." + format, new(), _ct);
        Assert.Equal("Mira warm", draft.SuggestedName); Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, _ct)).Voices);
        await using (var preview = await f.Assets.OpenVoicePreviewAsync(f.Project.Id, draft.Id, new(), staged: true, ct: _ct))
        { Assert.NotNull(preview); Assert.Equal("audio/mpeg", preview.ContentType); Assert.True(preview.Content.Length > 0); }
        d = await f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Mira", .5, 1.5, d.Revision, _ct);
        Assert.Equal(draft.Id, d.Voices.Single().Id); Assert.Equal(1, d.Voices[0].ExcerptDuration);
        var retry = await f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Mira", .5, 1.5, 0, _ct); Assert.Equal(d.Revision, retry.Revision);
        await using var saved = await f.Assets.OpenVoiceAsync(f.Project.Id, draft.Id, ct: _ct);
        using var output = new MemoryStream(); await saved!.Content.CopyToAsync(output, _ct); Assert.Equal(bytes, output.ToArray());
        d = await f.Assets.DiscardVoiceAsync(f.Project.Id, draft.Id, d.Revision, _ct);
        Assert.Null(await f.Assets.OpenVoicePreviewAsync(f.Project.Id, draft.Id, new(), ct: _ct));
        await using var retained = await f.Assets.OpenVoicePreviewAsync(f.Project.Id, d.VoiceTrash[0].Id, new(), trash: true, ct: _ct); Assert.NotNull(retained);
        retry = await f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Mira", .5, 1.5, 0, _ct); Assert.Empty(retry.Voices);
        d = await f.Assets.RestoreVoicesAsync(f.Project.Id, [d.VoiceTrash[0].Id], d.Revision, _ct); Assert.Equal(draft.Id, d.Voices[0].Id);
    }
    [Fact]
    public async Task VoiceImportFailureConflictAndBoundsKeepDraftForRetry()
    {
        var f = Fixture(); var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Mira", Category = AssetCategory.Character };
        var d = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        using var input = new MemoryStream(Wav()); var draft = await f.Assets.StageVoiceAsync(f.Project.Id, asset.Id, input, "voice.wav", new(), _ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Mira", 0, 2, 0, _ct));
        foreach (var end in new[] { .5, 3, double.NaN }) await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Mira", 0, end, d.Revision, _ct));
        var manifest = Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "assets.json");
        using (var locked = TestProjectDatabase.HoldReadOnly(Path.GetDirectoryName(manifest)!))
            await Assert.ThrowsAnyAsync<Exception>(() => f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Mira", 0, 2, d.Revision, _ct));
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, _ct)).Voices);
        await using (var preview = await f.Assets.OpenVoicePreviewAsync(f.Project.Id, draft.Id, new(), staged: true, ct: _ct)) Assert.NotNull(preview);
        d = await f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Mira", 0, 2, d.Revision, _ct); Assert.Single(d.Voices);
    }
    [Fact]
    public async Task CancelAndExpiredImportsNeverPublishAndCleanupRemovesStaging()
    {
        var f = Fixture(); var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Mira", Category = AssetCategory.Character };
        var d = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        using var input = new MemoryStream(Wav()); var draft = await f.Assets.StageVoiceAsync(f.Project.Id, asset.Id, input, "voice.wav", new(), _ct);
        await f.Assets.CancelVoiceImportAsync(f.Project.Id, draft.Id, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Mira", 0, 2, d.Revision, _ct));
        input.Position = 0; draft = await f.Assets.StageVoiceAsync(f.Project.Id, asset.Id, input, "voice.wav", new(), _ct);
        _clock.Now = _clock.Now.AddDays(1);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Assets.CommitVoiceAsync(f.Project.Id, draft.Id, "Mira", 0, 2, d.Revision, _ct));
        Assert.Empty(await f.Assets.CleanupVoiceImportsAsync(_ct));
        Assert.Empty(Directory.GetDirectories(Path.Combine(await f.Files.DirectoryAsync(f.Project.Id, _ct), "voice-imports")));
        Assert.Empty((await f.Assets.LoadAsync(f.Project.Id, _ct)).Voices);
    }
}
