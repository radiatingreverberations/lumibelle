using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public sealed record ReferenceCopyResult(Shot Inputs, IReadOnlyList<string> Warnings);

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
        var speakers = destination.Dialogue.Select(d => d.Speaker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Set synchronizes the materialized Voices/Videos with their managed choices.
        // Work on a detached array because it replaces entries in CharacterVoices.
        foreach (var choice in (result.CharacterVoices ?? []).ToArray())
        {
            if (choice.ReelBindingId is { } oldId)
            {
                if (!videoIds.TryGetValue(oldId, out var newId))
                    throw new WorkspaceStoreException($"The source voice reel for {choice.CharacterName} is unavailable. Repair it before copying references.");
                choice.ReelBindingId = newId;
            }
            if (choice.Source != CharacterVoiceSource.None && choice.Speaker.Length > 0)
            {
                var speaker = speakers.FirstOrDefault(s => s.Equals(choice.Speaker, StringComparison.OrdinalIgnoreCase));
                if (speaker is null)
                {
                    // An absent source speaker is not evidence of vocalizations or
                    // silence in the destination. Require a fresh explicit decision.
                    choice.Speaker = "";
                    choice.SpeakerConfirmed = false;
                    warnings.Add($"Choose the destination dialogue speaker or vocalizations for {choice.CharacterName} before applying.");
                }
                else choice.Speaker = speaker;
            }
            CharacterVoices.Set(result, choice, library);
        }
        return new(result, warnings.Distinct().ToArray());
    }
}
