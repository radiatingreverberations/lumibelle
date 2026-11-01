using System.Text.RegularExpressions;
using lumibelle.Models;

namespace lumibelle.Services.Production;

// H3 binds an Audio input through its line in subject_definitions and its relationship entry in
// retention_analysis (docs/h3/ref2va-prompt-guide.md, sections 2.4 and 4.2). Models given only a
// general rule tended to explain the input in referenceUsage, which is not part of the prompt, so
// the instructions show this shot's own lines and parsing adds any that are still missing.
public static partial class PromptComposer
{
    private static string AudioReferenceInstructions(Shot shot)
    {
        var speakers = SpeakerNames(shot);
        var first = ResolvedReferences.For(shot).Audio.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Speaker)) ?? ResolvedReferences.For(shot).Audio[0];
        var retention = AudioRetention(first, speakers);
        return "\nOnly the selected numbered Audio inputs supply voice identity or an explicitly enabled soundtrack. " +
            "Character voice choices are independent of appearance references; disabled reel soundtracks and choices set to None are not inputs. " +
            "Use the supplied Audio numbers exactly; never derive them from Video numbers or speaker numbers. " +
            "Define every selected Audio input on its own line in subject_definitions, stating its role. A mapped voice names its defined subject or authored speaker " +
            "with its stable (S1), (S2) label, the speaker written exactly as in the dialogue, for example: " + AudioDefinition(first, "<Subject 1>", speakers) + " " +
            (retention is null ? "" : "In retention_analysis, give each Audio input its own relationship entry, for example: " + retention + " ") +
            "An unmapped soundtrack retains its supplied role without an invented speaker. Audio-only reels supply voice references without visual information. " +
            "Do not claim to have heard recordings; only their supplied roles and speaker mappings are available. " +
            "Voice references supply identity, never their source words. A vocalizations-only voice supplies no dialogue. " +
            "Also explain the Audio inputs in referenceUsage, which is not part of the prompt H3 receives.";
    }

    // "JAN (V.O.) (S1)": the speaker as the dialogue names it, so the speaker mapping check finds it.
    private static string? SpeakerLabel(ResolvedAudio audio, string[] speakers)
    {
        if (string.IsNullOrWhiteSpace(audio.Speaker)) return null;
        var number = SpeakerNumber(speakers, audio.Speaker);
        return number > 0 ? $"{audio.Speaker.Trim()} (S{number})" : audio.Speaker.Trim();
    }

    private static string AudioDefinition(ResolvedAudio audio, string? subject, string[] speakers)
    {
        var tag = $"<Audio {audio.Number}>";
        if (SpeakerLabel(audio, speakers) is { } label) return $"{tag} is the voice-timbre reference for {(subject is null ? "" : subject + ", ")}{label}.";
        if (audio.CharacterVoice is { } vocal) return $"{tag} is the voice-timbre reference for {vocal.CharacterName}'s vocalizations; it supplies no dialogue.";
        return $"{tag} is the enabled soundtrack of {audio.Reel?.Name ?? "a reference reel"}.";
    }

    // A soundtrack's relationship (copied or referenced) is the author's to state; only voices get one here.
    private static string? AudioRetention(ResolvedAudio audio, string[] speakers)
    {
        var tag = $"<Audio {audio.Number}>";
        if (SpeakerLabel(audio, speakers) is { } label) return $"{tag}: reference - {label} follows {tag}'s voice timbre without copying its words.";
        if (audio.CharacterVoice is { } vocal) return $"{tag}: reference - {vocal.CharacterName}'s vocalizations follow {tag}'s voice timbre.";
        return null;
    }

    // Adds the definition or relationship line of each Audio input the section does not name.
    private static string? WithAudioLines(string section, string? body, string? definitions, Shot shot, SortedSet<int> added)
    {
        var speakers = SpeakerNames(shot);
        var lines = new List<string>();
        foreach (var audio in ResolvedReferences.For(shot).Audio)
        {
            if (body?.Contains($"<Audio {audio.Number}>", StringComparison.Ordinal) == true) continue;
            var line = section == "subject_definitions" ? AudioDefinition(audio, Subject(definitions ?? body, audio, speakers), speakers) : AudioRetention(audio, speakers);
            if (line is null) continue;
            lines.Add(line); added.Add(audio.Number);
        }
        if (lines.Count == 0) return body;
        return string.IsNullOrWhiteSpace(body) ? string.Join("\n", lines) : body.TrimEnd() + "\n" + string.Join("\n", lines);
    }

    // The subject whose definition carries the voice's (S1) label.
    private static string? Subject(string? definitions, ResolvedAudio audio, string[] speakers)
    {
        if (definitions is null || string.IsNullOrWhiteSpace(audio.Speaker) || SpeakerNumber(speakers, audio.Speaker) is not (> 0 and var number)) return null;
        var line = definitions.Split('\n').FirstOrDefault(l => l.Contains($"(S{number})", StringComparison.Ordinal));
        return line is null ? null : Regex.Match(line, @"<Subject \d+>") is { Success: true } subject ? subject.Value : null;
    }

    private static string AddedAudioNote(SortedSet<int> added) =>
        $"The response did not name {string.Join(" and ", added.Select(n => $"<Audio {n}>"))} in the prompt sections, so Lumibelle added " +
        $"{(added.Count == 1 ? "its definition" : "their definitions")} from the shot's voice mapping. Check the wording.";
}
