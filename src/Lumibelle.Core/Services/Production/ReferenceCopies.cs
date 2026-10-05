using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

/// <param name="Notes">What the copy decided on the person's behalf, such as a voice's speaker here; unlike warnings, nothing to fix.</param>
public sealed record ReferenceCopyResult(Shot Inputs, IReadOnlyList<string> Warnings, IReadOnlyList<string>? Notes = null);

public static class ReferenceCopies
{
    // Copy a reference graph into a new destination draft. Never mutate either input,
    // copy the source dialogue/settings, or silently select a new asset default voice.
    public static ReferenceCopyResult Into(Shot source, Shot destination, AssetLibrary library)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(library);
        var result = destination.Copy();
        var warnings = new List<string>();
        var notes = new List<string>();
        var charactersByName = destination.Characters
            .GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
        var sourceNames = source.Characters.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        result.Images = ShotCopy.Of(source.Images);
        // The source's continuity picture belongs to the shot before it; a fresh one is added for the source's own take.
        result.ContinuityFrame = null;
        foreach (var image in result.Images)
        {
            image.Id = Guid.NewGuid();
            if (image.RepresentsId is not { } characterId) continue;
            var character = source.Characters.FirstOrDefault(c => c.Id == characterId);
            var match = destination.Characters.FirstOrDefault(c => c.Id == characterId);
            if (match is null && character is not null && sourceNames[character.Name.Trim()] == 1)
                charactersByName.TryGetValue(character.Name.Trim(), out match);
            image.RepresentsId = match?.Id;
            if (match is null) warnings.Add($"Review the character link for {image.Name}; no unambiguous destination character was found.");
        }

        var videoIds = new Dictionary<Guid, Guid>();
        result.Videos = ShotCopy.Of(source.Videos);
        foreach (var video in result.Videos)
        {
            var newId = Guid.NewGuid();
            if (!videoIds.TryAdd(video.Id, newId))
                throw new WorkspaceStoreException("The source setup contains duplicate reel bindings. Repair it before copying references.");
            video.Id = newId;
            if (video.Keyframes is { } frames)
                frames.Frames = frames.Frames.Select(f => f with { Id = Guid.NewGuid() }).ToList();
        }
        result.Voices = ShotCopy.Of(source.Voices);
        result.CharacterVoices = source.CharacterVoices is null ? null : ShotCopy.Of(source.CharacterVoices);
        var speakers = destination.Dialogue.Select(d => d.Speaker.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var choices = (result.CharacterVoices ?? []).ToArray();
        foreach (var choice in choices)
            if (choice.ReelBindingId is { } oldId)
            {
                if (!videoIds.TryGetValue(oldId, out var newId))
                    throw new WorkspaceStoreException($"The source voice reel for {choice.CharacterName} is unavailable. Repair it before copying references.");
                choice.ReelBindingId = newId;
            }
        // Speakers keep their voices; a voice whose source speaker is absent takes the one destination speaker
        // that is clearly the same character, by the cast member its pictures represent or by name.
        var speaking = choices.Where(c => c.Source != CharacterVoiceSource.None && c.Speaker.Length > 0).ToArray();
        var matched = new Dictionary<CharacterVoiceSelection, string>();
        foreach (var choice in speaking)
            if (speakers.FirstOrDefault(s => s.Equals(choice.Speaker, StringComparison.OrdinalIgnoreCase)) is { } same) matched[choice] = same;
        foreach (var choice in speaking.Where(c => !matched.ContainsKey(c)))
        {
            var claimed = matched.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var open = speakers.Where(s => !claimed.Contains(s)).ToArray();
            var cast = result.Images.Where(i => i.AssetId == choice.AssetId && i.RepresentsId is not null)
                .Select(i => result.Characters.FirstOrDefault(c => c.Id == i.RepresentsId)?.Name.Trim()).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] Candidates(Func<string, bool> fits) => open.Where(fits).ToArray();
            var found = Candidates(s => cast.Contains(s, StringComparer.OrdinalIgnoreCase)) is [var byCast] ? byCast
                : Candidates(s => s.Equals(choice.CharacterName.Trim(), StringComparison.OrdinalIgnoreCase)) is [var byName] ? byName
                : Candidates(s => WordsOf(s).IsSubsetOf(WordsOf(choice.CharacterName))) is [var byWords] ? byWords : null;
            if (found is not null) { matched[choice] = found; notes.Add($"{choice.CharacterName}'s voice speaks the {found} lines here."); }
        }
        var unclaimed = speakers.Where(s => !matched.Values.Contains(s, StringComparer.OrdinalIgnoreCase)).ToArray();
        // Set synchronizes the materialized Voices/Videos with their managed choices.
        // Work on a detached array because it replaces entries in CharacterVoices.
        foreach (var choice in choices)
        {
            if (choice.Source != CharacterVoiceSource.None && choice.Speaker.Length > 0)
            {
                if (matched.TryGetValue(choice, out var speaker)) { choice.Speaker = speaker; choice.SpeakerConfirmed = true; }
                else if (unclaimed.Length == 0)
                {
                    // Every line here belongs to someone else, or there are none: this character does not speak.
                    choice.Source = CharacterVoiceSource.None; choice.Speaker = ""; choice.SpeakerConfirmed = true;
                    choice.Recording = null; choice.ReelBindingId = null; choice.ReelMediaId = null; choice.Excerpt = null; choice.FromDefault = false; choice.SourceName = "";
                    notes.Add($"{choice.CharacterName} has no lines here, so their voice is None.");
                }
                else
                {
                    // Another speaker here could be this character, or it may only vocalize. Ask rather than guess.
                    choice.Speaker = "";
                    choice.SpeakerConfirmed = false;
                    warnings.Add($"Choose the destination dialogue speaker or vocalizations for {choice.CharacterName} before applying.");
                }
            }
            CharacterVoices.Set(result, choice, library);
        }
        return new(result, warnings.Distinct().ToArray(), notes);
    }
    private static HashSet<string> WordsOf(string name) =>
        name.Split([' ', '-', '_', '.', ','], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
