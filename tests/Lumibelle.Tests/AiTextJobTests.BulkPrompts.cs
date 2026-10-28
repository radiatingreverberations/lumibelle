using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    [Theory]
    [InlineData(ImageWorkflow.Krea2, false)] [InlineData(ImageWorkflow.Krea2, true)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, false)] [InlineData(ImageWorkflow.Flux2Klein9bKv, true)]
    [InlineData(ImageWorkflow.MossImage, false)] [InlineData(ImageWorkflow.MossImage, true)]
    [InlineData(ImageWorkflow.Flux3ImageKlein, false)] [InlineData(ImageWorkflow.Flux3ImageKlein, true)]
    [InlineData(ImageWorkflow.QwenImage21, false)] [InlineData(ImageWorkflow.QwenImage21, true)]
    [InlineData(ImageWorkflow.Fibo2, false)] [InlineData(ImageWorkflow.Fibo2, true)]
    [InlineData(ImageWorkflow.ZImage, false)]
    public async Task BulkEnhancementUsesTheSingleImageGuideAndKeepsItsOwnJobTarget(ImageWorkflow workflow, bool edit)
    {
        var initial = Enhancement();
        var request = initial with { Context = initial.Context with { Workflow = workflow, IsEdit = edit,
            TargetLook = lumibelle.Services.Assets.LookPolicy.Capture(_asset, null),
            References = edit ? initial.Context.References.Take(1).ToArray() : [],
            VisualStyle = new() { Preset = VisualStylePreset.Photographic }, AspectRatio = "9:16" } };
        var capture = new AiTextJobCapture(_settings, _projects, _assets, Enhancer, Guidance, providers: _providers);
        var bulk = await capture.EnhanceAssetBatchAsync(Guid.NewGuid(), Guid.NewGuid(), request, _settings.Value, _ct);
        var single = await capture.EnhanceAsync(Guid.NewGuid(), Guid.NewGuid(), request, _ct);
        var snapshot = bulk.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        var original = single.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(original.Profile, snapshot.Profile);
        Assert.Equal(JsonSerializer.Serialize(original.Messages), JsonSerializer.Serialize(snapshot.Messages));
        Assert.NotEqual(bulk.Target.LockKey(bulk.Kind), single.Target.LockKey(single.Kind));
        bulk.Target.Validate(bulk.Kind);
        var response = workflow == ImageWorkflow.Fibo2
            ? """{"short_description":"Photographic green robot mouse_token","edit_instruction":"Preserve identity"}"""
            : "Photographic green robot mouse_token";
        _providers.Chat.Output = JsonSerializer.Serialize(new { kind = "Prompt", text = response });
        var claimed = await Claim(bulk);
        var outcome = await Handler().ExecuteAsync(claimed, bulk.Snapshot, _ct);
        Assert.Equal(AiJobState.Completed, outcome.State); Assert.False(outcome.Reviewable);
        var result = await Store.ReadArtifactAsync<AiTextJobResult>(bulk.Id, AiJobArtifact.Result, _ct);
        Assert.Null(result!.Error); Assert.Equal(response, result.Read<PromptEnhancementResult>()!.Text);
        Assert.Equal(0, _assets.SaveCalls);
        Assert.Contains("assetBatchReview=", claimed.Job.ReviewUrl);
    }
}
