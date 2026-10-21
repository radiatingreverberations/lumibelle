using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    private ScriptAssistantRequest ScriptWithImages(Guid id, params AssetImageReference[] images) => new(
        new() { Id = id, Operation = WritingOperation.Revise, Backend = _model.Backend, Model = _model.Model,
            Target = ScriptStructure.Capture(_script, ScriptScope.Document, null), Instructions = "Use the selected images." }, _script, [], _model)
        { ProjectContext = "Selected shot dialogue: Hello.", Images = images };

    [Fact]
    public async Task ScriptCapturesOrderedImagesAndDescriptionsAndExecutesAfterOriginalsAreRemoved()
    {
        _assets.Library = _assets.Library with { Assets = [_asset with { Images = [_asset.Images[0] with { Name = "Base" }, _asset.Images[1] with { Name = "Raincoat" }] }] };
        var id = Guid.NewGuid();
        var request = ScriptWithImages(id, new(_asset.Id, _asset.Images[1].Id), new(_asset.Id, _asset.Images[0].Id));
        var submission = await Capture.ScriptAsync(id, Guid.NewGuid(), request, true, _ct);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.True(captured.InspectsImages);
        Assert.Equal(request.Images, captured.Payload<ScriptAssistantRequest>().Images);
        var parts = captured.Messages.Single(m => m.Parts.Any(p => p.Image is not null)).Parts;
        Assert.Contains("Image 1 · Raincoat", parts[1].Text);
        Assert.Contains("Shared identity notes", parts[1].Text);
        Assert.Contains("Image 2 · Base", parts[3].Text);
        Assert.Equal(2, parts.Count(p => p.Image is not null));
        Assert.Contains(captured.Messages.SelectMany(m => m.Parts), p => p.Text?.Contains("Selected shot dialogue: Hello.") == true);

        _assets.Library = _assets.Library with { Assets = [] };
        _providers.Chat.Output = Response(AiJobKind.ScriptAssistant);
        var outcome = await Handler().ExecuteAsync(await Claim(submission), submission.Snapshot, _ct);
        Assert.Equal(AiJobState.Completed, outcome.State);
        Assert.Equal(2, _providers.Chat.Messages.SelectMany(m => m.Contents).OfType<DataContent>().Count());
        Assert.Equal(2, _assets.ImageOpenCalls);
        Assert.Equal("A mouse listens to rain.", _script.Blocks[1].Text);
    }

    [Fact]
    public async Task MissingWritingImageIsRejectedBeforeQueueing()
    {
        var id = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => Capture.ScriptAsync(id, Guid.NewGuid(),
            ScriptWithImages(id, new AssetImageReference(_asset.Id, Guid.NewGuid())), true, _ct));
        Assert.Contains("unavailable", error.Message);
        Assert.Equal(0, _providers.Created);
    }

    [Fact]
    public async Task ScriptDoesNotSilentlyDropImagesForTextOnlyModels()
    {
        _providers.Vision = false;
        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, providers: _providers);
        var id = Guid.NewGuid();
        var request = ScriptWithImages(id, new AssetImageReference(_asset.Id, _asset.Images[0].Id));
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => capture.ScriptAsync(id, Guid.NewGuid(), request, true, _ct));
        Assert.Contains("vision model", error.Message);
        Assert.Equal(0, _assets.ImageOpenCalls);
        var textOnly = await capture.ScriptAsync(id, Guid.NewGuid(), request with { Images = [] }, true, _ct);
        Assert.False(textOnly.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.InspectsImages);
    }

    [Fact]
    public async Task DirectScriptAssistantAlsoInspectsSelectedImages()
    {
        _providers.Chat.Output = Response(AiJobKind.ScriptAssistant);
        var assistant = new ScriptAssistant(_providers, _settings, _assets);
        await foreach (var _ in assistant.GenerateAsync(ScriptWithImages(Guid.NewGuid(), new AssetImageReference(_asset.Id, _asset.Images[0].Id)), _ct)) { }
        Assert.Single(_providers.Chat.Messages.SelectMany(m => m.Contents).OfType<DataContent>());
    }
}
