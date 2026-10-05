using Bunit;
using lumibelle.Components.AI;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Fact]
    public void CodexDisclosureFollowsActivityAndKeepsExpansionOnlyForTheSameRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var job = new AiJobHeader { Id = Guid.NewGuid(), Kind = AiJobKind.ImageCreate, Backend = AiBackend.Codex,
            Target = new(Guid.NewGuid()), ProjectName = "Test", TargetName = "Image", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test",
            State = AiJobState.Running, CreatedUtc = now.AddSeconds(-50), StartedUtc = now.AddSeconds(-20) };
        var timing = new CodexImageTiming(now.AddSeconds(-20), []);
        AiJobProgress Progress(CodexImageTiming value) => new(new(GenerationPhase.Generating, "Codex agent is working", Elapsed: TimeSpan.FromSeconds(3)), 1, 2, CodexTiming: value);
        var ui = Render<AiJobProgressView>(p => p.Add(c => c.Job, job).Add(c => c.Queue, _queue.View).Add(c => c.Progress, Progress(timing)));
        var details = ui.Find(".codex-image-timing");
        Assert.False(details.HasAttribute("open"));
        Assert.Equal("Preparing request…", details.QuerySelector("summary")!.TextContent);
        ui.Find(".codex-image-timing > summary").Click();
        timing = timing with { ImageCalls = [new("image", now.AddSeconds(-5), null)] };
        ui.Render(p => p.Add(c => c.Progress, Progress(timing)));
        Assert.True(ui.Find(".codex-image-timing").HasAttribute("open"));
        Assert.Equal("Image tool running…", ui.Find(".codex-image-timing > summary").TextContent);
        Assert.Single(ui.FindAll(".generation-progress-meta"));
        Assert.DoesNotContain("Codex agent is working", ui.Find(".generation-progress-label").TextContent);
        timing = timing with { ImageCalls = [new("image", now.AddSeconds(-5), now.AddSeconds(-1))] };
        ui.Render(p => p.Add(c => c.Progress, Progress(timing) with { Candidate = 2 }));
        Assert.Equal("Agent follow-up…", ui.Find(".codex-image-timing > summary").TextContent);
        Assert.True(ui.Find(".codex-image-timing").HasAttribute("open"));
        ui.Render(p => p.Add(c => c.Job, job with { Id = Guid.NewGuid() }));
        Assert.False(ui.Find(".codex-image-timing").HasAttribute("open"));
        ui.Render(p => p.Add(c => c.Progress, Progress(timing with { CompletedUtc = now })));
        Assert.Equal("Timing details", ui.Find(".codex-image-timing > summary").TextContent);
    }

    [Fact]
    public void RequestControlKeepsInspectionEnabledAndRoutesEveryStateWithoutResubmitting()
    {
        var starts = 0; var reviews = 0;
        var job = new AiJobHeader { Kind = AiJobKind.ShotPlanning, Backend = AiBackend.OpenRouter, Target = new(Guid.NewGuid()), ProjectName = "Test", TargetName = "Draft shots", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test", Id = Guid.NewGuid(), State = AiJobState.Waiting, CreatedUtc = DateTimeOffset.UtcNow.AddSeconds(-42) };
        var ui = Render<TextRequestAction>(p => p.Add(c => c.Label, "Draft shots").Add(c => c.OnStart, () => starts++)
            .Add(c => c.OnReview, () => reviews++));
        ui.Find(".request-action-button").Click(); Assert.Equal(1, starts);
        ui.Render(p => p.Add(c => c.Preparing, true));
        Assert.True(ui.Find(".request-action-button").HasAttribute("disabled"));
        Assert.Contains("Preparing request", ui.Markup);
        ui.Render(p => p.Add(c => c.Preparing, false).Add(c => c.Disabled, true).Add(c => c.Request, new(job, "Drafting shots…")));
        Assert.False(ui.Find(".request-action-button").HasAttribute("disabled"));
        Assert.Contains("waiting", ui.Find(".request-time").TextContent);
        ui.Find(".request-action-button").Click(); Assert.Equal(1, reviews); Assert.Equal(1, starts);
        job = job with { State = AiJobState.Running, StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-12) };
        ui.Render(p => p.Add(c => c.Request, new(job, "Drafting shots…")));
        Assert.Single(ui.FindAll(".request-spinner"));
        Assert.Contains("elapsed", ui.Find(".request-time").TextContent);
        // The button is the only control: a running request opens where it can be cancelled.
        ui.Find(".request-action-button").Click(); Assert.Equal(2, reviews);
        Assert.Empty(ui.FindAll(".request-action-menu, details"));
        ui.Render(p => p.Add(c => c.Request, new(job with { CancelRequested = true }, "Drafting shots…")));
        Assert.Contains("Cancellation requested", ui.Find(".request-action-button").TextContent);
        foreach (var (outcome, label) in new[] { (TextRequestOutcome.Proposal, "Review changes"), (TextRequestOutcome.Response, "View response"), (TextRequestOutcome.Invalid, "Needs attention") }) {
            ui.Render(p => p.Add(c => c.Request, new(job with { State = AiJobState.Completed }, "Drafting shots…", outcome)));
            Assert.Contains(label, ui.Find(".request-action-button").TextContent);
            ui.Find(".request-action-button").Click();
        }
        Assert.Equal(5, reviews);
        Assert.Equal(1, starts);
        ui.Render(p => p.Add(c => c.Request, new(job with { State = AiJobState.Completed }, "Drafting shots…", TextRequestOutcome.Resolved)));
        Assert.Contains("Draft shots", ui.Find(".request-action-button").TextContent);
        ui.Render(p => p.Add(c => c.Request, new(job with { State = AiJobState.Cancelled }, "Drafting shots…")));
        Assert.Contains("Draft shots", ui.Find(".request-action-button").TextContent);
    }
}
