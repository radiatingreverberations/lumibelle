using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ScreenplaySpeakerNormalizationTests
{
    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(32)]
    public void ACompactBriefKeepsAllBeatsIncludingAModestOverrunOfThePromptTarget(int count)
    {
        var brief = new lumibelle.Services.AI.ScriptScenePlan(Enumerable.Range(1, count).Select(n => $"Story change {n}.").ToArray(), [], [], "She closes the door.", "",
            Enumerable.Range(1, 20).Select(n => $"Story fact {n}.").ToArray());
        Assert.True(lumibelle.Services.AI.ScriptScenePlanning.Valid(brief));
        var recovered = lumibelle.Services.AI.ScriptScenePlanning.Parse(JsonSerializer.Serialize(brief));
        Assert.NotNull(recovered);
        Assert.Equal(brief.Beats, recovered.Beats);
        Assert.Equal(brief.Details, recovered.Details);
    }
    [Fact]
    public void ABriefOverrunStillCannotExceedTheContextBudgetOrCollectionLimit()
    {
        var brief = new lumibelle.Services.AI.ScriptScenePlan(Enumerable.Repeat("A substantial story change. " + new string('x', 500), 21).ToArray(), [], [], "The door closes.", "");
        Assert.False(lumibelle.Services.AI.ScriptScenePlanning.Valid(brief));
        Assert.False(lumibelle.Services.AI.ScriptScenePlanning.Valid(brief with { Beats = Enumerable.Repeat("A story change.", 33).ToArray() }));
    }
    [Fact]
    public void ExplicitVoiceCuesAndTheirKnownNamesBecomeCharacterAndDialogueWithoutChangingSpeech()
    {
        const string json = "[{\"kind\":\"Speaker A\",\"spans\":[{\"text\":\"Hello, \",\"italic\":true},{\"text\":\"Speaker B.\"}]},{\"kind\":\"Speaker A (V.O.)\",\"spans\":[{\"text\":\"I remembered.\"}]},{\"kind\":\"Speaker B (O.S.)\",\"spans\":[{\"text\":\"Yes?\"}]}]";
        var parsed = ScreenplayJson.Parse(json);
        Assert.Null(parsed.Error); Assert.Equal(6, parsed.Blocks!.Count);
        Assert.Equal(["SPEAKER A", "Hello, Speaker B.", "SPEAKER A (V.O.)", "I remembered.", "SPEAKER B (O.S.)", "Yes?"], parsed.Blocks.Select(b => b.Text));
        Assert.True(parsed.Blocks[1].Spans[0].Italic);
        Assert.Equal([ScriptBlockKind.Character, ScriptBlockKind.Dialogue, ScriptBlockKind.Character, ScriptBlockKind.Dialogue, ScriptBlockKind.Character, ScriptBlockKind.Dialogue], parsed.Blocks.Select(b => b.Kind));
        Assert.Contains("3 speaker-labelled", Assert.Single(parsed.NormalizationNotes!));
        var corrected = ScriptProposals.CorrectJson(new() { Status = AssistantRunStatus.Completed, Operation = WritingOperation.Revise }, json, Guid.NewGuid());
        Assert.Equal(json, corrected.Output); Assert.NotNull(corrected.NormalizationNotes);
    }
    [Theory]
    [InlineData("Voiceover")]
    [InlineData("CameraMovement")]
    [InlineData("Unknown")]
    public void AmbiguousUnknownKindsRemainInvalid(string kind)
    {
        var parsed = ScreenplayJson.Parse(JsonSerializer.Serialize(new[] { new { kind, spans = new[] { new { text = "Unlabelled writing." } } } }));
        Assert.Null(parsed.Blocks); Assert.Contains("kind", parsed.Error);
    }
    [Fact]
    public void AlreadyValidCharacterAndDialogueBlocksNeedNoNotice()
    {
        var parsed = ScreenplayJson.Parse("[{\"kind\":\"Character\",\"spans\":[{\"text\":\"SPEAKER A (V.O.)\"}]},{\"kind\":\"Dialogue\",\"spans\":[{\"text\":\"I remember.\"}]}]");
        Assert.Null(parsed.Error); Assert.Equal(2, parsed.Blocks!.Count); Assert.Null(parsed.NormalizationNotes);
    }
}
