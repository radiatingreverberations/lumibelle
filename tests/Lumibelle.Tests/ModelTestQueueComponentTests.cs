using Bunit;
using lumibelle.Components;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Lumibelle.Tests;

public sealed partial class AiSettingsComponentTests
{
    [Fact]
    public async Task ModelTestSurvivesDisposalAndReopensFromActivityWithoutRegeneration()
    {
        const string model = "queued-model.safetensors";
        _providers.Models = [new(model, "Queued model")];
        _providers.VerificationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _providers.TestResponse = "Durable response, <not markup>.";
        var dialog = TestDialog(model, advanced: true);
        dialog.Find("#advanced-test-prompt").Input("Captured message");
        await dialog.InvokeAsync(() => dialog.FindAll("button").Single(b => b.TextContent.Trim() == "Run advanced test").ClickAsync(new()));
        dialog.WaitForAssertion(() => Assert.Equal(1, _providers.VerificationCalls));
        var queue = Services.GetRequiredService<AiJobCoordinator>(); var job = Assert.Single(queue.View.Jobs);
        Assert.False(dialog.FindAll("button").Single(b => b.TextContent.Trim() == "Close").HasAttribute("disabled"));
        await dialog.Instance.DisposeAsync(); _testHost!.Dispose(); _testHost = null;
        Assert.False(_providers.VerificationToken.IsCancellationRequested);
        _providers.VerificationRelease.SetResult();
        var host = _testHost = Render<MudDialogProvider>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (queue.View.Jobs.Single().State != AiJobState.Completed) await Task.Delay(10, deadline.Token);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"/settings/ai?jobId={job.Id}");
        var page = Render<AiSettingsPage>();
        host.WaitForAssertion(() => Assert.Contains(_providers.TestResponse, host.Find(".advanced-model-test-result pre").TextContent));
        Assert.Equal("Captured message", host.Find("#advanced-test-prompt").GetAttribute("value"));
        Assert.Empty(host.FindAll(".advanced-model-test-result not"));
        Assert.Equal(1, _providers.VerificationCalls);
        host.WaitForAssertion(() => Assert.False(queue.View.Jobs.Single().Unread));
    }

    [Fact]
    public async Task PausedModelTestRestoresCapturedComposerAndPreventsDuplicateTests()
    {
        const string model = "queued-model.safetensors";
        _providers.Models = [new(model, "Queued model")];
        var queue = Services.GetRequiredService<AiJobCoordinator>();
        await queue.SetPausedAsync(AiBackend.ComfyUI, true, Xunit.TestContext.Current.CancellationToken);
        var first = TestDialog(model, advanced: true);
        first.Find("#advanced-test-prompt").Input("Captured while waiting"); first.Find("#advanced-test-tokens").Input("192");
        await first.InvokeAsync(() => first.FindAll("button").Single(b => b.TextContent.Trim() == "Run advanced test").ClickAsync(new()));
        first.WaitForAssertion(() => Assert.Contains("This provider queue is paused", first.Markup));
        var job = Assert.Single(queue.View.Jobs);
        Assert.Equal(0, _providers.VerificationCalls);
        await first.InvokeAsync(() => first.FindAll("button").Single(b => b.TextContent.Trim() == "Close").ClickAsync(new()));
        var reopened = TestDialog(model, advanced: true);
        reopened.WaitForAssertion(() => Assert.Equal("Captured while waiting", reopened.Find("#advanced-test-prompt").GetAttribute("value")));
        Assert.Equal("192", reopened.Find("#advanced-test-tokens").GetAttribute("value"));
        Assert.True(reopened.Find("#advanced-test-prompt").HasAttribute("disabled"));
        Assert.True(reopened.FindAll("button").Single(b => b.TextContent.Trim() == "Run advanced test").HasAttribute("disabled"));
        await reopened.InvokeAsync(() => reopened.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel test").ClickAsync(new()));
        reopened.WaitForAssertion(() => Assert.Contains("Model test cancelled", reopened.Markup));
        await queue.SetPausedAsync(AiBackend.ComfyUI, false, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(AiJobState.Cancelled, queue.View.Jobs.Single(j => j.Id == job.Id).State);
        Assert.Equal(0, _providers.VerificationCalls);
    }
}
