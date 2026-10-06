using System.Text.Json;
using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components.Web;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    private IRenderedComponent<GuidanceSuggestion> Guidance(GuidanceContext context, Action<string>? apply = null)
    {
        _dialogs ??= Render<MudBlazor.MudDialogProvider>();
        return Render<GuidanceSuggestion>(p => p.Add(c => c.Context, context).Add(c => c.Library, _assets.Library)
            .Add(c => c.Apply, (string value) => apply?.Invoke(value)));
    }
    private async Task StartGuidance(IRenderedComponent<GuidanceSuggestion> component)
    {
        await component.InvokeAsync(() => component.Find(".assist-trigger").Click());
        _dialogs.WaitForAssertion(() => Assert.False(_dialogs.FindAll(".ai-assist-dialog button").Single(b => b.TextContent.Trim() == "Suggest").HasAttribute("disabled")));
        await _dialogs.InvokeAsync(() => _dialogs.FindAll(".ai-assist-dialog button").Single(b => b.TextContent.Trim() == "Suggest").ClickAsync(new MouseEventArgs()));
        component.WaitForAssertion(() => Assert.Contains(component.FindAll("button"), b => b.ClassList.Contains("request-action-button")));
        await component.InvokeAsync(() => component.FindAll("button").Single(b => b.ClassList.Contains("request-action-button")).ClickAsync(new()));
    }
    [Fact]
    public async Task GuidanceSurvivesClosingAndDisposalAndReopeningNeverSubmitsAgain()
    {
        var asset = Asset("Mira"); _assets.Library = _assets.Library with { Assets = [asset] };
        var context = GuidanceContext.From(new(_projectId, asset.Id, GuidanceScope.CharacterIdentity), asset)!;
        var component = Guidance(context); await StartGuidance(component);
        await _guidance.Called();
        var job = Assert.Single(_queue.View.Jobs);
        await _dialogs.InvokeAsync(() => _dialogs.FindAll(".guidance-dialog button").Single(b => b.TextContent.Trim() == "Close").ClickAsync(new()));
        await component.Instance.DisposeAsync(); component.Dispose();
        Assert.False(_guidance.Token.IsCancellationRequested);
        var reopened = Guidance(context);
        reopened.WaitForAssertion(() => Assert.Contains("View request", reopened.Markup));
        await reopened.InvokeAsync(() => reopened.FindAll("button").Single(b => b.ClassList.Contains("request-action-button")).Click());
        Assert.Single(_guidance.Requests);
        _guidance.Release.TrySetResult();
        _dialogs.WaitForAssertion(() => Assert.False(_dialogs.FindAll(".guidance-dialog button").Single(b => b.TextContent.Trim() == "Apply changes").HasAttribute("disabled")));
        Assert.Equal(AiJobState.Completed, Assert.Single(_queue.View.Jobs).State);
        Assert.False((await _jobs.ReadAsync(Xunit.TestContext.Current.CancellationToken)).Jobs.Single(j => j.Id == job.Id).CancelRequested);
    }
    [Fact]
    public async Task GuidanceLocksOnlyItsFieldAndCancellingWaitingWorkDoesNotContactTheProvider()
    {
        await _queue.SetPausedAsync(AiBackend.OpenRouter, true, Xunit.TestContext.Current.CancellationToken);
        var asset = Asset("Mira") with { Images = [new AssetImage { Id = Guid.NewGuid(), FileName = "face.png", ContentType = "image/png", Width = 80, Height = 40 }] }; _assets.Library = _assets.Library with { Assets = [asset] };
        var first = Guidance(GuidanceContext.From(new(_projectId, asset.Id, GuidanceScope.CharacterIdentity), asset)!);
        await StartGuidance(first);
        await _dialogs.InvokeAsync(() => _dialogs.FindAll(".guidance-dialog button").Single(b => b.TextContent.Trim() == "Close").ClickAsync(new()));
        var second = Guidance(GuidanceContext.From(new(_projectId, asset.Id, GuidanceScope.Image, ImageId: asset.Images[0].Id), asset)!);
        await StartGuidance(second);
        Assert.Equal(2, _queue.View.Jobs.Count); Assert.Empty(_guidance.Requests);
        await _dialogs.InvokeAsync(() => _dialogs.FindAll(".guidance-dialog button").Single(b => b.TextContent.Trim() == "Cancel suggestion").ClickAsync(new()));
        second.WaitForAssertion(() => Assert.Contains("cancelled", _dialogs.Markup));
        Assert.Single(_queue.View.Jobs, j => j.LocksTarget); Assert.Empty(_guidance.Requests);
    }
    [Fact]
    public async Task GuidanceKeepsEditedSuggestionAcrossQueueUpdatesAndRechecksSavedTargetBeforeApply()
    {
        _guidance.Release.TrySetResult();
        var asset = Asset("Mira"); _assets.Library = _assets.Library with { Assets = [asset] };
        string? applied = null;
        var component = Guidance(GuidanceContext.From(new(_projectId, asset.Id, GuidanceScope.CharacterIdentity), asset)!, value => applied = value);
        await StartGuidance(component);
        _dialogs.WaitForAssertion(() => Assert.False(_dialogs.FindAll(".guidance-dialog button").Single(b => b.TextContent.Trim() == "Apply changes").HasAttribute("disabled")));
        await _dialogs.InvokeAsync(() => _dialogs.Find(".guidance-comparison label:last-child textarea").Input("Reviewed wording."));
        await _queue.SetPausedAsync(AiBackend.OpenRouter, true, Xunit.TestContext.Current.CancellationToken);
        _dialogs.WaitForAssertion(() => Assert.Equal("Reviewed wording.", _dialogs.Find(".guidance-comparison label:last-child textarea").GetAttribute("value")));
        _assets.Library = _assets.Library with { Assets = [asset with { Description = "Changed by another author." }] };
        await _dialogs.InvokeAsync(() => _dialogs.FindAll(".guidance-dialog button").Single(b => b.TextContent.Trim() == "Apply changes").ClickAsync(new()));
        // A saved change made elsewhere is named first; the reviewed wording can then be applied anyway.
        _dialogs.WaitForAssertion(() => Assert.Contains("Since this suggestion was written, the notes changed. It may not match the character as it is now.", _dialogs.Markup));
        Assert.Contains("reload Assets", _dialogs.Markup);
        Assert.Null(applied); Assert.Single(_guidance.Requests);
        await _dialogs.InvokeAsync(() => _dialogs.FindAll(".guidance-dialog button").Single(b => b.TextContent.Trim() == "Apply anyway").ClickAsync(new()));
        _dialogs.WaitForAssertion(() => Assert.Equal("Reviewed wording.", applied));
    }
}

internal sealed class ComponentGuidanceHandler : IAiJobHandler
{
    public List<GuidanceRequest> Requests { get; } = [];
    private readonly CallCount _calls = new();
    public Task Called(int times = 1) => _calls.Reached(times);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken Token { get; private set; }
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.Guidance];
    public async Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        Requests.Add(snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<GuidanceRequest>()); Token = ct; _calls.Increment();
        await context.ReportAsync(new(new(GenerationPhase.Generating, "Suggesting guidance")));
        await Release.Task.WaitAsync(ct);
        await context.SaveResultAsync(new AiTextJobResult("Saved mock guidance", true, "stop",
            JsonSerializer.SerializeToElement(new PromptEnhancementResult(PromptEnhancementKind.Prompt, "Preserve the described identity."), AtomicJsonFile.Options)));
        return AiJobOutcome.Complete();
    }
    public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(AiJobOutcome.Attention("Interrupted", AiJobRecovery.GenerateAgain));
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(true);
}
