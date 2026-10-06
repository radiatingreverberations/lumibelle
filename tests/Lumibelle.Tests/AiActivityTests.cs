using Bunit;
using lumibelle.Components.AI;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed partial class AiActivityTests : BunitContext
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.ActivityTests", Guid.NewGuid().ToString("N"));
    private readonly FileAiJobStore _store;
    private readonly AiJobCoordinator _queue;
    private readonly CancellationToken _ct = Xunit.TestContext.Current.CancellationToken;
    public AiActivityTests()
    {
        Services.AddMudServices(); Services.AddSingleton(TimeProvider.System); JSInterop.Mode = JSRuntimeMode.Loose;
        _store = new(new StorageTestEnvironment(_root), TimeProvider.System);
        _queue = new(_store, new FakeAiSettingsStore(), [], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        Services.AddSingleton<IAiJobStore>(_store); Services.AddSingleton(_queue); Render<MudPopoverProvider>();
    }
    private Task<AiJobHeader> Add(string label, Guid? project = null) => _store.EnqueueAsync(AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.ScriptAssistant,
        AiBackend.ComfyUI, new(project ?? Guid.NewGuid()), "QA project", label, Guid.NewGuid(), new { prompt = "Captured" }), _ct);
    // Buttons can appear after the render that follows an earlier action; wait for one before clicking it.
    private static async Task Button(IRenderedComponent<AiActivity> ui, string label)
    {
        ui.WaitForAssertion(() => Assert.Contains(ui.FindAll("button"), b => b.TextContent.Trim() == label));
        await ui.ClickCurrent(() => ui.FindAll("button").Single(b => b.TextContent.Trim() == label));
    }
    private async Task Open(IRenderedComponent<AiActivity> ui)
    {
        // Initialization reads the file store asynchronously. Finish that read before interacting.
        await ui.InvokeAsync(() => _queue.RefreshAsync(_ct));
        await ui.ClickCurrent(() => ui.Find(".ai-activity-trigger"));
    }
    [Fact]
    public async Task RunningElapsedTimeTicksWithoutProviderUpdatesAndResumesFromSavedStart()
    {
        var added = await Add("Quiet provider");
        var running = (await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!;
        running = running with { StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-2) };
        var timing = new CodexImageTiming(running.StartedUtc!.Value, [new("image", running.StartedUtc.Value.AddSeconds(10))]);
        var progress = new AiJobProgress(new(GenerationPhase.Generating, "Waiting for response…"), CodexTiming: timing);
        var ui = Render<AiJobProgressView>(p => p.Add(c => c.Job, running).Add(c => c.Queue, new()).Add(c => c.Progress, progress));
        var initial = ui.Find(".generation-progress-meta").TextContent;
        var imageTime = ui.FindAll(".codex-timing-values dd")[1].TextContent;
        Assert.Contains("2m", initial);
        ui.WaitForAssertion(() => Assert.NotEqual(initial, ui.Find(".generation-progress-meta").TextContent), BunitDefaults.WaitTimeout(3));
        ui.WaitForAssertion(() => Assert.NotEqual(imageTime, ui.FindAll(".codex-timing-values dd")[1].TextContent), BunitDefaults.WaitTimeout(3));
        Assert.Equal("≈10s", ui.FindAll(".codex-timing-values dd")[0].TextContent);
        Assert.Equal(TimeSpan.Zero, progress.Progress.Elapsed); // Display time never mutates a provider checkpoint.
        ui.Render(p => p.Add(c => c.Job, running with { State = AiJobState.Completed }));
        Assert.Empty(ui.FindAll(".compact-generation-progress"));
    }
    [Theory]
    [InlineData(AiJobKind.Video)] [InlineData(AiJobKind.ReelVideo)] [InlineData(AiJobKind.ImageCreate)] [InlineData(AiJobKind.ImageEdit)]
    public async Task OutOfOrderTakeProgressShowsExecutionPositionAndKeepsItOnReopen(AiJobKind kind)
    {
        var job = (await Add("Take progress")) with { Kind = kind, State = AiJobState.Running };
        VideoCandidate[] candidates = [new() { Number = 1, State = VideoCandidateState.Waiting }, new() { Number = 2, State = VideoCandidateState.Running }];
        var progress = new AiJobProgress(new(GenerationPhase.Generating, "Take 2 · Sampling H3", 6, 8, "steps") {
            EstimateScope = "take_2_sampling", EstimatedRemaining = TimeSpan.FromSeconds(15),
            EstimateObservedUtc = DateTimeOffset.UtcNow, EstimateExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        }, 2, 2) { CandidateExecutionOrder = [2] };
        var ui = Render<AiJobProgressView>(p => p.Add(c => c.Job, job).Add(c => c.Queue, new())
            .Add(c => c.Progress, progress).Add(c => c.VideoCandidates, candidates));

        Assert.Equal("0 / 2 takes saved", ui.Find(".ai-batch-summary").TextContent);
        Assert.Equal("1/2 · Sampling H3", ui.Find(".generation-progress-label").TextContent);
        Assert.Contains("6 / 8 steps", ui.Find(".generation-progress-meta").TextContent);
        Assert.DoesNotContain("Candidate 2 of 2", ui.Markup);
        Assert.DoesNotContain("Take 2", ui.Markup);
        Assert.DoesNotContain("take_2_", ui.Markup);
        Assert.Contains("left in sampling", ui.Find(".generation-progress-meta").TextContent);

        candidates[0] = candidates[0] with { State = VideoCandidateState.Running };
        progress = new(new(GenerationPhase.Generating, "Take 1 · Sampling H3", 1, 8, "steps"), 1, 2) { CandidateExecutionOrder = [2, 1] };
        ui.Render(p => p.Add(c => c.Progress, progress).Add(c => c.VideoCandidates, candidates));
        Assert.Equal("0 / 2 takes saved", ui.Find(".ai-batch-summary").TextContent);
        Assert.Equal("2/2 · Sampling H3", ui.Find(".generation-progress-label").TextContent);
        Assert.DoesNotContain("Candidate 1 of 2", ui.Markup);
        Assert.DoesNotContain("Take 1", ui.Markup);

        await _store.WriteArtifactAsync(job.Id, AiJobArtifact.Progress, progress, _ct);
        var reopened = Render<AiJobProgressView>(p => p.Add(c => c.Job, job).Add(c => c.Queue, new()));
        reopened.WaitForAssertion(() => Assert.Equal("2/2 · Sampling H3", reopened.Find(".generation-progress-label").TextContent));

        // Later decoding of the first branch must not make overall progress count backward.
        ui.Render(p => p.Add(c => c.Progress, progress with {
            Candidate = 2, Progress = new(GenerationPhase.Finalizing, "Take 2 · Encoding MP4")
        }));
        Assert.Equal("2/2 · Encoding MP4", ui.Find(".generation-progress-label").TextContent);
    }

    [Fact]
    public async Task ActivityShowsApplicationQueueOrderPauseAndDirectCancellation()
    {
        var a = await Add("First request"); var b = await Add("Second request");
        var ui = Render<AiActivity>(); await Open(ui);
        ui.WaitForAssertion(() => Assert.Contains("0 running · 2 waiting", ui.Markup)); Assert.Contains("Queued in Lumibelle", ui.Markup);
        await ui.InvokeAsync(() => Assert.False(ui.Find($"[data-job-id='{b.Id}']").QuerySelectorAll("button").Single(x => x.TextContent == "Run next").HasAttribute("disabled")));
        await ui.ClickCurrent(() => ui.Find($"[data-job-id='{b.Id}']").QuerySelectorAll("button").Single(x => x.TextContent == "Run next"));
        ui.WaitForAssertion(() => Assert.Equal(b.Id.ToString(), ui.FindAll(".ai-activity-job")[0].GetAttribute("data-job-id")));
        await ui.ClickCurrent(() => ui.Find("button[aria-label='Pause queue ComfyUI']")); ui.WaitForAssertion(() => Assert.Contains("This provider queue is paused", ui.Markup));
        Assert.Contains(AiBackend.ComfyUI, (await _store.ReadAsync(_ct)).Paused);
        await ui.ClickCurrent(() => ui.Find($"[data-job-id='{b.Id}']").QuerySelectorAll("button").Single(x => x.TextContent == "Cancel"));
        ui.WaitForAssertion(() => Assert.Single(ui.FindAll(".ai-activity-job")));
        Assert.Equal(AiJobState.Cancelled, (await _store.ReadAsync(_ct)).Jobs.Single(j => j.Id == b.Id).State);
        await ui.InvokeAsync(() => Assert.Equal(a.Id.ToString(), ui.Find(".ai-activity-job").GetAttribute("data-job-id")));
    }
    [Fact]
    public async Task UnconfirmedRemoteJobStaysVisibleAndCancellableUntilItsReservationIsReleased()
    {
        var job = await Add("Missing ComfyUI video");
        await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.NeedsAttention, RemoteUnconfirmed = true, Recovery = AiJobRecovery.CheckStatus }, _ct);
        var ui = Render<AiActivity>(); await Open(ui);
        ui.WaitForAssertion(() => Assert.Contains("1 blocked", ui.Find(".ai-activity-trigger").TextContent));
        await ui.InvokeAsync(() =>
        {
            Assert.Contains("ComfyUI is reserved", ui.Find(".ai-activity-job").TextContent);
            Assert.Contains(ui.FindAll(".ai-job-actions button"), b => b.TextContent == "Cancel");
        });
        await Button(ui, "Needs attention");
        await ui.InvokeAsync(() => ui.FindAll(".ai-job-actions button").Single(b => b.TextContent == "Cancel").ClickAsync(new()));
        Assert.True((await _store.ReadAsync(_ct)).Jobs.Single().CancelRequested);
        await Button(ui, "Active");
        await ui.InvokeAsync(() =>
        {
            Assert.Single(ui.FindAll(".ai-activity-job"));
            Assert.DoesNotContain(ui.FindAll(".ai-job-actions button"), b => b.TextContent == "Cancel");
            Assert.Contains(ui.FindAll(".ai-job-actions button"), b => b.TextContent == "Check status");
        });
        // Local cancellation alone must not claim that a remote workload has stopped.
        ui.WaitForAssertion(() => Assert.Contains("1 blocked", ui.Find(".ai-activity-trigger").TextContent));
        await _store.UpdateAsync(job.Id, j => j with { RemoteUnconfirmed = false, Recovery = AiJobRecovery.None }, _ct);
        await _queue.RefreshAsync(_ct);
        ui.WaitForAssertion(() => Assert.Empty(ui.FindAll(".ai-activity-job")));
        await ui.InvokeAsync(() => Assert.DoesNotContain("blocked", ui.Find(".ai-activity-trigger").TextContent));
    }
    [Fact]
    public async Task FailedResponsesStayInspectableWhenTheirOriginalProjectIsGone()
    {
        JSInterop.SetupModule("./_content/Lumibelle.UI/ai-jobs.js").Setup<bool>("isReviewVisible", _ => true).SetResult(true);
        var job = await Add("Removed target"); await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.NeedsAttention, Unread = true, Error = "Response needs correction" }, _ct);
        await _store.WriteArtifactAsync(job.Id, AiJobArtifact.Result, new AiTextJobResult("Saved response for the original target", true), _ct);
        var ui = Render<AiActivity>(); await Open(ui); await Button(ui, "Needs attention");
        await ui.InvokeAsync(() => Assert.Equal("true", ui.FindAll("[role='tab']")[1].GetAttribute("aria-selected")));
        await Button(ui, "Inspect response"); ui.WaitForAssertion(() => Assert.Contains("Saved response for the original target", ui.Markup));
        // Showing the response marks it read, and the drawer re-renders on the renderer's thread when that lands.
        // Query the DOM only inside the wait: a test-thread query during that render can cache the previous markup.
        ui.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(ui.FindAll(".ai-job-actions button"), b => b.TextContent == "Mark read");
            Assert.Equal("true", ui.FindAll("button").Single(b => b.TextContent == "Hide response").GetAttribute("aria-expanded"));
            Assert.Single(ui.FindAll(".ai-activity-job"));
        });
        await Button(ui, "Hide response"); ui.WaitForAssertion(() => Assert.Empty(ui.FindAll(".ai-activity-job")));
        await Button(ui, "History"); ui.WaitForAssertion(() => Assert.Single(ui.FindAll(".ai-activity-job")));
        await Button(ui, "Inspect response"); ui.WaitForAssertion(() => Assert.Contains("Saved response for the original target", ui.Markup));
        Assert.NotNull(await _store.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, _ct));
        await ui.InvokeAsync(() => Assert.Equal(job.ReviewUrl, ui.Find(".ai-job-actions a").GetAttribute("href")));
    }
    [Fact]
    public async Task ReviewNavigationOccursAfterTheDrawerClosesAndDoesNotConsumeTheUnreadResult()
    {
        var job = await Add("Review exact target");
        await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.Completed, Unread = true }, _ct);
        var ui = Render<AiActivity>(); await Open(ui); await Button(ui, "History");
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var closedAtNavigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        navigation.LocationChanged += (_, _) => closedAtNavigation.TrySetResult(ui.FindAll(".ai-activity-panel").Count == 0);
        await ui.InvokeAsync(() => ui.Find(".ai-job-actions a").Click());
        // Navigation follows the drawer's last render, so await it: a WaitFor checks only after renders.
        Assert.True(await closedAtNavigation.Task.WaitAsync(BunitContext.DefaultWaitTimeout, _ct));
        Assert.EndsWith(job.ReviewUrl, navigation.Uri);
        Assert.True((await _store.ReadAsync(_ct)).Jobs.Single().Unread);
    }
    [Fact]
    public async Task ReopenedRunningJobShowsDurableProgressWithoutClaimingItIsLive()
    {
        var job = await Add("Transferring saved frames");
        await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct);
        await _store.WriteArtifactAsync(job.Id, AiJobArtifact.Progress, new AiJobProgress(new(GenerationPhase.Downloading, "Saving lossless frames", 17, 39, "frames"), 1, 2), _ct);
        var ui = Render<AiActivity>(); await Open(ui);
        ui.WaitForAssertion(() => Assert.Contains("Saving lossless frames", ui.Markup));
        Assert.Contains("Saved progress · waiting for live updates", ui.Markup);
        Assert.DoesNotContain("Preparing request", ui.Markup);
        Assert.Contains("17", ui.Markup); Assert.Contains("39", ui.Markup);
    }
    [Fact]
    public async Task ExtendingTheActiveBatchExplainsTheEffectOnWaitingRequests()
    {
        var id = Guid.NewGuid();
        await _store.EnqueueAsync(AiJobSubmission.Create(id, AiJobKind.Video, AiBackend.ComfyUI,
            new(Guid.NewGuid(), ShotId: Guid.NewGuid()), "QA project", "Video takes", Guid.NewGuid(), new { }) with
            { Batch = AiBatchDefinition.Create(id, 1) }, _ct);
        await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct);
        var waiting = await Add("Image edit waiting");
        var ui = Render<AiActivity>(); await Open(ui);
        Assert.DoesNotContain("Extra takes stay with this batch", ui.Markup);
        await _store.ExtendBatchAsync(id, Guid.NewGuid(), Guid.NewGuid(), _ct);
        await _queue.RefreshAsync(_ct);
        ui.WaitForAssertion(() => Assert.Contains("Extra takes stay with this batch. Waiting requests will start afterward.", ui.Markup));
        await _queue.CancelAsync(waiting.Id, _ct);
        ui.WaitForAssertion(() => Assert.DoesNotContain("Extra takes stay with this batch", ui.Markup));
    }
    [Fact]
    public async Task HistoryFiltersAndPaginatesWithoutChangingProviderOrder()
    {
        for (var i = 0; i < 21; i++) { var job = await Add("Saved " + i); await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.Completed, FinishedUtc = DateTimeOffset.UtcNow }, _ct); }
        var selected = await Add("This project only"); await _store.UpdateAsync(selected.Id, j => j with { State = AiJobState.Completed }, _ct);
        var ui = Render<AiActivity>(); await Open(ui);
        await ui.InvokeAsync(async () =>
        {
            await ui.FindAll("[role='tab']")[0].TriggerEventAsync("onkeydown", new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "End" });
            Assert.Equal("true", ui.FindAll("[role='tab']")[2].GetAttribute("aria-selected"));
            Assert.Equal(20, ui.FindAll(".ai-activity-job").Count);
        });
        await Button(ui, "Next"); await ui.InvokeAsync(() => Assert.Equal(2, ui.FindAll(".ai-activity-job").Count));
        await ui.InvokeAsync(async () =>
        {
            await ui.Find(".ai-activity-filters select").ChangeAsync(new() { Value = selected.Target.ProjectId!.Value.ToString() });
            Assert.Single(ui.FindAll(".ai-activity-job")); Assert.Contains("This project only", ui.Find(".ai-activity-job").TextContent);
            await ui.Find(".ai-activity-panel").TriggerEventAsync("onkeydown", new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
            Assert.Empty(ui.FindAll(".ai-activity-panel"));
            Assert.Equal("false", ui.Find(".ai-activity-trigger").GetAttribute("aria-expanded"));
        });
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _queue.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
