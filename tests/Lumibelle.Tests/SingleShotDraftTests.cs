using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class SingleShotDraftTests
{
    private static (ShotPlanningRequest Request, ScriptDocument Doc) Fixture(string directions = "A close-up of the key in her hand")
    {
        var doc = ScriptFixtures.Document();
        var existing = new Shot { Title = "Juniper answers", Duration = 4, Description = "Juniper turns to the door.", SourceBlockIds = [doc.Blocks[0].Id],
            Dialogue = [new() { Speaker = "JUNIPER", Language = "English", Text = "You called?" }] };
        var request = new ShotPlanningRequest(ScriptFixtures.Approved(doc.Blocks, doc.ProjectId), new() { ProjectId = doc.ProjectId },
            [doc.Blocks[0].Id], 8, directions, new(AiBackend.OpenRouter, "mock", "Mock"))
        { SingleShot = new(Guid.NewGuid(), 1, [SceneShotSummary.From(existing)], null) };
        return (request, doc);
    }
    private static string Reply(ScriptDocument doc, int count = 1) => JsonSerializer.Serialize(Enumerable.Range(0, count).Select(i => new {
        title = "The key", sceneId = doc.Blocks[0].Id, sourceBlockIds = new[] { doc.Blocks[0].Id }, duration = 3,
        description = "Close on the key turning in Juniper's hand.", dialogue = Array.Empty<object>(), characters = new[] { new { name = "JUNIPER" } },
        atmosphere = "Quiet room.", music = "No music." }), AtomicJsonFile.Options);

    [Fact]
    public void TheRequestCarriesTheSceneTheOtherShotsAndThePlaceForOneShot()
    {
        var (request, _) = Fixture();
        var messages = ShotPlanner.BuildMessages(request);
        Assert.Contains("ONE shot", messages[0].Text);
        var task = JsonNode.Parse(messages[1].Text)!;
        Assert.Equal(1, (int)task["newShotPosition"]!);
        Assert.Equal("Juniper answers", (string)task["existingShots"]![0]!["title"]!);
        Assert.Equal("A close-up of the key in her hand", (string)task["directions"]!);
        Assert.Equal("single-shot-v1", ShotPlanner.Profile(request));
    }

    [Fact]
    public void OneShotIsReadAndOtherShotsCountAsCoverage()
    {
        var (request, doc) = Fixture();
        var result = ShotPlanner.Parse(Reply(doc), request);
        Assert.Null(result.Error);
        var shot = Assert.Single(result.Shots);
        Assert.Equal("The key", shot.Title);
        Assert.Equal("single-shot-v1", shot.Planning!.Profile);
        // "You called?" is spoken by the existing shot, so it is not reported as uncovered.
        Assert.DoesNotContain(result.UncoveredDialogue, line => line.Contains("You called?"));
    }

    [Fact]
    public void MoreThanOneShotIsNotApplicable()
    {
        var (request, doc) = Fixture();
        var result = ShotPlanner.Parse(Reply(doc, 2), request);
        Assert.Empty(result.Shots);
        Assert.Contains("instead of one", result.Error);
    }
}
