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
    private readonly HyperFlowSettingsGenerator _generator = new();
    public H3HyperFlowSettingsTests()
    {
        Services.AddMudServices(); Services.AddSingleton<IVideoGenerator>(_generator);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }
    private IRenderedComponent<VideoSettingsPanel> Panel(AiSettings settings, Func<H3Settings, Task<bool>> save) =>
        Render<VideoSettingsPanel>(p => p.Add(c => c.Settings, settings).Add(c => c.SaveSettings, save));
    private static Task Click(IRenderedComponent<VideoSettingsPanel> view, string label) => view.InvokeAsync(() =>
        view.FindAll("button").Single(b => b.TextContent.Trim() == label).ClickAsync(new()));

    [Fact]
    public async Task HyperFlowSettingsAreStagedSavedAndReloadedWithoutChangingOtherCheckpoints()
    {
        var settings = new AiSettings(); H3Settings? saved = null;
        var view = Panel(settings, value => { saved = value; return Task.FromResult(true); });
        var section = view.Find("[data-preset='hyperflow']");
        Assert.Contains("Experimental", section.TextContent); Assert.Contains("two-time", section.TextContent);
        Assert.Contains("Not yet benchmarked", section.TextContent);
        await Click(view, "Refresh video models");
        var file = H3HyperFlow.Checkpoints[2];
        await view.InvokeAsync(() => view.Find("[aria-label='hyperflow checkpoint']").ChangeAsync(new() { Value = file }));
        Assert.Null(settings.H3.HyperFlowLora); Assert.Null(saved);
        await view.InvokeAsync(() => view.Find("form").TriggerEventAsync("onsubmit", EventArgs.Empty));
        Assert.NotNull(saved); Assert.NotSame(settings.H3, saved); Assert.Equal(file, saved.HyperFlowLora);
        Assert.Equal(settings.H3.TurboLora, saved.TurboLora); Assert.Equal(settings.H3.Turbo8StepLora, saved.Turbo8StepLora);
        Assert.Equal(settings.H3.Model, saved.Model); Assert.Null(settings.H3.HyperFlowLora);
        var reopened = Panel(settings with { H3 = saved }, _ => Task.FromResult(true));
        Assert.Equal(file, reopened.Find("[aria-label='hyperflow checkpoint']").GetAttribute("value"));
        Assert.Equal(1, _generator.Checks);
    }

    [Fact]
    public async Task HyperFlowCancelRestoresTheOriginalPreference()
    {
        var settings = new AiSettings { H3 = new() { HyperFlowLora = H3HyperFlow.Checkpoints[1] } };
        var saves = 0; var view = Panel(settings, _ => { saves++; return Task.FromResult(true); });
        await Click(view, "Refresh video models");
        await view.InvokeAsync(() => view.Find("[aria-label='hyperflow checkpoint']").ChangeAsync(new() { Value = H3HyperFlow.Checkpoints[3] }));
        await Click(view, "Cancel");
        Assert.Equal(settings.H3.HyperFlowLora, view.Find("[aria-label='hyperflow checkpoint']").GetAttribute("value"));
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task HyperFlowMissingManualSigmasShowsSetupNeededWithoutHidingItsSelection()
    {
        _generator.MissingManual = true;
        var settings = new AiSettings(); var view = Panel(settings, _ => Task.FromResult(true));
        await Click(view, "Refresh video models");
        var section = view.Find("[data-preset='hyperflow']");
        Assert.Contains("Setup needed", section.TextContent); Assert.Contains("ManualSigmas", section.TextContent);
        Assert.Contains("No automatic scheduler fallback", section.TextContent);
        Assert.Equal(H3HyperFlow.DefaultCheckpoint, view.Find("[aria-label='hyperflow checkpoint']").GetAttribute("value"));
        Assert.Null(settings.H3.HyperFlowLora);
    }

    // Discovery only. Any accidental generation from the settings panel fails the test.
    private sealed class HyperFlowSettingsGenerator : IVideoGenerator
    {
        public int Checks;
        public bool MissingManual;
        public Task<H3Configuration> CheckAsync(AiSettings settings, CancellationToken ct = default)
        {
            Checks++;
            return Task.FromResult(new H3Configuration(true, true, "Checked", [], [], [], [])
            {
                DiscoverySucceeded = true,
                Presets = H3Presets.Keys.Select(key => new H3PresetSetup(key,
                    key == H3HyperFlow.Key && MissingManual ? "ManualSigmas: update ComfyUI and refresh." : null,
                    key == H3HyperFlow.Key ? H3HyperFlow.Checkpoints : [])).ToArray()
            });
        }
        public Task PrepareAsync(VideoRun run, string directory, CancellationToken ct) => throw new NotSupportedException();
        public Task ValidateInputsAsync(VideoSnapshot snapshot, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> SubmitAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<ComfyExecutionUpdate> ObserveAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct) => throw new NotSupportedException();
        public Task<ShotTake> DownloadAsync(VideoRun run, VideoCandidate candidate, string directory, Func<string, Task> progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CancelAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(VideoRun run, VideoCandidate candidate, CancellationToken ct) => throw new NotSupportedException();
    }
}
