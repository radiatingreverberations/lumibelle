using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ComfyMultiTakeWorkflowTests
{
    private static AiBatchDefinition Batch(int count = 2) => AiBatchDefinition.Create(Guid.NewGuid(), count, 123);
    private static JsonElement Graph(AiBatchDefinition batch, Func<AiBatchCandidate, object>? build = null) =>
        JsonSerializer.SerializeToElement(ComfyMultiTakeWorkflow.Build(batch.Candidates, build ?? Workflow, "remote-client"));
    private static object Workflow(AiBatchCandidate candidate) => new
    {
        client_id = candidate.Id.ToString("D"),
        prompt = new Dictionary<string, object>
        {
            ["loader"] = new { class_type = "CLIPLoader", inputs = new { clip_name = "encoder" } },
            ["encode"] = new { class_type = "CLIPTextEncode", inputs = new { clip = new object[] { "loader", 0 }, text = "Keep [\"loader\", 0] as literal text" } },
            ["latent"] = new { class_type = "EmptyLatentImage", inputs = new { width = 1024, height = 1024, batch_size = 1 } },
            ["noise"] = new { class_type = "RandomNoise", inputs = new { noise_seed = candidate.Seed } },
            ["sample"] = new { class_type = "SamplerCustomAdvanced", inputs = new { noise = new object[] { "noise", 0 }, conditioning = new object[] { "encode", 0 }, latent_image = new object[] { "latent", 0 } } },
            ["decode"] = new { class_type = "VAEDecode", inputs = new { samples = new object[] { "sample", 0 } } },
            ["output"] = new { class_type = "PreviewImage", inputs = new { images = new object[] { "decode", 0 } } },
            ["capture"] = new { class_type = "SaveLatent", inputs = new { samples = new object[] { "sample", 0 }, filename_prefix = "lumibelle/" + candidate.Id.ToString("D") + "/latent-video" } },
            ["save"] = new { class_type = "SaveVideo", inputs = new { video = new object[] { "decode", 0 }, filename_prefix = "lumibelle/" + candidate.Id.ToString("D") + "/video" } }
        }
    };

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void SharedWorkflowHasOneEncoderAndSeparateSeededOutputsWithoutIncreasingLatentBatch(int count)
    {
        var batch = Batch(count); var workflow = Graph(batch); var nodes = workflow.GetProperty("prompt");
        Assert.Equal("remote-client", workflow.GetProperty("client_id").GetString());
        Assert.Single(nodes.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "CLIPTextEncode");
        Assert.Equal(1, nodes.GetProperty("latent").GetProperty("inputs").GetProperty("batch_size").GetInt32());
        Assert.Equal("Keep [\"loader\", 0] as literal text", nodes.GetProperty("encode").GetProperty("inputs").GetProperty("text").GetString());
        foreach (var c in batch.Candidates)
        {
            string Id(string id) => ComfyMultiTakeWorkflow.Node(c, id);
            JsonElement Input(string id, string input) => nodes.GetProperty(Id(id)).GetProperty("inputs").GetProperty(input);
            Assert.Equal(c.Seed, Input("noise", "noise_seed").GetInt64());
            Assert.Equal(Id("noise"), Input("sample", "noise")[0].GetString());
            Assert.Equal("encode", Input("sample", "conditioning")[0].GetString());
            Assert.Equal(Id("sample"), Input("decode", "samples")[0].GetString());
            Assert.Equal(Id("decode"), Input("output", "images")[0].GetString());
            Assert.Contains(c.Id.ToString("D"), Input("capture", "filename_prefix").GetString()!);
            Assert.Contains(c.Id.ToString("D"), Input("save", "filename_prefix").GetString()!);
        }
        Assert.Equal(count, nodes.EnumerateObject().Count(n => n.Value.GetProperty("class_type").GetString() == "PreviewImage"));
    }

    [Fact]
    public void EqualSeedsStillGetSeparateSamplerAndOutputNodesAndLargeSeedsStayExact()
    {
        var original = Batch();
        var batch = original with { Candidates = original.Candidates.Select(c => c with { Seed = long.MaxValue - 2 }).ToArray() };
        var nodes = Graph(batch).GetProperty("prompt");
        foreach (var c in batch.Candidates)
            Assert.Equal(long.MaxValue - 2, nodes.GetProperty(ComfyMultiTakeWorkflow.Node(c, "noise")).GetProperty("inputs").GetProperty("noise_seed").GetInt64());
        Assert.Equal(2, nodes.EnumerateObject().Count(n => n.Value.GetProperty("class_type").GetString() == "SamplerCustomAdvanced"));
    }

    [Fact]
    public void DifferentConditioningIsNotAccidentallySharedAndInputGraphsAreNotMutated()
    {
        var batch = Batch();
        var originals = batch.Candidates.ToDictionary(c => c.Id, c => JsonSerializer.SerializeToNode(Workflow(c))!);
        originals[batch.Candidates[1].Id]["prompt"]!["encode"]!["inputs"]!["text"] = "Another prompt";
        var before = originals.ToDictionary(p => p.Key, p => p.Value.ToJsonString());
        var nodes = Graph(batch, c => originals[c.Id]).GetProperty("prompt");
        Assert.True(nodes.TryGetProperty("loader", out _)); Assert.False(nodes.TryGetProperty("encode", out _));
        foreach (var c in batch.Candidates)
        {
            Assert.Equal(ComfyMultiTakeWorkflow.Node(c, "encode"), nodes.GetProperty(ComfyMultiTakeWorkflow.Node(c, "sample")).GetProperty("inputs").GetProperty("conditioning")[0].GetString());
            Assert.Equal(before[c.Id], originals[c.Id].ToJsonString());
        }
    }

    [Fact]
    public void GraphWithDanglingLinkIsRejectedBeforeSubmission()
    {
        var batch = Batch();
        Assert.Throws<WorkspaceStoreException>(() => Graph(batch, c =>
        {
            var graph = JsonSerializer.SerializeToNode(Workflow(c))!;
            graph["prompt"]!["sample"]!["inputs"]!["noise"]![0] = "missing";
            return graph;
        }));
    }

    [Fact]
    public void MismatchedCandidateGraphsAreRejected()
    {
        var batch = Batch();
        Assert.Throws<WorkspaceStoreException>(() => Graph(batch, c =>
        {
            var graph = JsonSerializer.SerializeToNode(Workflow(c))!;
            if (c.Number == 2) graph["prompt"]!.AsObject().Remove("save");
            return graph;
        }));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(5)]
    public void UnsupportedBatchSizesAreRejected(int count)
    {
        var candidates = Enumerable.Range(1, count).Select(n => new AiBatchCandidate(Guid.NewGuid(), n, n)).ToArray();
        Assert.Throws<WorkspaceStoreException>(() => ComfyMultiTakeWorkflow.Build(candidates, Workflow, "client"));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void DuplicateCandidateIdentityOrNumberIsRejected(bool duplicateId)
    {
        var candidates = Batch().Candidates.ToArray();
        candidates[1] = duplicateId ? candidates[1] with { Id = candidates[0].Id } : candidates[1] with { Number = candidates[0].Number };
        Assert.Throws<WorkspaceStoreException>(() => ComfyMultiTakeWorkflow.Build(candidates, Workflow, "client"));
    }

    [Fact]
    public void OnlyUntouchedInitialCandidatesAreGroupedAndAppendsStaySeparate()
    {
        var batch = Batch(); var append = new AiBatchCandidate(Guid.NewGuid(), 3, 125, Guid.NewGuid());
        batch = batch with { Candidates = [.. batch.Candidates, append] };
        var empty = new AiJobExecution([]);
        Assert.Equal(batch.Candidates.Take(2), ComfyMultiTakeWorkflow.Group(batch, batch.Candidates[0], empty, false, false));
        Assert.Null(ComfyMultiTakeWorkflow.Group(batch, append, empty, false, false));
        Assert.Null(ComfyMultiTakeWorkflow.Group(batch, batch.Candidates[1], empty, false, false));
        Assert.Null(ComfyMultiTakeWorkflow.Group(batch, batch.Candidates[0], empty, true, false));
        Assert.Null(ComfyMultiTakeWorkflow.Group(batch, batch.Candidates[0], empty, false, true));
        var single = Batch(1);
        Assert.Null(ComfyMultiTakeWorkflow.Group(single, single.Candidates[0], empty, false, false));
    }

    [Fact]
    public void LegacyIndividualReceiptsAreNeverRegroupedButBatchRecoveryKeepsItsOriginalGroup()
    {
        var batch = Batch(); var first = batch.Candidates[0];
        var legacy = new AiJobExecution([new(ComfyMultiTakeWorkflow.CandidateOperation(first), "http://comfy.test", "client", DateTimeOffset.UtcNow)]);
        Assert.Null(ComfyMultiTakeWorkflow.Group(batch, first, legacy, false, false));
        var grouped = new AiJobExecution([new(ComfyMultiTakeWorkflow.Operation, "http://comfy.test", "client", DateTimeOffset.UtcNow)]);
        Assert.Equal(batch.Candidates, ComfyMultiTakeWorkflow.Group(batch, batch.Candidates[1], grouped, false, true));
        var append = new AiBatchCandidate(Guid.NewGuid(), 3, 125, Guid.NewGuid());
        Assert.Null(ComfyMultiTakeWorkflow.Group(batch with { Candidates = [.. batch.Candidates, append] }, append, grouped, false, false));
    }

    [Fact]
    public void OutputProjectionIsolatesOneCandidateAndChecksEveryRequiredOutput()
    {
        var batch = Batch(); var first = batch.Candidates[0]; var second = batch.Candidates[1];
        var history = JsonSerializer.SerializeToElement(new { outputs = new Dictionary<string, object>
        {
            [ComfyMultiTakeWorkflow.Node(first, "14")] = new { videos = new[] { new { filename = "one.mp4" } } },
            [ComfyMultiTakeWorkflow.Node(second, "14")] = new { videos = new[] { new { filename = "two.mp4" } } },
            [ComfyMultiTakeWorkflow.Node(second, "15")] = new { images = new[] { new { filename = "two.webp" } } },
            [ComfyMultiTakeWorkflow.Node(second, "21")] = new { latents = new[] { new { filename = "two-video.latent" } } },
            [ComfyMultiTakeWorkflow.Node(second, "22")] = new { latents = new[] { new { filename = "two-audio.latent" } } }
        } });
        Assert.False(ComfyMultiTakeWorkflow.HasOutputs(history, first, ["14", "15", "21", "22"]));
        Assert.True(ComfyMultiTakeWorkflow.HasOutputs(history, second, ["14", "15", "21", "22"]));
        var outputs = ComfyMultiTakeWorkflow.Output(history, second).GetProperty("outputs");
        Assert.Equal(4, outputs.EnumerateObject().Count());
        Assert.Equal("two.mp4", outputs.GetProperty("14").GetProperty("videos")[0].GetProperty("filename").GetString());
        Assert.DoesNotContain("one.mp4", outputs.GetRawText());
    }

    [Fact]
    public void ProgressAndTimingsAreRoutedPerTakeWithSharedPreparationChargedOnce()
    {
        var batch = Batch(); var first = batch.Candidates[0]; var second = batch.Candidates[1];
        var options = new ComfyExecutionOptions(new Dictionary<string, ComfyNodeStage>
        {
            ["5"] = new(GenerationPhase.Preparing, "Conditioning"),
            ["10"] = new(GenerationPhase.Generating, "Sampling", "Sampling", "steps", "sampling")
        }, "rejected", "execution", "submit", "timeout", "unreadable", "connection")
        { TimingNodes = new Dictionary<string, string> { ["5"] = "Preparation", ["10"] = "Sampling" } };
        var expanded = ComfyMultiTakeWorkflow.Options(options, batch.Candidates);
        Assert.Equal("Shared/Preparation", expanded.TimingNodes["5"]);
        Assert.Equal("take_2_Sampling", expanded.TimingNodes[ComfyMultiTakeWorkflow.Node(second, "10")]);
        Assert.Contains("Take 2", expanded.Nodes[ComfyMultiTakeWorkflow.Node(second, "10")].ProgressLabel!);
        Assert.NotEqual(expanded.Nodes[ComfyMultiTakeWorkflow.Node(first, "10")].EstimateScope, expanded.Nodes[ComfyMultiTakeWorkflow.Node(second, "10")].EstimateScope);
        Assert.Equal(2, ComfyMultiTakeWorkflow.ProgressCandidate(new(GenerationPhase.Generating, "Sampling") { ExecutionStageId = "take_2_10" }, batch.Candidates));
        Assert.Null(ComfyMultiTakeWorkflow.ProgressCandidate(new(GenerationPhase.Preparing, "Conditioning") { ExecutionStageId = "5" }, batch.Candidates));
        var timings = new ComfyObservedTimings(new Dictionary<string, double> { ["Shared/Preparation"] = 10, ["take_1_Sampling"] = 20, ["take_2_Sampling"] = 22 }, false);
        Assert.Equal(10d, ComfyMultiTakeWorkflow.Seconds(timings, first, "Preparation", true));
        Assert.Null(ComfyMultiTakeWorkflow.Seconds(timings, second, "Preparation", false));
        Assert.Equal(22d, ComfyMultiTakeWorkflow.Seconds(timings, second, "Sampling", false));
        Assert.Null(ComfyMultiTakeWorkflow.Seconds(timings with { Partial = true }, first, "Preparation", true));
        Assert.Null(ComfyMultiTakeWorkflow.Seconds(null, first, "Sampling", true));
    }
}
