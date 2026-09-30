using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static JsonNode PerformanceCatalog()
    {
        var root = TurboCatalog();
        root["ModelAttentionBackend"] = JsonNode.Parse("""
            {"input":{"required":{"model":["MODEL"],"attention":[["pytorch attention","comfy kitchen attention"]]}},"output":["MODEL"]}
            """);
        root[H3Performance.SageNode] = JsonNode.Parse("""
            {"input":{"required":{"model":["MODEL"],"sage_attention":[["disabled","auto"]]},"optional":{"allow_compile":["BOOLEAN",{"default":false}]}},"output":["MODEL"]}
            """);
        root["BlockSparseAttention"] = JsonNode.Parse("""
            {"input":{"required":{
                "model":["MODEL"],
                "selection":["COMFY_DYNAMICCOMBO_V3",{"options":[{"key":"Sol-Attn (adaptive tau)","inputs":{"required":{"tau":["FLOAT",{"min":0,"max":4}]}}}]}],
                "start_percent":["FLOAT",{"min":0,"max":1}],"end_percent":["FLOAT",{"min":0,"max":1}],
                "dense_blocks":["STRING"],"min_tokens":["INT",{"min":0,"max":1048576}],"extra_tokens":["INT",{"min":0,"max":256}],
                "sink_conditioning":["COMBO",{"options":["exact_kv","exact_kv_and_rows","off"]}],"verbose":["BOOLEAN"]}},"output":["MODEL"]}
            """);
        root["SaveAnimatedWEBP"]!["input"]!["required"]!["method"] = new JsonArray("COMBO", new JsonObject { ["options"] = new JsonArray("default", "fastest", "slowest") });
        return root;
    }

    public static IEnumerable<object[]> PerformanceGraphs()
    {
        foreach (var backend in Enum.GetValues<H3AttentionBackend>())
        foreach (var sol in new[] { false, true })
        foreach (var steps in new[] { 20, 4, 8 }) yield return [backend, sol, steps];
    }
    [Theory, MemberData(nameof(PerformanceGraphs))]
    public void PerformanceGraphPreservesSamplingAndMedia(H3AttentionBackend backend, bool sol, int steps)
    {
        var shot = Ready(); shot.Turbo = steps != 20; shot.TurboSteps = steps == 8 ? 8 : 4;
        var snapshot = Snapshot(Guid.NewGuid(), shot);
        JsonElement Graph(VideoSnapshot s) => JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(s, 123, "performance-test", [])).GetProperty("prompt");
        var original = Graph(snapshot);
        var profile = H3Performance.Capture(new() { Attention = backend, SolAttention = sol });
        var graph = Graph(snapshot with { Performance = profile });
        JsonElement Input(string id, string name) => graph.GetProperty(id).GetProperty("inputs").GetProperty(name);
        var baseModel = steps == 8 ? "18" : steps == 4 ? "16" : "1";
        Assert.Equal(sol ? "31" : backend == H3AttentionBackend.ServerDefault ? baseModel : "30", Input("6", "model")[0].GetString());
        Assert.Equal("fastest", Input("15", "method").GetString());
        Assert.True(Input("15", "lossless").GetBoolean()); Assert.Equal(80, Input("15", "quality").GetInt32());
        if (backend == H3AttentionBackend.ServerDefault) Assert.False(graph.TryGetProperty("30", out _));
        else
        {
            Assert.Equal(baseModel, Input("30", "model")[0].GetString());
            if (backend == H3AttentionBackend.Sage)
            {
                Assert.Equal(H3Performance.SageNode, graph.GetProperty("30").GetProperty("class_type").GetString());
                Assert.Equal("auto", Input("30", "sage_attention").GetString()); Assert.False(Input("30", "allow_compile").GetBoolean());
            }
            else Assert.Equal(backend == H3AttentionBackend.Kitchen ? "comfy kitchen attention" : "pytorch attention", Input("30", "attention").GetString());
        }
        if (sol)
        {
            Assert.Equal(backend == H3AttentionBackend.ServerDefault ? baseModel : "30", Input("31", "model")[0].GetString());
            Assert.Equal(1.3, Input("31", "selection.tau").GetDouble());
            Assert.Equal("exact_kv_and_rows", Input("31", "sink_conditioning").GetString());
            Assert.Equal(12288, Input("31", "min_tokens").GetInt32()); Assert.Equal(256, Input("31", "extra_tokens").GetInt32());
        }
        else Assert.False(graph.TryGetProperty("31", out _));
        foreach (var node in original.EnumerateObject().Where(n => n.Name is not ("6" or "15")))
            Assert.True(JsonElement.DeepEquals(node.Value, graph.GetProperty(node.Name)), "Existing node changed: " + node.Name);
    }
    [Fact]
    public void LegacySnapshotsIgnoreNewSettingsDefaults()
    {
        var snapshot = Snapshot(Guid.NewGuid(), Ready());
        var original = JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 12, "same", []));
        snapshot.Settings.Performance = new() { Attention = H3AttentionBackend.Sage, SolAttention = true };
        var loaded = ShotCopy.Of(snapshot);
        Assert.Null(loaded.Performance);
        Assert.True(JsonElement.DeepEquals(original, JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(loaded, 12, "same", []))));
        Assert.Equal(H3ArchiveCompression.Fast, JsonSerializer.Deserialize<H3Settings>("{}")!.Performance.ArchiveCompression);
        var profile = H3Performance.Capture(snapshot.Settings.Performance);
        Assert.Equal(profile, ShotCopy.Of(snapshot with { Performance = profile }).Performance);
        Assert.Throws<WorkspaceStoreException>(() => H3Performance.Validate(profile with { Tau = .5 }));
        Assert.Throws<WorkspaceStoreException>(() => H3Performance.Validate(profile with { Adapter = "arbitrary-node" }));
        Assert.Throws<WorkspaceStoreException>(() => H3Performance.Validate(profile with { ArchiveMethod = "lossy" }));
    }
    [Theory]
    [InlineData("kitchen")][InlineData("sage-node")][InlineData("sage-input")][InlineData("sol-node")][InlineData("sol-range")][InlineData("sol-audio")][InlineData("webp")]
    public void PerformanceCapabilitiesBlockOnlySelectedOptions(string missing)
    {
        var root = PerformanceCatalog(); var p = new H3PerformancePreferences();
        switch (missing)
        {
            case "kitchen": root["ModelAttentionBackend"]!["input"]!["required"]!["attention"]![0] = new JsonArray("pytorch attention"); p.Attention = H3AttentionBackend.Kitchen; break;
            case "sage-node": root.AsObject().Remove(H3Performance.SageNode); p.Attention = H3AttentionBackend.Sage; break;
            case "sage-input": root[H3Performance.SageNode]!["input"]!["optional"]!.AsObject().Remove("allow_compile"); p.Attention = H3AttentionBackend.Sage; break;
            case "sol-node": root.AsObject().Remove("BlockSparseAttention"); p.SolAttention = true; break;
            case "sol-range": root["BlockSparseAttention"]!["input"]!["required"]!["extra_tokens"]![1]!["max"] = 128; p.SolAttention = true; break;
            case "sol-audio": root["BlockSparseAttention"]!["input"]!["required"]!["sink_conditioning"]![1]!["options"] = new JsonArray("off"); p.SolAttention = true; break;
            case "webp": root["SaveAnimatedWEBP"]!["input"]!["required"]!["method"] = new JsonArray(new JsonArray("default")); break;
        }
        var catalog = JsonSerializer.SerializeToElement(root);
        var selected = ComfyH3Video.Inspect(catalog, new() { Performance = p });
        Assert.Equal(missing.StartsWith("sol-") || missing == "webp", selected.Ready(Ready())); Assert.NotNull(selected.SelectedPerformanceIssue);
        var captured = Snapshot(Guid.NewGuid(), Ready()) with { Performance = H3Performance.Capture(p) };
        Assert.Throws<WorkspaceStoreException>(() => H3Presets.CheckSubmission(captured, selected));
        Assert.True(selected.StandardReady);
        var legacy = ComfyH3Video.Inspect(catalog, new() { Performance = H3Performance.Preferences(null) });
        Assert.True(legacy.Ready(Ready()), legacy.Issue(Ready()));
    }
    [Fact]
    public void MalformedOptionalPerformanceContractsAreUnavailable()
    {
        var root = PerformanceCatalog();
        root["ModelAttentionBackend"]!["output"] = new JsonArray(42);
        root[H3Performance.SageNode]!["input"]!["required"]!["sage_attention"] = new JsonArray();
        root["BlockSparseAttention"]!["input"]!["required"]!["selection"]![1] = "invalid schema";
        var c = H3Performance.Inspect(JsonSerializer.SerializeToElement(root));
        Assert.NotNull(c.PyTorchIssue); Assert.NotNull(c.KitchenIssue); Assert.NotNull(c.SageIssue); Assert.NotNull(c.SolIssue);
        Assert.Null(c.FastArchiveIssue);
    }
    [Fact]
    public void AllPerformanceContractsAvailableAndSageDependencyErrorIsActionable()
    {
        var root = JsonSerializer.SerializeToElement(PerformanceCatalog());
        foreach (var backend in Enum.GetValues<H3AttentionBackend>())
            Assert.True(ComfyH3Video.Inspect(root, new() { Performance = new() { Attention = backend, SolAttention = true } }).Ready(Ready()));
        var failure = JsonSerializer.SerializeToElement(new { status = new { messages = new object[] { new object[] {
            "execution_error", new { node_type = H3Performance.SageNode, exception_message = "No module named sageattention" } } } } });
        Assert.Contains("sageattention package", ComfyExecutionMonitor.ExecutionFailureMessage(ComfyH3Video.MonitorOptions, failure));
    }

    [Fact]
    public void TextOutOfMemoryFailuresExplainHowToShrinkTheRequest()
    {
        var failure = JsonSerializer.SerializeToElement(new { status = new { messages = new object[] { new object[] {
            "execution_error", new { node_type = "TextGenerate", exception_type = "torch.OutOfMemoryError",
                exception_message = "Allocation on device 0 would exceed allowed memory. (out of memory)" } } } } });
        Assert.Equal(ComfyChatClient.OutOfMemoryMessage, ComfyExecutionMonitor.ExecutionFailureMessage(ComfyChatClient.ExecutionOptions, failure));
        // Workflows without a specific message keep the raw ComfyUI failure.
        Assert.Contains("OutOfMemoryError", ComfyExecutionMonitor.ExecutionFailureMessage(ComfyH3Video.MonitorOptions, failure));
    }
}

public sealed class ComfyTimingTests
{
    private sealed class TimestampClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    [Fact]
    public void ObservedIntervalsAggregateRepeatedArchiveNodesAndExcludeQueueTime()
    {
        var clock = new TimestampClock(); var tracker = new ComfyTimingTracker(clock, ComfyH3Video.MonitorOptions.TimingNodes);
        clock.Advance(100); tracker.Start(); tracker.Executing("5"); clock.Advance(2);
        tracker.Executing("10"); clock.Advance(20); tracker.Executing("11"); clock.Advance(3);
        tracker.Executing("15"); clock.Advance(4); tracker.Executing("15"); clock.Advance(5); tracker.End();
        var result = tracker.Snapshot(); Assert.False(result.Partial);
        Assert.Equal(2, result.Seconds["Preparation"]); Assert.Equal(20, result.Seconds["Sampling"]);
        Assert.Equal(3, result.Seconds["Decoding"]); Assert.Equal(9, result.Seconds["Archive"]);
    }
    [Fact]
    public void MissingEventsAndDisconnectionsNeverFabricateCompletedStageTimings()
    {
        var clock = new TimestampClock(); var tracker = new ComfyTimingTracker(clock, ComfyH3Video.MonitorOptions.TimingNodes);
        tracker.Executing("5"); clock.Advance(4); tracker.Executing("10"); clock.Advance(3);
        Assert.Empty(tracker.Snapshot().Seconds);
        tracker.Executing("11"); Assert.Equal(3, tracker.Snapshot().Seconds["Sampling"]);
        tracker.Gap(); clock.Advance(1000); tracker.End();
        Assert.True(tracker.Snapshot().Partial); Assert.Empty(tracker.Snapshot().Seconds);
    }
    [Fact]
    public void CachedStagesAreMeasuredAsZeroAndHistoryOnlyRecoveryIsUnavailable()
    {
        var tracker = new ComfyTimingTracker(TimeProvider.System, ComfyH3Video.MonitorOptions.TimingNodes);
        Assert.Empty(tracker.Snapshot().Seconds); Assert.True(tracker.Snapshot().Partial);
        tracker.Start(); tracker.Cached(["1", "2", "5"]); tracker.End();
        Assert.Equal(0, tracker.Snapshot().Seconds["Preparation"]);
    }
    [Fact]
    public void UpscalingIntervalsAreSeparateAndLostObservationIsUnavailable()
    {
        var clock = new TimestampClock(); var tracker = new ComfyTimingTracker(clock, ComfyH3Video.MonitorOptions.TimingNodes);
        tracker.Start(); tracker.Executing("10"); clock.Advance(20); tracker.Executing("40"); clock.Advance(1);
        tracker.Executing("41"); clock.Advance(5); tracker.Executing("42"); clock.Advance(1); tracker.Executing("11"); clock.Advance(3); tracker.End();
        Assert.Equal(20, tracker.Snapshot().Seconds["Sampling"]); Assert.Equal(7, tracker.Snapshot().Seconds["Upscaling"]);
        Assert.Equal(3, tracker.Snapshot().Seconds["Decoding"]);
        tracker.Gap(); Assert.True(tracker.Snapshot().Partial); Assert.Empty(tracker.Snapshot().Seconds);
    }
}
