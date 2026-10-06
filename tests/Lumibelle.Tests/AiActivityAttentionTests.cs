using System.Text.Json;
using Bunit;
using lumibelle.Components.AI;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class AiActivityAttentionPolicyTests
{
    private static AiJobHeader Job() => new() {
        Id = Guid.NewGuid(), Kind = AiJobKind.ScriptAssistant, Backend = AiBackend.ComfyUI,
        Target = new(Guid.NewGuid()), ProjectName = "Project", TargetName = "Result",
        OriginTabId = Guid.NewGuid(), RequestFingerprint = new('a', 64),
        State = AiJobState.Completed, Unread = true
    };

    [Theory]
    [InlineData(AiJobState.Completed, true, false, false, null, false)]
    [InlineData(AiJobState.Completed, false, false, false, null, false)]
    [InlineData(AiJobState.Completed, true, false, false, "", false)]
    [InlineData(AiJobState.Completed, true, false, false, "  ", false)]
    [InlineData(AiJobState.Completed, true, false, false, "Output needs checking", true)]
    [InlineData(AiJobState.Completed, false, false, false, "Output needs checking", false)]
    [InlineData(AiJobState.Completed, true, true, false, "Output needs checking", false)]
    [InlineData(AiJobState.NeedsAttention, true, false, false, null, true)]
    [InlineData(AiJobState.NeedsAttention, true, false, false, "Download failed", true)]
    [InlineData(AiJobState.NeedsAttention, false, false, false, "Download failed", false)]
    [InlineData(AiJobState.NeedsAttention, true, true, false, "Download failed", false)]
    [InlineData(AiJobState.NeedsAttention, true, false, true, "Cancelled locally", false)]
    [InlineData(AiJobState.Waiting, true, false, false, "Previous error", false)]
    [InlineData(AiJobState.Running, true, false, false, "Previous error", false)]
    [InlineData(AiJobState.Cancelled, true, false, false, null, false)]
    [InlineData(AiJobState.Cancelled, true, false, true, "Request cancelled", false)]
    public void OnlyUnacknowledgedTerminalProblemsRaiseAlerts(AiJobState state, bool unread,
        bool cleared, bool cancelled, string? error, bool expected)
    {
        var job = Job() with { State = state, Unread = unread, CancelRequested = cancelled,
            ActivityClearedUtc = cleared ? DateTimeOffset.UnixEpoch : null, Error = error };
        var before = JsonSerializer.Serialize(job, AtomicJsonFile.Options);
        Assert.Equal(expected, AiActivityPolicy.NeedsAttention(job));
        Assert.Equal(before, JsonSerializer.Serialize(job, AtomicJsonFile.Options));
    }

    [Theory]
    [InlineData(AiJobState.Waiting)]
    [InlineData(AiJobState.Running)]
    [InlineData(AiJobState.NeedsAttention)]
    [InlineData(AiJobState.Completed)]
    [InlineData(AiJobState.Cancelled)]
    public void RemoteUncertaintyIsNeverHiddenByReadClearOrLocalCancellation(AiJobState state)
    {
        var job = Job() with { State = state, RemoteUnconfirmed = true, Unread = false,
            CancelRequested = true, ActivityClearedUtc = DateTimeOffset.UnixEpoch };
        Assert.True(AiActivityPolicy.NeedsAttention(job));
        Assert.True(job.HoldsProvider);
        Assert.False(job.CanClearActivity);
        Assert.False(AiActivityPolicy.NeedsAttention(job with { RemoteUnconfirmed = false }));
    }

    [Fact]
    public void TheRuleAppliesToEveryRequestKindNotOnlyImagesAndVideo()
    {
        foreach (var kind in Enum.GetValues<AiJobKind>())
        {
            var job = Job() with { Kind = kind };
            Assert.False(AiActivityPolicy.NeedsAttention(job));
            Assert.True(AiActivityPolicy.NeedsAttention(job with { State = AiJobState.NeedsAttention }));
        }
    }

    [Fact]
    public void SuccessfulBatchPublicationDoesNotBecomeAnAlertAtCompletion()
    {
        var job = Job() with { State = AiJobState.Running, Version = 2 };
        Assert.False(AiActivityPolicy.NeedsAttention(job)); // A take is ready while more run.
        Assert.False(AiActivityPolicy.NeedsAttention(job with { State = AiJobState.Completed, Version = 3 }));
        Assert.True(AiActivityPolicy.NeedsAttention(job with { State = AiJobState.NeedsAttention,
            Error = "Some takes could not be saved", Recovery = AiJobRecovery.RetryOutput, Version = 3 }));
    }
}

public sealed partial class AiActivityTests
{
    [Fact]
    public async Task ActivityAttentionIgnoresExistingSuccessfulBacklogWithoutRewritingHistory()
    {
        AiJobHeader? last = null;
        for (var i = 0; i < 25; i++)
        {
            var job = await Add("Successful " + i);
            last = await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.Completed,
                FinishedUtc = DateTimeOffset.UnixEpoch.AddSeconds(i), Unread = true }, _ct);
        }
        await _store.WriteArtifactAsync(last!.Id, AiJobArtifact.Result, new AiTextJobResult("Retained success", true), _ct);
        var before = JsonSerializer.Serialize(await _store.ReadAsync(_ct), AtomicJsonFile.Options);
        var ui = Render<AiActivity>(); await Open(ui); await Button(ui, "Needs attention");
        Assert.Empty(ui.FindAll("[data-ai-attention-count]"));
        Assert.DoesNotContain("unread", ui.Find(".ai-activity-trigger").TextContent);
        Assert.Empty(ui.FindAll(".ai-activity-job"));
        Assert.Contains("Successful results", ui.Find(".ai-activity-empty").TextContent);
        Assert.True(ui.FindAll("button").Single(b => b.TextContent.Trim() == "Acknowledge alerts").HasAttribute("disabled"));
        await Button(ui, "History"); Assert.Equal(20, ui.FindAll(".ai-activity-job").Count);
        await Button(ui, "Next"); Assert.Equal(5, ui.FindAll(".ai-activity-job").Count);
        Assert.Equal(before, JsonSerializer.Serialize(await _store.ReadAsync(_ct), AtomicJsonFile.Options));
        Assert.Equal("Retained success", (await _store.ReadArtifactAsync<AiTextJobResult>(last.Id, AiJobArtifact.Result, _ct))!.Raw);
    }

    [Fact]
    public async Task ActivityAttentionBadgeAndTabUseTheSameProblemSelection()
    {
        async Task<AiJobHeader> AddState(string label, AiJobState state, string? error = null, bool remote = false)
        {
            var job = await Add(label);
            if (state == AiJobState.Running)
                Assert.Equal(job.Id, (await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!.Id);
            return await _store.UpdateAsync(job.Id, j => j with { State = state, Error = error,
                RemoteUnconfirmed = remote, Unread = true }, _ct);
        }
        _ = await AddState("Successful proposal", AiJobState.Completed);
        _ = await AddState("Running batch", AiJobState.Running);
        _ = await AddState("Confirmed cancellation", AiJobState.Cancelled, "Request cancelled");
        var failed = await AddState("Failed response", AiJobState.NeedsAttention, "Invalid output");
        var warning = await AddState("Completed with an issue", AiJobState.Completed, "Save needs checking");
        var remote = await AddState("Unconfirmed remote", AiJobState.NeedsAttention, "Check status", true);
        var ui = Render<AiActivity>(); await Open(ui); await Button(ui, "Needs attention");
        Assert.Equal("3", ui.Find("[data-ai-attention-count]").GetAttribute("data-ai-attention-count"));
        Assert.Equal(new[] { failed.Id, warning.Id, remote.Id }.Order(),
            ui.FindAll(".ai-activity-job").Select(n => Guid.Parse(n.GetAttribute("data-job-id")!)).Order());
        Assert.Contains("1 active", ui.Find(".ai-activity-trigger").TextContent);
        Assert.Contains("1 blocked", ui.Find(".ai-activity-trigger").TextContent);
        await ui.InvokeAsync(() => ui.Find($"[data-job-id='{failed.Id}']").QuerySelectorAll("button")
            .Single(b => b.TextContent == "Mark read").ClickAsync(new()));
        ui.WaitForAssertion(() => Assert.Equal("2", ui.Find("[data-ai-attention-count]").GetAttribute("data-ai-attention-count")));
        Assert.Empty(ui.FindAll($"[data-job-id='{failed.Id}']"));
        await Button(ui, "History"); Assert.Single(ui.FindAll($"[data-job-id='{failed.Id}']"));
        Assert.Equal(AiJobState.NeedsAttention, (await _store.ReadAsync(_ct)).Jobs.Single(j => j.Id == failed.Id).State);
    }

    [Fact]
    public async Task ActivityAttentionBulkAcknowledgementRespectsProjectAndKeepsSuccessfulResultsUnread()
    {
        var project = Guid.NewGuid();
        var failure = await Add("This failure", project);
        await _store.UpdateAsync(failure.Id, j => j with { State = AiJobState.NeedsAttention, Unread = true }, _ct);
        var success = await Add("This success", project);
        await _store.UpdateAsync(success.Id, j => j with { State = AiJobState.Completed, Unread = true }, _ct);
        var remote = await Add("Remote unconfirmed", project);
        await _store.UpdateAsync(remote.Id, j => j with { State = AiJobState.NeedsAttention,
            RemoteUnconfirmed = true, Unread = true, Recovery = AiJobRecovery.CheckStatus }, _ct);
        var other = await Add("Other failure");
        await _store.UpdateAsync(other.Id, j => j with { State = AiJobState.NeedsAttention, Unread = true }, _ct);
        var ui = Render<AiActivity>(); await Open(ui); await Button(ui, "Needs attention");
        await ui.InvokeAsync(() => ui.Find("#ai-activity-project").Change(project.ToString()));
        Assert.Equal(2, ui.FindAll(".ai-activity-job").Count);
        await Button(ui, "Acknowledge alerts");
        ui.WaitForAssertion(() => Assert.Single(ui.FindAll(".ai-activity-job")));
        Assert.Equal(remote.Id.ToString(), ui.Find(".ai-activity-job").GetAttribute("data-job-id"));
        // The global badge still includes the remote reservation and the other project.
        Assert.Equal("2", ui.Find("[data-ai-attention-count]").GetAttribute("data-ai-attention-count"));
        var saved = (await _store.ReadAsync(_ct)).Jobs;
        Assert.False(saved.Single(j => j.Id == failure.Id).Unread);
        Assert.False(saved.Single(j => j.Id == remote.Id).Unread);
        Assert.True(saved.Single(j => j.Id == remote.Id).HoldsProvider);
        Assert.True(saved.Single(j => j.Id == success.Id).Unread);
        Assert.True(saved.Single(j => j.Id == other.Id).Unread);
    }

    [Fact]
    public async Task ActivityAttentionAcknowledgesAllSelectedAlertsAcrossPages()
    {
        var project = Guid.NewGuid();
        for (var i = 0; i < 21; i++)
        {
            var job = await Add("Failure " + i, project);
            await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.NeedsAttention, Unread = true }, _ct);
        }
        var ui = Render<AiActivity>(); await Open(ui); await Button(ui, "Needs attention");
        Assert.Equal(20, ui.FindAll(".ai-activity-job").Count);
        await Button(ui, "Acknowledge alerts");
        ui.WaitForAssertion(() => Assert.Empty(ui.FindAll("[data-ai-attention-count]")));
        Assert.Empty(ui.FindAll(".ai-activity-job"));
        Assert.All((await _store.ReadAsync(_ct)).Jobs, j => { Assert.False(j.Unread); Assert.Equal(AiJobState.NeedsAttention, j.State); });
        await Button(ui, "History"); Assert.Equal(20, ui.FindAll(".ai-activity-job").Count);
        await Button(ui, "Next"); Assert.Single(ui.FindAll(".ai-activity-job"));
    }

    [Fact]
    public async Task ActivityAttentionClearsAfterTheRegularStudioObserverDisplaysTheError()
    {
        var visible = JSInterop.SetupModule("./_content/Lumibelle.UI/ai-jobs.js").Setup<bool>("isReviewVisible", _ => true);
        visible.SetResult(false);
        var job = await Add("Visible in a studio");
        job = await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.NeedsAttention, Unread = true,
            Error = "Download failed", Recovery = AiJobRecovery.RetryOutput }, _ct);
        var ui = Render<AiActivity>();
        var observer = Render<AiResultAcknowledgement>(p => p.Add(c => c.Observed, new[] { job }));
        ui.WaitForAssertion(() => Assert.Equal("1", ui.Find("[data-ai-attention-count]").GetAttribute("data-ai-attention-count")));
        Assert.True((await _store.ReadAsync(_ct)).Jobs.Single().Unread);
        visible.SetResult(true);
        observer.Render(p => p.Add(c => c.Observed, new[] { job }));
        ui.WaitForAssertion(() => Assert.Empty(ui.FindAll("[data-ai-attention-count]")));
        var saved = (await _store.ReadAsync(_ct)).Jobs.Single();
        Assert.False(saved.Unread); Assert.Equal("Download failed", saved.Error);
        Assert.Equal(AiJobRecovery.RetryOutput, saved.Recovery); Assert.Equal(AiJobState.NeedsAttention, saved.State);
        Assert.True(saved.CanRetryCaptured);
    }

    [Fact]
    public async Task ActivityAttentionAStaleSuccessfulReviewCannotClearANewerFailure()
    {
        var visible = JSInterop.SetupModule("./_content/Lumibelle.UI/ai-jobs.js").Setup<bool>("isReviewVisible", _ => true);
        visible.SetResult(false);
        var job = await Add("Batch advanced during review");
        Assert.Equal(job.Id, (await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct))!.Id);
        var displayed = await _store.UpdateAsync(job.Id, j => j with { Unread = true }, _ct);
        var ui = Render<AiActivity>();
        var observer = Render<AiResultAcknowledgement>(p => p.Add(c => c.Observed, new[] { displayed }));
        Assert.Empty(ui.FindAll("[data-ai-attention-count]"));
        var failure = await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.NeedsAttention,
            Error = "A later candidate failed", Unread = true }, _ct);
        await _queue.RefreshAsync(_ct);
        visible.SetResult(true);
        observer.Render(p => p.Add(c => c.Observed, new[] { displayed }));
        ui.WaitForAssertion(() => Assert.Equal("1", ui.Find("[data-ai-attention-count]").GetAttribute("data-ai-attention-count")));
        Assert.True((await _store.ReadAsync(_ct)).Jobs.Single().Unread);
        observer.Render(p => p.Add(c => c.Observed, new[] { failure }));
        ui.WaitForAssertion(() => Assert.Empty(ui.FindAll("[data-ai-attention-count]")));
        Assert.Equal("A later candidate failed", (await _store.ReadAsync(_ct)).Jobs.Single().Error);
    }

    [Fact]
    public async Task ActivityAttentionConfirmedCancellationStopsAnAlreadyReadRemoteAlert()
    {
        var job = await Add("Cancellation pending remotely");
        job = await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.Cancelled, CancelRequested = true,
            RemoteUnconfirmed = true, Unread = false, Recovery = AiJobRecovery.CheckStatus }, _ct);
        var ui = Render<AiActivity>(); await Open(ui); await Button(ui, "Needs attention");
        Assert.Equal("1", ui.Find("[data-ai-attention-count]").GetAttribute("data-ai-attention-count"));
        Assert.Contains("Check status", ui.Find(".ai-activity-job").TextContent);
        Assert.DoesNotContain(ui.FindAll(".ai-job-actions button"), b => b.TextContent == "Mark read");
        await _store.UpdateAsync(job.Id, j => j with { RemoteUnconfirmed = false, Recovery = AiJobRecovery.None }, _ct);
        await _queue.RefreshAsync(_ct);
        ui.WaitForAssertion(() => Assert.Empty(ui.FindAll("[data-ai-attention-count]")));
        Assert.Empty(ui.FindAll(".ai-activity-job"));
        await Button(ui, "History"); Assert.Single(ui.FindAll(".ai-activity-job"));
    }

    [Fact]
    public async Task ActivityAttentionHistoryClearAndRestoreDoNotCreateNewAlerts()
    {
        var job = await Add("Old failure");
        await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.NeedsAttention, Error = "Old error", Unread = true }, _ct);
        var ui = Render<AiActivity>(); await Open(ui); await Button(ui, "History");
        await Button(ui, "Clear history");
        ui.WaitForAssertion(() => Assert.Empty(ui.FindAll("[data-ai-attention-count]")));
        await Button(ui, "Undo"); ui.WaitForAssertion(() => Assert.Single(ui.FindAll(".ai-activity-job")));
        Assert.Empty(ui.FindAll("[data-ai-attention-count]"));
        await Button(ui, "Needs attention"); Assert.Empty(ui.FindAll(".ai-activity-job"));
        // A new failure notification still raises an alert after acknowledgement.
        await _store.UpdateAsync(job.Id, j => j with { Error = "A new failure", Unread = true }, _ct);
        await _queue.RefreshAsync(_ct);
        ui.WaitForAssertion(() => Assert.Equal("1", ui.Find("[data-ai-attention-count]").GetAttribute("data-ai-attention-count")));
        await ui.InvokeAsync(() => Assert.Contains("A new failure", ui.Find(".ai-activity-job").TextContent));
    }

    [Fact]
    public async Task ActivityAttentionSuccessfulResponsesRemainInspectableInHistory()
    {
        JSInterop.SetupModule("./_content/Lumibelle.UI/ai-jobs.js").Setup<bool>("isReviewVisible", _ => true).SetResult(true);
        var job = await Add("Successful result with missing target");
        await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.Completed, Unread = true }, _ct);
        await _store.WriteArtifactAsync(job.Id, AiJobArtifact.Result, new AiTextJobResult("Saved successful response", true), _ct);
        var ui = Render<AiActivity>(); await Open(ui); await Button(ui, "History");
        await Button(ui, "Inspect response");
        ui.WaitForAssertion(() => Assert.Contains("Saved successful response", ui.Markup));
        await _queue.Until(() => Assert.False(_queue.View.Jobs.Single().Unread));
        ui.WaitForAssertion(() => Assert.DoesNotContain(ui.FindAll(".ai-job-actions button"), b => b.TextContent == "Mark read"));
        await ui.InvokeAsync(() => Assert.Empty(ui.FindAll("[data-ai-attention-count]")));
        await Button(ui, "Hide response"); ui.WaitForAssertion(() => Assert.Single(ui.FindAll(".ai-activity-job")));
        Assert.NotNull(await _store.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result, _ct));
    }
}
