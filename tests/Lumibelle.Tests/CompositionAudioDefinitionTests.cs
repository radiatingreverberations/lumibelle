using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Production;

namespace Lumibelle.Tests;

// H3 binds an Audio input through its definition line. A composition that explains the input only in
// referenceUsage, which H3 never receives, gets the line added from the shot's voice mapping.
public sealed partial class ShotTests
{
    private static Shot VoiceoverShot()
    {
        var shot = Ready(); shot.Dialogue = [new() { Speaker = "JAN (V.O.)", Language = "English", Text = "Of course, I'd been tied before." }];
        var jan = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Jan Sherwood", Category = AssetCategory.Character };
        var voice = new VoiceReference { AssetId = jan.Id, Name = "Jan reference voice", Duration = 12, Start = 0, ExcerptDuration = 10 };
        var choice = new CharacterVoiceSelection { AssetId = jan.Id, CharacterName = jan.Name, Speaker = "JAN (V.O.)", SpeakerConfirmed = true };
        CharacterVoices.SelectRecording(choice, voice, false);
        CharacterVoices.Set(shot, choice, new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [jan], Voices = [voice] });
        return shot;
    }
    private static JsonObject VoiceoverReply(string definitions, string retention)
    {
        var reply = SectionedReply("Her fingers flex behind her back.", "They tighten, then ease.");
        reply["sections"]!["subject_definitions"] = definitions;
        reply["sections"]!["retention_analysis"] = retention;
        reply["referenceUsage"] = "<Audio 1> supplies JAN (V.O.)'s voice identity only.";
        return reply;
    }

    [Fact]
    public void AnAudioInputExplainedOnlyInReferenceUsageIsDefinedInThePrompt()
    {
        var shot = VoiceoverShot();
        var raw = VoiceoverReply("<Subject 1> is Jan, a pale woman in her forties. Speaker mapping JAN (S1).", "Preserve her appearance.").ToJsonString();
        var parsed = PromptComposer.Parse(raw, SoundRequest(shot));
        Assert.Contains("<Audio 1> is the voice-timbre reference for <Subject 1>, JAN (V.O.) (S1).", parsed.Prompt);
        Assert.Contains("<Audio 1>: reference - JAN (V.O.) (S1) follows <Audio 1>'s voice timbre without copying its words.", parsed.Prompt);
        // The added line satisfies the speaker mapping check, so the only note is the one saying it was added.
        Assert.Equal("The response did not name <Audio 1> in the prompt sections, so Lumibelle added its definition from the shot's voice mapping. Check the wording.", Assert.Single(parsed.Notes));
        Assert.Null(ProductionPolicy.PromptWarning(parsed.Prompt, shot));
    }

    [Fact]
    public void AnAudioInputTheResponseDefinesIsLeftAsWritten()
    {
        var shot = VoiceoverShot();
        var definitions = "<Subject 1> is Jan. <Audio 1> is the voice of JAN (V.O.) (S1).";
        var parsed = PromptComposer.Parse(VoiceoverReply(definitions, "<Audio 1>: reference - her voice follows <Audio 1>.").ToJsonString(), SoundRequest(shot));
        Assert.Contains("subject_definitions:\n" + definitions + "\n\n", parsed.Prompt);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(parsed.Prompt, "<Audio 1> is"));
        Assert.Empty(parsed.Notes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AudioInstructionsShowThisShotsOwnDefinitionAndRetentionLines(bool structured)
    {
        var instructions = PromptComposer.BuildMessages(SoundRequest(VoiceoverShot()), [], structured: structured)[0].Text;
        Assert.Contains("<Audio 1> is the voice-timbre reference for <Subject 1>, JAN (V.O.) (S1).", instructions);
        Assert.Contains("<Audio 1>: reference - JAN (V.O.) (S1) follows <Audio 1>'s voice timbre without copying its words.", instructions);
        Assert.Contains("referenceUsage, which is not part of the prompt H3 receives", instructions);
    }
}
