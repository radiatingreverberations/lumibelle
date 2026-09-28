using System.Text.Json;
using Bunit;
using lumibelle.Components.AI;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

[Trait("Category", "Component")]
public sealed class CodexImageTimingTests
{
    private static DateTimeOffset At(int seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void ProviderTimestampsSeparatePreparationImageAndFollowUpDespiteDelayedDelivery()
    {
        var clock = new TimingClock { Now = At(2000) };
        var tracker = new CodexImageTimingTracker(Json(new { startedAt = 1000 }), At(999), clock);
        Assert.Equal(TimeSpan.FromSeconds(3), tracker.Snapshot.DurationsAt(At(1003))!.Preparation);
        tracker.StartImage(Json(new { startedAtMs = 1005000 }), "one");
        var running = tracker.Snapshot;
        Assert.Equal(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.Zero), running.DurationsAt(At(1015)));
        tracker.FinishImage(Json(new { completedAtMs = 1028000 }), "one");
        Assert.Null(running.ImageCalls[0].CompletedUtc); // Published checkpoints are immutable.
        tracker.Complete(Json(new { emittedAtMs = 1030000 }), Json(new { durationMs = 30000 }));
        Assert.Equal(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(2)), tracker.Snapshot.DurationsAt(At(3000)));
    }

    [Fact]
    public void MultipleCallsIncludeAgentGapsAndDoNotDoubleCountOverlappingImageTime()
    {
        var timing = new CodexImageTiming(At(1000), [new("one", At(1005), At(1015)), new("two", At(1010), At(1020)), new("three", At(1023), At(1033))], At(1035));
        Assert.Equal(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(5)), timing.DurationsAt(At(2000)));
    }

    [Fact]
    public void MissingLifecycleEventsLeaveTheSplitUnknownAndMissingTimestampsUseObservedTime()
    {
        var clock = new TimingClock { Now = At(1000) };
        var tracker = new CodexImageTimingTracker(Json(new { startedAt = (long?)null }), At(1000), clock);
        clock.Now = At(1005); tracker.StartImage(Json(new { startedAtMs = (long?)null }), "one");
        clock.Now = At(1025); tracker.FinishImage(Json(new { }), "one");
        clock.Now = At(1030); tracker.Complete(Json(new { }), Json(new { durationMs = (long?)null }));
        Assert.Equal(new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5)), tracker.Snapshot.DurationsAt(At(2000)));
        tracker.FinishImage(Json(new { }), "missing-start");
        Assert.Null(tracker.Snapshot.DurationsAt(At(2000)));
        Assert.Null(new CodexImageTiming(At(1000), [new("missing-end", At(1005))], At(1030)).DurationsAt(At(2000)));
    }

    [Fact]
    public void SavedTimingShowsHonestLabelsAndStaysFixedAfterReload()
    {
        using var context = new BunitContext();
        var clock = new TimingClock { Now = At(2000) }; context.Services.AddSingleton<TimeProvider>(clock);
        var timing = new CodexImageTiming(At(1000), [new("one", At(1005), At(1028))], At(1030));
        var restored = JsonSerializer.Deserialize<CodexImageTiming>(JsonSerializer.Serialize(timing))!;
        var ui = context.Render<CodexImageTimingView>(p => p.Add(c => c.Timing, restored));
        Assert.Equal(new[] { "Agent preparation", "Image tool", "Agent follow-up" }, ui.FindAll("dt").Select(e => e.TextContent));
        Assert.Equal(new[] { "≈5s", "≈23s", "≈2s" }, ui.FindAll("dd").Select(e => e.TextContent));
        Assert.Contains("including waiting", ui.Markup);
        clock.Now = At(4000); ui.Render();
        Assert.Equal(new[] { "≈5s", "≈23s", "≈2s" }, ui.FindAll("dd").Select(e => e.TextContent));
        ui.Render(p => p.Add(c => c.Timing, restored with { ImageCalls = [new("unknown", null, At(1028))] }));
        Assert.Contains("Timing breakdown unavailable", ui.Markup); Assert.Empty(ui.FindAll("dd"));
    }

    private sealed class TimingClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
