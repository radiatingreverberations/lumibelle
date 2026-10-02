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
        Assert.Contains("ONE new shot", messages[0].Text);
        Assert.Contains("never include dialogue an existing shot already speaks", messages[0].Text);
        var task = JsonNode.Parse(messages[1].Text)!;
        // Position 1 puts the new shot after the existing one, with no shot after it.
        Assert.Equal("Juniper answers", (string)task["previousShot"]!["title"]!);
        Assert.Null(task["nextShot"]);
        Assert.Equal("You called?", (string)task["alreadySpokenDialogue"]![0]!);
        Assert.DoesNotContain(task["unspokenDialogue"]!.AsArray(), line => (string)line! == "You called?");
        Assert.Equal("A close-up of the key in her hand", (string)task["directions"]!);
        Assert.Equal("single-shot-v1", ShotPlanner.Profile(request));
    }

    [Fact]
    public void OnlyTheNeighbouringShotsAreDescribedInFull()
    {
        var (request, doc) = Fixture();
        SceneShotSummary Shot(string title) => new(title, 3, title + " in long detail.", [], [doc.Blocks[0].Id]);
        request = request with { SingleShot = request.SingleShot! with { Position = 2, SceneShots = [Shot("Arrival"), Shot("Door"), Shot("Key"), Shot("Exit")] } };
        var text = ShotPlanner.BuildMessages(request)[1].Text;
        var task = JsonNode.Parse(text)!;
        Assert.Equal("Door", (string)task["previousShot"]!["title"]!);
        Assert.Equal("Key", (string)task["nextShot"]!["title"]!);
        Assert.Equal(["1. Arrival", "4. Exit"], task["otherShotTitles"]!.AsArray().Select(t => (string)t!));
        Assert.DoesNotContain("Arrival in long detail", text);
        Assert.DoesNotContain("Exit in long detail", text);
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
    public void ReusingAnotherShotsDialogueOrTheSceneHeadingIsFlaggedForReview()
    {
        var (request, doc) = Fixture();
        var reply = JsonNode.Parse(Reply(doc))!.AsArray();
        reply[0]!["title"] = ScriptStructure.Sections(doc.Blocks).First().Title;
        reply[0]!["dialogue"] = JsonNode.Parse("[{\"speaker\":\"JUNIPER\",\"language\":\"English\",\"text\":\"You called?\"}]");
        var result = ShotPlanner.Parse(reply.ToJsonString(), request);
        Assert.Null(result.Error);
        Assert.Contains(result.DialogueNotes, note => note.Contains("already spoken in shot 1 of this scene, “Juniper answers”"));
        Assert.Equal("Untitled shot", Assert.Single(result.Shots).Title);
    }

    [Fact]
    public void AScriptLineWithAParentheticalCountsAsSpokenByTheShotThatSpeaksIt()
    {
        var (request, doc) = Fixture();
        var line = doc.Blocks.First(b => b.Kind == ScriptBlockKind.Dialogue);
        var script = doc.Blocks.Select(b => b.Id == line.Id ? ScriptBlock.Create(ScriptBlockKind.Dialogue, "(Into mic, energetic) " + b.Text) : b).ToList();
        var shot = request.SingleShot!.SceneShots[0] with { Dialogue = [new() { Speaker = "JUNIPER", Language = "English", Text = line.Text }] };
        request = request with { Script = ScriptFixtures.Approved(script, doc.ProjectId), SingleShot = request.SingleShot with { SceneShots = [shot] } };
        var task = JsonNode.Parse(ShotPlanner.BuildMessages(request)[1].Text)!;
        Assert.Empty(task["unspokenDialogue"]!.AsArray());
        Assert.Contains("they decide what this shot shows", ShotPlanner.BuildMessages(request)[0].Text);
    }

    [Fact]
    public void ABareShotObjectWithCharacterNamesIsRead()
    {
        var (request, doc) = Fixture();
        var reply = JsonNode.Parse(Reply(doc))!.AsArray()[0]!.AsObject();
        reply["characters"] = JsonNode.Parse("[\"JUNIPER\", \"FIGURE\"]");
        var result = ShotPlanner.Parse(reply.ToJsonString(), request);
        Assert.Null(result.Error);
        Assert.Equal(["JUNIPER", "FIGURE"], Assert.Single(result.Shots).Characters.Select(c => c.Name));
    }

    [Fact]
    public void AListForATextFieldAndAMistypedSourceIdAreRepaired()
    {
        var (request, doc) = Fixture();
        var reply = JsonNode.Parse(Reply(doc))!.AsArray()[0]!.AsObject();
        // As a model returned it: the first group of the UUID is one character short, and music is an empty list.
        reply["sourceBlockIds"] = new JsonArray(doc.Blocks[0].Id.ToString()[1..], "not-a-uuid");
        reply["music"] = new JsonArray();
        reply["atmosphere"] = new JsonArray("Quiet room", "distant traffic");
        reply["characters"] = JsonNode.Parse("[\"JUNIPER\"]");
        var result = ShotPlanner.Parse(reply.ToJsonString(), request);
        Assert.Null(result.Error);
        var shot = Assert.Single(result.Shots);
        Assert.Equal([doc.Blocks[0].Id], shot.SourceBlockIds);
        Assert.Equal("", shot.Music);
        Assert.Equal("Quiet room, distant traffic", shot.Atmosphere);
        Assert.Contains(result.SourceNotes, note => note.Contains("1 source block reference was slightly misspelled"));
        Assert.Contains(result.SourceNotes, note => note.Contains("1 of 2 source block references did not match"));
    }

    [Fact]
    public void AnIdIsRepairedOnlyWhenOneCandidateIsClearlyClosest()
    {
        var a = Guid.Parse("0edffc35-cdbb-4edf-991a-9f3ecc1ac0a7");
        var b = Guid.Parse("0edffc35-cdbb-4edf-991a-9f3ecc1ac0b8");
        Assert.Equal(a, ShotPlanner.Nearest("edffc35-cdbb-4edf-991a-9f3ecc1ac0a7", [a, Guid.NewGuid()]));
        // Equally close to two IDs, or far from every one: left alone.
        Assert.Null(ShotPlanner.Nearest("0edffc35-cdbb-4edf-991a-9f3ecc1ac0c9", [a, b]));
        Assert.Null(ShotPlanner.Nearest(Guid.NewGuid().ToString(), [a, b]));
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
