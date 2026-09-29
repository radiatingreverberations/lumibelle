using System.Runtime.CompilerServices;
using System.Text.Json;
using Bunit;
using lumibelle.Components.Assets;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task HandledEnhancementLinkDoesNotSurviveSettingsRoundTrip()
    {
        var asset = Asset("Mouse", "Mouse prompt");
        _assets.Library = _assets.Library with { Assets = [asset] };
        var page = Page();
        await StartEnhancement(page); EnhancementReady();
        var jobId = _queue.View.Jobs.Single().Id;
        await _dialogs.Find("[aria-label='Close prompt enhancement']").ClickAsync(new());

        var navigation = Services.GetRequiredService<NavigationManager>();
        var destination = $"/projects/{_projectId:D}/assets#references";
        var reviewLink = $"/projects/{_projectId:D}/assets?assetId={asset.Id:D}&jobId={jobId:D}#references";
        await page.InvokeAsync(() => navigation.NavigateTo(navigation.ToAbsoluteUri(reviewLink).AbsoluteUri));
        await page.InvokeAsync(() => page.Render(p => p.Add(x => x.RequestedJobId, jobId).Add(x => x.RequestedAssetId, asset.Id)));
        _dialogs.WaitForElement("#enhancement-original");
        await _dialogs.Find("[aria-label='Close prompt enhancement']").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Equal(destination, navigation.ToAbsoluteUri(navigation.Uri).PathAndQuery + navigation.ToAbsoluteUri(navigation.Uri).Fragment));
        var settingsLink = AiSettingsNavigation.Link(navigation);
        var returnUrl = QueryHelpers.ParseQuery(navigation.ToAbsoluteUri(settingsLink).Query)["returnUrl"].ToString();
        Assert.Equal(destination, returnUrl);
        await page.InvokeAsync(() => navigation.NavigateTo(navigation.ToAbsoluteUri(settingsLink).AbsoluteUri));
        page.Dispose();
        navigation.NavigateTo(navigation.ToAbsoluteUri(returnUrl).AbsoluteUri);
        page = Page(false);
        page.WaitForAssertion(() => Assert.Contains("Review changes", page.Find(".prompt-enhancement").TextContent));
        Assert.Empty(_dialogs.FindAll("#enhancement-original"));

        // A deliberate Activity link to this same request must still work.
        await page.InvokeAsync(() => navigation.NavigateTo(navigation.ToAbsoluteUri(reviewLink).AbsoluteUri));
        await page.InvokeAsync(() => page.Render(p => p.Add(x => x.RequestedJobId, jobId).Add(x => x.RequestedAssetId, asset.Id)));
        _dialogs.WaitForElement("#enhancement-original");
        Assert.Single(_queue.View.Jobs);
    }

    [Fact]
    public async Task DismissedEnhancementLinkDoesNotReopenWhenReturningToAsset()
    {
        var mouse = Asset("Mouse", "Mouse prompt"); var room = Asset("Room", "A small room");
        _assets.Library = _assets.Library with { Assets = [mouse, room] };
        var page = Page();
        await StartEnhancement(page); EnhancementReady();
        var jobId = _queue.View.Jobs.Single().Id;
        await _dialogs.Find("[aria-label='Close prompt enhancement']").ClickAsync(new());

        // An Activity link opens the saved review once.
        await page.InvokeAsync(() => page.Render(p => p.Add(x => x.RequestedJobId, jobId)));
        _dialogs.WaitForElement("#enhancement-original");
        await _dialogs.Find("[aria-label='Close prompt enhancement']").ClickAsync(new());
        for (var visit = 0; visit < 2; visit++)
        {
            await page.FindAll(".asset-choice").Single(b => b.TextContent.Contains("Room")).ClickAsync(new());
            await page.FindAll(".asset-choice").Single(b => b.TextContent.Contains("Mouse")).ClickAsync(new());
            page.WaitForAssertion(() => Assert.Contains("Review changes", page.Find(".prompt-enhancement").TextContent));
            Assert.Empty(_dialogs.FindAll("#enhancement-original"));
        }

        // Dismissal must not prevent deliberately reopening the saved result.
        await page.Find(".prompt-enhancement .request-action-button").ClickAsync(new());
        _dialogs.WaitForElement("#enhancement-original");
        await _dialogs.Find("[aria-label='Close prompt enhancement']").ClickAsync(new());
        await page.InvokeAsync(() => page.Render(p => p.Add(x => x.RequestedJobId, (Guid?)null)));
        await page.InvokeAsync(() => page.Render(p => p.Add(x => x.RequestedJobId, jobId)));
        _dialogs.WaitForElement("#enhancement-original");
        Assert.Single(_queue.View.Jobs);
    }

    [Fact]
    public async Task EnhancementPreviewAppliesOnlyPromptAndUndoExpiresAfterTypingOrContextChange()
    {
        var enhancer = (FakePromptEnhancer)Services.GetRequiredService<IPromptEnhancer>();
        _assets.Library = _assets.Library with { Assets = [Asset("Mouse") with { Description = "Original prompt" }] };
        var page = Page();
        await StartEnhancement(page);
        EnhancementReady();
        Assert.Equal("Original prompt", page.Find("#image-prompt").GetAttribute("value"));
        await _dialogs.InvokeAsync(() => _dialogs.Find("#enhancement-suggestion").Input("An edited suggestion"));
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").ClickAsync(new()));
        page.WaitForAssertion(() => Assert.Equal("An edited suggestion", page.Find("#image-prompt").GetAttribute("value")));
        Assert.Equal("Original prompt", _assets.Library.Assets[0].Description); Assert.Equal(0, _generator.Calls);
        await page.InvokeAsync(() => page.Find(".prompt-undo").Click()); Assert.Equal("Original prompt", page.Find("#image-prompt").GetAttribute("value"));
        await StartEnhancement(page); EnhancementReady(); await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").ClickAsync(new()));
        await page.InvokeAsync(() => page.Find("#image-prompt").Input("Manual change")); Assert.Empty(page.FindAll(".prompt-undo"));
        await StartEnhancement(page); EnhancementReady(); await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").ClickAsync(new()));
        await page.InvokeAsync(() => page.Find("#aspect").Change("16:9")); Assert.Empty(page.FindAll(".prompt-undo"));
        Assert.Equal(3, enhancer.Calls);
    }

    [Fact]
    public async Task EnhancementAllowsTypingNamesItBeforeLateApplyAndBlocksImageGeneration()
    {
        var enhancer = (FakePromptEnhancer)Services.GetRequiredService<IPromptEnhancer>(); enhancer.Wait = true;
        _assets.Library = _assets.Library with { Assets = [Asset("Mouse") with { Description = "Original" }] };
        var page = Page();
        await StartEnhancement(page);
        page.WaitForAssertion(() => Assert.Equal(1, enhancer.Calls), BunitDefaults.WaitTimeout(5));
        Assert.True(page.FindComponent<PromptEnhancementPanel>().FindComponent<lumibelle.Components.TextModelPicker>().Instance.Disabled);
        Assert.True(page.FindAll("button").Single(b => b.TextContent.Trim() == "Generate images").HasAttribute("disabled"));
        await page.InvokeAsync(() => { page.Render(); page.Find("#image-prompt").Input("Newer typing"); });
        enhancer.Release.TrySetResult();
        _dialogs.WaitForAssertion(() => Assert.Contains("Since this prompt was written, the prompt was edited.", _dialogs.Markup));
        Assert.Equal("Newer typing", page.Find("#image-prompt").GetAttribute("value"));
        // Applying anyway replaces the prompt as it is now, which the review shows, and Undo restores it.
        Assert.Equal("Original", _dialogs.Find("#enhancement-original").GetAttribute("value"));
        Assert.Equal("Newer typing", _dialogs.Find("#enhancement-current").GetAttribute("value"));
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply anyway").ClickAsync(new()));
        page.WaitForAssertion(() => Assert.Equal("A softly lit mouse illustration.", page.Find("#image-prompt").GetAttribute("value")));
        await page.InvokeAsync(() => page.Find(".prompt-undo").Click());
        Assert.Equal("Newer typing", page.Find("#image-prompt").GetAttribute("value"));
    }

    [Fact]
    public async Task CancelAndRetryKeepPromptAndIncompleteResponseUnapplied()
    {
        var enhancer = (FakePromptEnhancer)Services.GetRequiredService<IPromptEnhancer>(); enhancer.Wait = true;
        _assets.Library = _assets.Library with { Assets = [Asset("Mouse") with { Description = "Original" }] };
        var page = Page();
        var operation = StartEnhancement(page);
        page.WaitForAssertion(() => Assert.Equal(1, enhancer.Calls), BunitDefaults.WaitTimeout(5));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.ClassList.Contains("request-action-button") && b.Closest(".prompt-enhancement") is not null).ClickAsync(new()));
        _dialogs.WaitForElement("#enhancement-original");
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel enhancement").Click());
        await operation;
        _dialogs.WaitForAssertion(() => Assert.Contains("cancelled", _dialogs.Markup));
        Assert.Equal("Original", page.Find("#image-prompt").GetAttribute("value"));
        Assert.True(_dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").HasAttribute("disabled"));
        var originalSnapshot = (await _jobs.ReadSnapshotAsync(_queue.View.Jobs.Single().Id, Xunit.TestContext.Current.CancellationToken));
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>();
        settings.Value = settings.Value with { Temperature = 1.3f, OpenRouterModel = "different-default" };
        enhancer.Wait = false;
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Retry").Click());
        EnhancementReady(); Assert.Equal(2, enhancer.Calls);
        var retriedSnapshot = (await _jobs.ReadSnapshotAsync(_queue.View.Jobs.OrderBy(j => j.CreatedUtc).Last().Id, Xunit.TestContext.Current.CancellationToken));
        Assert.True(JsonElement.DeepEquals(originalSnapshot, retriedSnapshot));
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Close").Click());
        Assert.Equal("Original", page.Find("#image-prompt").GetAttribute("value")); Assert.Empty(page.FindAll(".prompt-undo"));
    }

    [Fact]
    public async Task ClarificationCannotBeAppliedAndVisionRequiresAnExplicitCapableSelection()
    {
        var enhancer = (FakePromptEnhancer)Services.GetRequiredService<IPromptEnhancer>(); enhancer.Kind = PromptEnhancementKind.NeedsInput;
        var image = new AssetImage { Id = Guid.NewGuid(), FileName = "base.png", ContentType = "image/png", Width = 12, Height = 8 };
        _assets.Library = _assets.Library with { Assets = [Asset("Mouse") with { Description = "Original", Images = [image] }] };
        var page = Page(); await StartEnhancement(page);
        _dialogs.WaitForAssertion(() => Assert.Contains("More detail needed", _dialogs.Markup), BunitDefaults.WaitTimeout(5));
        Assert.True(_dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").HasAttribute("disabled"));
        page.WaitForAssertion(() => Assert.Equal(AiJobState.Completed, _queue.View.Jobs.Single().State));
        _dialogs.Render();
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Back to prompt").Click());
        page.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".prompt-enhancement-dialog")));
        page.Render();
        await page.InvokeAsync(() => page.Find(".media-select").Click());
        await page.InvokeAsync(() => page.Find("#image-prompt").Input("Change the coat"));
        await page.InvokeAsync(() => EnhancementControls(page).Find(".inspection-toggle input").Change(true));
        Assert.True(EnhancementControls(page).Find(".enhance-button").HasAttribute("disabled"));
        Assert.Contains("vision model", EnhancementControls(page).Markup);
        await page.InvokeAsync(() => EnhancementControls(page).Find(".inspection-toggle input").Change(false));
        Assert.False(EnhancementControls(page).Find(".enhance-button").HasAttribute("disabled"));
    }

    [Fact]
    public void AnUnavailableModelIsExplainedOnTheFooterChipInTheDialogOnly()
    {
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>();
        settings.Value = settings.Value with { HasOpenRouterKey = false };
        _assets.Library = _assets.Library with { Assets = [Asset("Mouse", "Portrait prompt")] };
        var page = Page(); var controls = EnhancementControls(page);
        controls.WaitForAssertion(() => Assert.Contains("Add an OpenRouter key", controls.Find(".assist-footer-model .model-chip .request-notice-badge").GetAttribute("title")));
        var chip = controls.Find(".assist-footer-model .model-chip");
        Assert.Contains("Add an OpenRouter key", controls.Find($"#{chip.GetAttribute("aria-describedby")}").TextContent);
        Assert.Empty(page.FindAll(".model-notice")); Assert.Empty(page.FindAll(".model-chip .request-notice-badge"));
        Assert.True(controls.Find(".enhance-button").HasAttribute("disabled"));
    }

    [Fact]
    public async Task FailedDefaultSavePreservesEnhancementOverrideAndDoesNotStartRequest()
    {
        var settings = (FakeAiSettingsStore)Services.GetRequiredService<IAiSettingsStore>();
        var choice = new TextModelReference(AiBackend.OpenRouter, "alternate", "Alternate");
        settings.Value = settings.Value with { StarredTextModels = [choice] };
        ((FakeProviders)Services.GetRequiredService<IAiProviderRegistry>()).Models = [new("test/model", "Default"), new(choice.Model, choice.Name)];
        ((FakeProjectAiPreferencesStore)Services.GetRequiredService<IProjectAiPreferencesStore>()).SaveError = new WorkspaceConflictException();
        _assets.Library = _assets.Library with { Assets = [Asset("Mouse", "Portrait prompt")] };
        var page = Page(); var controls = EnhancementControls(page);
        await controls.InvokeAsync(() => controls.Find(".assist-composer-model .model-chip").Click());
        _dialogs.WaitForElement(".ai-assist-dialog select").Change(TextModelPolicy.Key(choice));
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Set as project default").ClickAsync(new()));
        _dialogs.WaitForAssertion(() => Assert.Contains("changed", _dialogs.Markup));
        Assert.Equal(TextModelPolicy.Key(choice), _dialogs.Find(".ai-assist-dialog select").GetAttribute("value"));
        Assert.Empty(_queue.View.Jobs);
    }
    private IRenderedComponent<MudBlazor.MudDialogProvider> EnhancementControls(IRenderedComponent<AssetsStudio> page)
    {
        if (_dialogs.FindAll(".enhance-button").Count == 0)
            page.InvokeAsync(() => (page.FindAll(".prompt-enhancement .request-action-menu button").FirstOrDefault(b => b.TextContent.Trim() == "New request") ?? page.Find(".prompt-enhancement .assist-trigger")).ClickAsync(new())).GetAwaiter().GetResult();
        return _dialogs;
    }
    private async Task StartEnhancement(IRenderedComponent<AssetsStudio> page)
    {
        var controls = EnhancementControls(page);
        controls.WaitForAssertion(() => Assert.False(controls.Find(".enhance-button").HasAttribute("disabled")));
        await controls.InvokeAsync(() => controls.Find(".enhance-button").ClickAsync(new()));
    }
    private void EnhancementReady() => _dialogs.WaitForAssertion(() =>
        Assert.False(_dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply changes").HasAttribute("disabled")), BunitDefaults.WaitTimeout(5));

    [Fact]
    public async Task QueuedEnhancementRestoresAfterReloadWithoutCallingProviderAndSurvivesDisposal()
    {
        var enhancer = (FakePromptEnhancer)Services.GetRequiredService<IPromptEnhancer>();
        _assets.Library = _assets.Library with { Assets = [Asset("Mouse") with { Description = "Seed prompt" }] };
        await _queue.SetPausedAsync(AiBackend.OpenRouter, true, Xunit.TestContext.Current.CancellationToken);
        var page = Page(); await page.InvokeAsync(() => page.Find("#image-prompt").Input("Captured authored prompt"));
        await StartEnhancement(page);
        page.WaitForAssertion(() => Assert.Contains("Queued", page.Markup));
        Assert.Equal(0, enhancer.Calls); await page.FindComponent<PromptEnhancementPanel>().Instance.DisposeAsync(); page.Dispose();
        page = Page(false);
        page.WaitForAssertion(() => Assert.Equal("Captured authored prompt", page.Find("#image-prompt").GetAttribute("value")));
        Assert.Contains("Queued", page.Find(".prompt-enhancement .request-action-button").TextContent);
        Assert.False(page.Find(".prompt-enhancement .request-action-button").HasAttribute("disabled"));
        await page.FindComponent<PromptEnhancementPanel>().Instance.DisposeAsync(); page.Dispose(); await _queue.SetPausedAsync(AiBackend.OpenRouter, false, Xunit.TestContext.Current.CancellationToken);
        // No rendered subscriber remains: wait on the coordinator, not a render event.
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (_queue.View.Jobs.Single().State != AiJobState.Completed) await Task.Delay(10, timeout.Token);
        }
        Assert.Empty(_dialogs.FindAll("#enhancement-original")); Assert.Equal(1, enhancer.Calls);
        page = Page(false);
        page.WaitForAssertion(() => Assert.Contains("Review changes", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.ClassList.Contains("request-action-button") && b.Closest(".prompt-enhancement") is not null).ClickAsync(new()));
        EnhancementReady(); Assert.Equal("Captured authored prompt", page.Find("#image-prompt").GetAttribute("value"));
    }

    [Fact]
    public async Task ClosingEnhancementKeepsRunningAndExplicitReopenPreservesEditedSuggestion()
    {
        var enhancer = (FakePromptEnhancer)Services.GetRequiredService<IPromptEnhancer>(); enhancer.Wait = true;
        _assets.Library = _assets.Library with { Assets = [Asset("Mouse") with { Description = "Original" }] };
        var page = Page(); await StartEnhancement(page);
        page.WaitForAssertion(() => Assert.Equal(1, enhancer.Calls), BunitDefaults.WaitTimeout(5));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.ClassList.Contains("request-action-button") && b.Closest(".prompt-enhancement") is not null).ClickAsync(new()));
        await _dialogs.InvokeAsync(() => _dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Close").Click());
        Assert.False(enhancer.Token.IsCancellationRequested); enhancer.Release.TrySetResult();
        page.WaitForAssertion(() => Assert.Equal(AiJobState.Completed, _queue.View.Jobs.Single().State), BunitDefaults.WaitTimeout(5));
        Assert.Empty(_dialogs.FindAll("#enhancement-original"));
        page.WaitForAssertion(() => Assert.Contains("Review changes", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.ClassList.Contains("request-action-button") && b.Closest(".prompt-enhancement") is not null).ClickAsync(new()));
        EnhancementReady(); await _dialogs.InvokeAsync(() => _dialogs.Find("#enhancement-suggestion").Input("My edited suggestion"));
        await _queue.MarkReadAsync(enhancer.JobId, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("My edited suggestion", _dialogs.Find("#enhancement-suggestion").GetAttribute("value"));
    }

    [Fact]
    public async Task ReopeningResultKeepsNewerPromptAndOtherAssetsRemainUsable()
    {
        var enhancer = (FakePromptEnhancer)Services.GetRequiredService<IPromptEnhancer>(); enhancer.Wait = true;
        var mouse = Asset("Mouse") with { Description = "Mouse prompt" }; var room = Asset("Room", "A small room");
        _assets.Library = _assets.Library with { Assets = [mouse, room] };
        var page = Page(); await StartEnhancement(page);
        page.WaitForAssertion(() => Assert.Equal(1, enhancer.Calls), BunitDefaults.WaitTimeout(5));
        await page.InvokeAsync(() => page.Find("#image-prompt").Input("Newer authored instructions"));
        await page.InvokeAsync(() => page.FindAll(".asset-choice").Single(b => b.TextContent.Contains("Room")).Click());
        page.WaitForAssertion(() => Assert.False(EnhancementControls(page).Find(".enhance-button").HasAttribute("disabled")));
        enhancer.Release.TrySetResult(); page.WaitForAssertion(() => Assert.Equal(AiJobState.Completed, _queue.View.Jobs.Single().State), BunitDefaults.WaitTimeout(5));
        Assert.Empty(_dialogs.FindAll("#enhancement-original"));
        await page.InvokeAsync(() => page.FindAll(".asset-choice").Single(b => b.TextContent.Contains("Mouse")).Click());
        page.WaitForAssertion(() => Assert.Contains("Review changes", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.ClassList.Contains("request-action-button") && b.Closest(".prompt-enhancement") is not null).ClickAsync(new()));
        _dialogs.WaitForAssertion(() => Assert.Contains("the prompt was edited", _dialogs.Markup));
        Assert.Equal("Newer authored instructions", page.Find("#image-prompt").GetAttribute("value"));
        Assert.False(_dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply anyway").HasAttribute("disabled"));
    }
}

internal sealed class FakePromptEnhancer : IPromptEnhancer, IAiJobHandler
{
    public int Calls;
    public bool Wait;
    public PromptEnhancementKind Kind = PromptEnhancementKind.Prompt;
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken Token;
    public Guid JobId;
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.PromptEnhancement];
    public async Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
    {
        Token = ct; JobId = context.Job.Id;
        var request = snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.Payload<PromptEnhancementRequest>();
        await foreach (var update in EnhanceAsync(request, ct))
        {
            if (update.Progress is { } progress) await context.ReportAsync(new(progress));
            await context.SaveResultAsync(new AiTextJobResult(update.Text ?? "Complete mock enhancement", update.Complete, "stop",
                update.Result is null ? null : JsonSerializer.SerializeToElement(update.Result, AtomicJsonFile.Options), update.Error));
        }
        return AiJobOutcome.Complete();
    }
    public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(AiJobOutcome.Attention("Interrupted mock enhancement", AiJobRecovery.GenerateAgain));
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Task.FromResult(true);
    public Task ValidateInputsAsync(PromptEnhancementContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async IAsyncEnumerable<PromptEnhancementUpdate> EnhanceAsync(PromptEnhancementRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++; yield return new(Progress: new(GenerationPhase.Generating, "Enhancing"), Text: "partial");
        if (Wait) await Release.Task.WaitAsync(cancellationToken);
        yield return new(Result: new(Kind, Kind == PromptEnhancementKind.Prompt ? "A softly lit mouse illustration." : "What exact lettering should appear?"), Complete: true);
    }
}
