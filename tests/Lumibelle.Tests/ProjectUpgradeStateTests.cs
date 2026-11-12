using Bunit;
using lumibelle.Components.Projects;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class ProjectUpgradeStateTests : BunitContext
{
    [Fact]
    public async Task AnOlderBusyStatusCannotOverwriteANewerIdleStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lumibelle-upgrade-status-" + Guid.NewGuid().ToString("N"));
        var upgrades = new DelayedUpgrades();
        var ct = Xunit.TestContext.Current.CancellationToken;
        using var jobs = new AiJobCoordinator(new FileAiJobStore(directory, TimeProvider.System), new FakeAiSettingsStore(), [],
            TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        Services.AddMudServices(); JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IProjectUpgrades>(upgrades); Services.AddSingleton(jobs);
        try
        {
            var component = Render<ProjectUpgradeState>(p => p.Add(c => c.ProjectId, Guid.NewGuid()).Add(c => c.Name, "A film")
                .Add(c => c.Status, new ProjectUpgradeStatus(1, 2, ["Update documents"], 0, "Queued work")));
            await jobs.SetPausedAsync(AiBackend.ComfyUI, true, ct);
            await upgrades.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
            await jobs.SetPausedAsync(AiBackend.ComfyUI, false, ct);
            await upgrades.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
            await component.InvokeAsync(() => upgrades.Idle.SetResult(new(1, 2, ["Update documents"], 0, null)));
            component.WaitForAssertion(() => Assert.False(component.Find("button").HasAttribute("disabled")));
            await component.InvokeAsync(async () => { upgrades.Busy.SetResult(new(1, 2, ["Update documents"], 0, "Queued work")); await Task.Yield(); });
            component.WaitForAssertion(() => Assert.False(component.Find("button").HasAttribute("disabled")));
            component.WaitForAssertion(() => Assert.DoesNotContain("Queued work", component.Markup));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class DelayedUpgrades : IProjectUpgrades
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProjectUpgradeStatus> Busy { get; } = new();
        public TaskCompletionSource<ProjectUpgradeStatus> Idle { get; } = new();
        private int reads;
        public Task<ProjectUpgradeStatus> StatusAsync(Guid project, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref reads) == 1) { FirstStarted.SetResult(); return Busy.Task; }
            SecondStarted.SetResult(); return Idle.Task;
        }
        public Task UpgradeAsync(Guid project, IProgress<string>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
