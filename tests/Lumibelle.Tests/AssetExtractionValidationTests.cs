using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class AssetExtractionValidationTests
{
    [Fact]
    public void InvalidSceneReferencesIdentifyEachFieldWithoutDiscardingTheRawResponse()
    {
        var scene = ScriptBlock.Create(ScriptBlockKind.Scene, "Station");
        var script = ScriptFixtures.Approved([scene]);
        var request = new AssetExtractionRequest(script, new() { ProjectId = script.ProjectId }, [scene.Id], AiBackend.Vllm, "model");
        var raw = JsonSerializer.Serialize(new[] { new { category = "environment", name = "Station", description = "A stone arch.",
            evidence = new[] { new { label = "Station", sceneId = Guid.NewGuid(), excerpt = "stone arch" } } } });
        var result = AssetExtractor.Inspect(raw, request);
        Assert.Equal(raw, result.RawText); Assert.Empty(result.Proposals);
        Assert.Contains("Asset 1.evidence[1].sceneId", Assert.Single(result.ValidationIssues));
        Assert.Contains("Selected source scenes", result.ValidationError);
        Assert.Null(AssetExtractor.Parse(raw, request));
    }

    [Fact]
    public void SyntaxFailuresExposePositionAndCanBeCorrectedLocally()
    {
        var script = ScriptFixtures.Approved([]);
        var request = new AssetExtractionRequest(script, new() { ProjectId = script.ProjectId }, [], AiBackend.Vllm, "model");
        Assert.Contains("line 1, column", AssetExtractor.Inspect("[{", request).ValidationError);
        Assert.Null(AssetExtractor.Inspect("[]", request).ValidationError);
    }

    [Fact]
    public void PromptSeparatesSyntheticExampleFromSelectedScenesAndKeepsCatalogueTextOnly()
    {
        var scene = ScriptBlock.Create(ScriptBlockKind.Scene, "Forest");
        var script = ScriptFixtures.Approved([scene, ScriptBlock.Create(ScriptBlockKind.Action, "Rowan carries a lantern.")]);
        var request = new AssetExtractionRequest(script, new() { ProjectId = script.ProjectId }, [scene.Id], AiBackend.Vllm, "model");
        var messages = AssetExtractor.BuildMessages(request);
        Assert.Equal(new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User }, messages.Select(m => m.Role));
        Assert.DoesNotContain("Rowan", messages[2].Text); Assert.Contains(scene.Id.ToString(), messages[^1].Text);
        using var example = JsonDocument.Parse(messages[2].Text!);
        Assert.Equal(3, example.RootElement.GetArrayLength());
        using var actual = JsonDocument.Parse(messages[^1].Text![messages[^1].Text!.IndexOf('{')..]);
        Assert.Single(actual.RootElement.GetProperty("selectedScenes").EnumerateArray());
    }
}
