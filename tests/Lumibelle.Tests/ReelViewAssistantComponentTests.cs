using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task ReelViewSubmissionShowsPreparationOnFirstClickAndRejectsRepeatClicks()
    {
        var providers = (FakeProviders)Services.GetRequiredService<IAiProviderRegistry>();
        providers.Models = [new("test/model", "Vision model", SupportsImages: true)];
        _dialogs = Render<MudBlazor.MudDialogProvider>();
        var media = new ReferenceVideoMedia(Guid.NewGuid(), new('a', 64), 100, 640, 360, 360, 24, 15, false);
        var ui = Render<ReelViewAssistant>(p => p.Add(c => c.ProjectId, _projectId).Add(c => c.Media, media).Add(c => c.Current, new ReelKeyframeSet()));
        await ui.Find(".request-action-button").ClickAsync(new());
        _dialogs.WaitForAssertion(() => Assert.False(_dialogs.Find(".ai-assist-action").HasAttribute("disabled")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _assets.BeforeLoad = async () => {
            calls++; entered.TrySetResult();
            await release.Task;
            throw new WorkspaceStoreException("Fixture preparation failed");
        };
        var click = _dialogs.Find(".ai-assist-action").ClickAsync(new());
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Xunit.TestContext.Current.CancellationToken);
            _dialogs.WaitForAssertion(() => {
                var button = _dialogs.Find(".ai-assist-action");
                Assert.True(button.HasAttribute("disabled"));
                Assert.Contains("Preparing views", button.TextContent);
            });
            ui.WaitForAssertion(() => Assert.Contains("Preparing request", ui.Find(".request-action-button").TextContent));
            // A second activation while pictures are being prepared must not start another capture.
            await _dialogs.Find(".ai-assist-action").ClickAsync(new());
            Assert.Equal(1, calls);
        } finally { release.TrySetResult(); await click; }
        ui.WaitForAssertion(() => Assert.Contains("Fixture preparation failed", ui.Markup));
        _dialogs.WaitForAssertion(() => Assert.Contains("Fixture preparation failed", _dialogs.Find("[role=alert]").TextContent));
        _dialogs.WaitForAssertion(() => Assert.False(_dialogs.Find(".ai-assist-action").HasAttribute("disabled")));
    }
}
