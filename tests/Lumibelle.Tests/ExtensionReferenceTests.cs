using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData(TakeExtensionDirection.After, false)]
    [InlineData(TakeExtensionDirection.Before, true)]
    public async Task ExtensionReferencesAreLocalAndUsedByCompositionAndVideo(TakeExtensionDirection direction, bool empty)
    {
        using var f = await QueuedVideoFixture.Create(this, trimMedia: new TrimTestMedia());
        var asset = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Courtyard", Category = AssetCategory.Environment, PreservationGuidance = "Keep the stone walls." };
        var library = await f.Assets.SaveAsync(new() { ProjectId = f.Project.Id, Assets = [asset] }, 0, _ct);
        foreach (var color in new[] { new Rgb24(255, 0, 0), new Rgb24(0, 0, 255) }) {
            using var image = new Image<Rgb24>(32, 32, color); using var buffer = new MemoryStream();
            await image.SaveAsPngAsync(buffer, _ct); buffer.Position = 0;
            library = await f.Assets.AddImageAsync(f.Project.Id, asset.Id, buffer, new("view.png", [], AssetImageOrigin.Imported), library.Revision, _ct);
        }
        f.Shot.Images = [new() { AssetId = asset.Id, MediaId = library.Assets[0].Images[0].Id, Name = "Original view" }];
        f.Shot.SaveLosslessFrames = true; f.Adapter.RealFrames = true;
        await f.Shots.SaveAsync(f.Project.Id, [f.Shot], (await f.Shots.LoadAsync(f.Project.Id, _ct)).Revision, ct: _ct);
        var initial = await f.Capture(); await f.Worker.ExecuteAsync(await f.Claim(initial), initial.Snapshot, _ct);
        await f.Jobs.UpdateAsync(initial.Id, j => j with { State = AiJobState.Completed, LeaseId = null }, _ct);
        var source = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        var sourceJson = JsonSerializer.Serialize(source, AtomicJsonFile.Options);
        var draft = source.Snapshot.Shot.Copy();
        draft.Images = empty ? [] : [new() { AssetId = asset.Id, MediaId = library.Assets[0].Images[1].Id, Name = "New view" }];
        var references = new TakeExtensionReferences(draft, ShotReferences.Resolve(draft, library, new()), ShotLooks.Capture(draft, library));
        var options = new TakeExtensionOptions(source.Id, source.FrameCount, 1, "The camera moves around the courtyard.", [], null, Combine: false) { Direction = direction, References = references };
        var projects = new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) };
        var composer = new AiTextJobCapture(f.Settings, projects, f.Assets, null!, null!, shots: f.Shots);
        var composed = await composer.ComposeExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, options, new(AiBackend.OpenRouter, "mock-model", "Mock model"), false, _ct);
        var text = composed.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<PromptCompositionRequest>();
        Assert.Equal(draft.Images.Select(i => i.MediaId), text.Shot.Images.Select(i => i.MediaId));
        Assert.Equal(references.Guidance, text.Guidance);
        Assert.NotEmpty(text.MotionStills!);
        var submission = await f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, options, _ct);
        var request = submission.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(draft.Images.Select(i => i.MediaId), request.Snapshot.Shot.Images.Select(i => i.MediaId));
        Assert.Equal(text.Images.Select(i => i.Sha256), request.Inputs.Select(i => i.Sha256));
        Assert.Equal(text.Guidance, request.Snapshot.ReferenceGuidance);
        Assert.NotNull(request.Snapshot.Motion); Assert.NotEmpty(request.Snapshot.Motion.Files);
        await AiVideoJobPolicy.ValidatePreparedFilesAsync(request, await f.Shots.RunDirectoryAsync(f.Project.Id, submission.Id, _ct), _ct);
        var document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(sourceJson, JsonSerializer.Serialize(document.Takes.Single(t => t.Id == source.Id), AtomicJsonFile.Options));
        Assert.Equal(f.Shot.Images[0].MediaId, document.Shots.Single(s => s.Id == source.ShotId).Images[0].MediaId);
        Assert.Equal(draft.Images.Select(i => i.MediaId), document.Shots.Single(s => s.Id == request.OutputShotId).Images.Select(i => i.MediaId));
        if (!empty) {
            Assert.NotEqual(initial.Snapshot.Deserialize<AiVideoJobRequest>(AtomicJsonFile.Options)!.Inputs[0].Sha256, request.Inputs[0].Sha256);
            // Guidance drift between Apply and capture requires another review.
            var latest = await f.Assets.LoadAsync(f.Project.Id, _ct);
            latest.Assets[0] = latest.Assets[0] with { PreservationGuidance = "New shared guidance" };
            await f.Assets.SaveAsync(latest, latest.Revision, _ct);
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.CaptureService.CaptureExtensionAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, options, _ct));
        }
    }
}
