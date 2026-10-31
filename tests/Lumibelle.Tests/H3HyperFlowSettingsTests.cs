using Bunit;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class H3HyperFlowSettingsTests : BunitContext
{
    public H3HyperFlowSettingsTests()
    {
        Services.AddMudServices(); Services.AddSingleton<IVideoGenerator>(new CurrentSettingsGenerator());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }
    [Fact]
    public async Task RemovedRecipesHaveNoInstallOrSelectionControlsAndSavingKeepsOrdinaryModels()
    {
        var settings = new AiSettings { H3 = new() { HyperFlowLora = H3HyperFlow.DefaultCheckpoint, TenErosCompactModel = "sub/" + H3AcceleratedCheckpoints.CompactFile, SmdBaseModel = "base/" + H3SmdRecipe.BaseFile, SmdLora = "smd/" + H3SmdRecipe.AdapterFile, FastH3Model = "fast/" + H3AcceleratedCheckpoints.FastFile, PtdBaseModel = "ptd-base/" + H3SmdRecipe.BaseFile, PtdAdapter = "ptd/" + H3PtdRecipe.AdapterFile } }; H3Settings? saved = null;
        var view = Render<VideoSettingsPanel>(p => p.Add(c => c.Settings, settings)
            .Add(c => c.SaveSettings, value => { saved = value; return Task.FromResult(true); }));
        view.WaitForAssertion(() => Assert.NotEmpty(view.FindAll("[data-preset='standard']")));
        foreach (var key in new[] { "spectrum", "turbo8", "turbo4-075", "hyperflow" }) Assert.Empty(view.FindAll($"[data-preset='{key}']"));
        Assert.Empty(view.FindAll("[data-requirement='hyperflow-lora'],[data-requirement='turbo8-lora'],[data-requirement='spectrum-nodes']"));
        Assert.Contains("reset to Standard", view.Markup);
        await view.InvokeAsync(() => view.Find("form").TriggerEventAsync("onsubmit", EventArgs.Empty));
        Assert.NotNull(saved); Assert.Equal(settings.H3.Model, saved.Model); Assert.Equal(settings.H3.Encoder, saved.Encoder);
        Assert.Equal(settings.H3.TurboLora, saved.TurboLora); Assert.Equal(settings.H3.TenErosCompactModel, saved.TenErosCompactModel); Assert.Equal(settings.H3.SmdBaseModel, saved.SmdBaseModel); Assert.Equal(settings.H3.SmdLora, saved.SmdLora); Assert.Equal(settings.H3.FastH3Model, saved.FastH3Model); Assert.Equal(settings.H3.PtdBaseModel, saved.PtdBaseModel); Assert.Equal(settings.H3.PtdAdapter, saved.PtdAdapter);
    }
    private sealed class CurrentSettingsGenerator : IVideoGenerator
    {
        public Task<H3Configuration> CheckAsync(AiSettings settings, CancellationToken ct = default) => Task.FromResult(new H3Configuration(true, true, "Checked", [], [], [], []) {
            DiscoverySucceeded = true, InstalledModels = [settings.H3.Model], InstalledEncoders = [settings.H3.Encoder],
            InstalledVaes = [settings.H3.VideoVae, settings.H3.AudioVae],
            Presets = H3Presets.Offered.Select(key => new H3PresetSetup(key, null, [])).ToArray()
        });
        public Task PrepareAsync(VideoRun run, string directory, CancellationToken ct) => throw new NotSupportedException();
        public Task ValidateInputsAsync(VideoSnapshot snapshot, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> SubmitAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct) => throw new NotSupportedException();
        public Task<ShotTake> DownloadAsync(VideoRun run, VideoCandidate candidate, string directory, Func<string, Task> progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CancelAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct) => throw new NotSupportedException();
    }
}
