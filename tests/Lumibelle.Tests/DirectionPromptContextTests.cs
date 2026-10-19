using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class DirectionPromptContextTests
{
    [Fact]
    public void PromptDirectionKeepsReviewedMeaningWithoutRepeatingReelHistory()
    {
        var media = new ReferenceVideoMedia(Guid.NewGuid(), new('a', 64), 100, 640, 360, 360, 24, 15, false);
        var frame = new ReelKeyframe { Name = "Podium side", Notes = "Gate beyond podium", Frame = new(media.Id, media.Sha256, 24, 1), Crop = new() { Width = .5 } };
        var reel = new AssetReferenceReel { AssetId = Guid.NewGuid(), Name = "Plaza circuit", Media = media,
            Keyframes = new() { Frames = [frame, new() { Frame = new(media.Id, media.Sha256, 72, 3), Notes = "UNSELECTED_FRAME_HISTORY" }] },
            Generation = new(new() { Prompt = "GENERATION_HISTORY" + new string('x', 100_000), CheckedInputs = "PRIVATE_CAPTURE_HISTORY" }, Guid.NewGuid(), Guid.NewGuid(), 1, 42, null!) };
        var view = new EnvironmentViewCandidate("reel/selected", frame.Name!, true, frame.Notes, null, reel, frame, new('b', 64));
        var other = view with { Id = "reel/uninspected", Sha256 = new('c', 64), Name = "UNINSPECTED_VIEW" };
        var accepted = new AcceptedDraftingEnvironment(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reel.AssetId,
            "Plaza", "Keep the stonework", "Riley cannot see over the podium", [view, other], "fingerprint"),
            new() { Summary = "Podium obstructs the reaction", Claims = [new("Gate behind podium", ReferenceSupportStatus.Supported, [view.Id])] })
            { InspectionImageHashes = [view.Sha256] };
        var context = new ShotDirectionContext(new() { VisualProgression = "Reveal the reaction" },
            new() { Purpose = "Conceal recognition" }, new() { Facing = "Toward the magistrate" },
            new() { CutOut = "When she turns" }, new() { Assumptions = "Side position is inferred" }, ["Timing needs review"])
            { Environment = accepted };
        var before = JsonSerializer.Serialize(context, AtomicJsonFile.Options);
        var compact = JsonSerializer.Serialize(ShotDirections.PromptContext(context), AtomicJsonFile.Options);
        Assert.True(before.Length > 200_000);
        Assert.True(compact.Length < 5_000);
        foreach (var needed in new[] { "Conceal recognition", "Toward the magistrate", "When she turns", "Side position is inferred", "Timing needs review",
            "Riley cannot see over the podium", "Podium obstructs the reaction", view.Id, view.Sha256, "Podium side", "Gate beyond podium" }) Assert.Contains(needed, compact);
        foreach (var excluded in new[] { "GENERATION_HISTORY", "PRIVATE_CAPTURE_HISTORY", "UNSELECTED_FRAME_HISTORY", "UNINSPECTED_VIEW" }) Assert.DoesNotContain(excluded, compact);
        var json = JsonDocument.Parse(compact).RootElement.GetProperty("environment");
        Assert.Equal(.5, Assert.Single(json.GetProperty("views").EnumerateArray()).GetProperty("crop").GetProperty("width").GetDouble());
        Assert.Equal(before, JsonSerializer.Serialize(context, AtomicJsonFile.Options)); // Captured history stays intact.

        var shot = new Shot { Duration = 5, Description = "She waits behind the podium" };
        var request = new PromptCompositionRequest(Guid.NewGuid(), Guid.NewGuid(), 1, "context", "source", shot, "", [], [], [], [], "", "", "",
            new(AiBackend.OpenRouter, "test/model", "Test")) { DirectionContext = context };
        var message = PromptComposer.BuildMessages(request, [])[1].Text!;
        Assert.Contains("Conceal recognition", message);
        Assert.Contains("Podium obstructs the reaction", message);
        Assert.DoesNotContain("GENERATION_HISTORY", message);
        Assert.True(message.Length < 20_000);
    }
}
