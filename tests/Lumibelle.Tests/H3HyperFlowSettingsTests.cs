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
    private const string Row = "[data-requirement='hyperflow-lora']";
    private static async Task Choose(IRenderedComponent<VideoSettingsPanel> view, string file)
    {
        await view.InvokeAsync(() => view.Find(Row + " .video-requirement-links button").ClickAsync(new()));
        await view.InvokeAsync(() => view.Find("[aria-label='HyperFlow ComfyUI conversion file']").ChangeAsync(new() { Value = file }));
    }
    private static string Shown(IRenderedComponent<VideoSettingsPanel> view) => view.Find(Row + " .video-requirement-file").TextContent;

    [Fact]
    public async Task HyperFlowSettingsAreStagedSavedAndReloadedWithoutChangingOtherCheckpoints()
    {
        var settings = new AiSettings(); H3Settings? saved = null;
        var view = Panel(settings, value => { saved = value; return Task.FromResult(true); });
        view.WaitForAssertion(() => Assert.Equal(1, _generator.Checks));
        var section = view.Find("[data-preset='hyperflow']");
        Assert.Contains("Experimental", section.TextContent); Assert.Contains("two-time", section.TextContent);
        Assert.Contains("Not yet benchmarked", section.TextContent);
        var file = H3HyperFlow.Checkpoints[2];
        await Choose(view, file);
        Assert.Equal(file, Shown(view)); Assert.Null(settings.H3.HyperFlowLora); Assert.Null(saved);
        await view.InvokeAsync(() => view.Find("form").TriggerEventAsync("onsubmit", EventArgs.Empty));
        Assert.NotNull(saved); Assert.NotSame(settings.H3, saved); Assert.Equal(file, saved.HyperFlowLora);
        Assert.Equal(settings.H3.TurboLora, saved.TurboLora); Assert.Equal(settings.H3.Turbo8StepLora, saved.Turbo8StepLora);
        Assert.Equal(settings.H3.Model, saved.Model); Assert.Null(settings.H3.HyperFlowLora);
        // Saving checks again, so the statuses describe what was saved.
        Assert.Equal(2, _generator.Checks);
        var reopened = Panel(settings with { H3 = saved }, _ => Task.FromResult(true));
        Assert.Equal(file, Shown(reopened));
    }

    [Fact]
    public async Task HyperFlowCancelRestoresTheOriginalPreference()
    {
        var settings = new AiSettings { H3 = new() { HyperFlowLora = H3HyperFlow.Checkpoints[1] } };
        var saves = 0; var view = Panel(settings, _ => { saves++; return Task.FromResult(true); });
        view.WaitForAssertion(() => Assert.Equal(1, _generator.Checks));
        await Choose(view, H3HyperFlow.Checkpoints[3]);
        await Click(view, "Cancel");
        Assert.Equal(settings.H3.HyperFlowLora, Shown(view));
        Assert.Equal(0, saves);
    }

    [Fact]
    public void HyperFlowMissingManualSigmasShowsSetupNeededWithoutHidingItsSelection()
    {
        _generator.MissingManual = true;
        var settings = new AiSettings(); var view = Panel(settings, _ => Task.FromResult(true));
        view.WaitForAssertion(() => Assert.Contains("Setup needed", view.Find("[data-preset='hyperflow']").TextContent));
        var section = view.Find("[data-preset='hyperflow']");
        Assert.Contains("ManualSigmas", section.TextContent); Assert.Contains("No automatic scheduler fallback", section.TextContent);
        Assert.Equal(H3HyperFlow.DefaultCheckpoint, Shown(view));
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
            var h3 = settings.H3;
            return Task.FromResult(new H3Configuration(true, true, "Checked", [], [], [], [])
            {
                DiscoverySucceeded = true, InstalledModels = [h3.Model], InstalledEncoders = [h3.Encoder], InstalledVaes = [h3.VideoVae, h3.AudioVae],
                InstalledLoras = [.. H3HyperFlow.Checkpoints],
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
