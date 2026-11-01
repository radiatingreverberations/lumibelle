using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public static class CharacterVoices
{
    public static VoiceReference? Default(ReferenceAsset asset, AssetLibrary library) =>
        library.Voices.FirstOrDefault(v => v.Id == asset.DefaultVoiceId && v.AssetId == asset.Id);
    public static Guid? Owner(ShotVideoBinding reel, AssetLibrary library) => reel.OwnerAssetId ??
        library.Reels.FirstOrDefault(r => r.Media.Id == reel.Media.Id)?.AssetId;
    public static IReadOnlyList<Guid> Owners(Shot shot, AssetLibrary library) =>
        shot.Images.Select(i => (Guid?)i.AssetId).Concat(shot.Videos.Select(v => Owner(v, library)))
            .Concat(shot.Voices.Select(v => (Guid?)(v.CharacterAssetId ?? v.AssetId)))
            .Concat((shot.CharacterVoices ?? []).Select(v => (Guid?)v.AssetId))
            .Where(id => id is not null && (library.Assets.Any(a => a.Id == id && a.Category == AssetCategory.Character) ||
                shot.Voices.Any(v => (v.CharacterAssetId ?? v.AssetId) == id) || shot.CharacterVoices?.Any(c => c.AssetId == id) == true))
            .Select(id => id!.Value).Distinct().ToArray();
    public static CharacterVoiceSelection Initial(Shot shot, ReferenceAsset asset, AssetLibrary library)
    {
        var speakers = shot.Dialogue.Select(d => d.Speaker.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var mapped = shot.Voices.Where(v => (v.CharacterAssetId ?? v.AssetId) == asset.Id).Select(v => v.Speaker)
            .Concat(shot.Videos.Where(v => Owner(v, library) == asset.Id).Select(v => v.Speaker ?? ""))
            .Where(s => speakers.Contains(s, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // JAN (V.O.) and JAN are both Jan Sherwood, unless another character could be JAN too.
        int Named(string speaker) => library.Assets.Count(a => a.Category == AssetCategory.Character && DialogueSpeakers.Names(speaker, a.Name));
        var matches = mapped.Length > 0 ? mapped : speakers.Where(s => DialogueSpeakers.Names(s, asset.Name)).ToArray();
        var unambiguous = matches.Length == 1 && (mapped.Length == 1 || Named(matches[0]) == 1);
        var allSpeakersKnown = speakers.All(s => Named(s) == 1);
        var choice = new CharacterVoiceSelection { AssetId = asset.Id, CharacterName = asset.Name, SpeakerConfirmed = unambiguous || speakers.Length == 0 || allSpeakersKnown && matches.Length == 0,
            Speaker = unambiguous ? matches[0] : "", Source = speakers.Length == 0 || allSpeakersKnown && matches.Length == 0
                ? CharacterVoiceSource.None : CharacterVoiceSource.Unselected };
        // An unmatched name with dialogue is ambiguous; do not guess that this character is silent.
        if (unambiguous && Default(asset, library) is { } voice) SelectRecording(choice, voice, true);
        return choice;
    }
    public static void SelectRecording(CharacterVoiceSelection choice, VoiceReference voice, bool fromDefault)
    {
        choice.Source = CharacterVoiceSource.Recording; choice.FromDefault = fromDefault; choice.SourceName = voice.Name;
        choice.Recording = new() { VoiceId = voice.Id, AssetId = voice.AssetId, CharacterAssetId = choice.AssetId,
            Speaker = string.IsNullOrWhiteSpace(choice.Speaker) ? choice.CharacterName : choice.Speaker, Start = voice.Start, Duration = voice.ExcerptDuration };
        choice.ReelBindingId = null; choice.ReelMediaId = null; choice.Excerpt = null;
    }
    public static void Set(Shot shot, CharacterVoiceSelection choice, AssetLibrary library)
    {
        choice = ShotCopy.Of(choice);
        var speaker = string.IsNullOrWhiteSpace(choice.Speaker) ? choice.CharacterName : choice.Speaker;
        if (choice.Recording is { } captured) choice.Recording = captured with { CharacterAssetId = choice.AssetId, Speaker = speaker };
        shot.CharacterVoices ??= [];
        var choiceIndex = shot.CharacterVoices.FindIndex(v => v.AssetId == choice.AssetId);
        if (choiceIndex < 0) shot.CharacterVoices.Add(choice); else shot.CharacterVoices[choiceIndex] = choice;
        bool OwnedVoice(ShotVoiceBinding v) => v.CharacterAssetId == choice.AssetId || v.CharacterAssetId is null &&
            (v.AssetId == choice.AssetId || !string.IsNullOrWhiteSpace(choice.Speaker) && v.Speaker.Equals(choice.Speaker, StringComparison.OrdinalIgnoreCase));
        var voiceIndex = shot.Voices.FindIndex(OwnedVoice);
        shot.Voices.RemoveAll(OwnedVoice);
        foreach (var reel in shot.Videos.Where(v => Owner(v, library) == choice.AssetId)) { reel.OwnerAssetId = choice.AssetId; reel.UseSoundtrack = false; reel.Speaker = null; }
        if (choice.Source == CharacterVoiceSource.Recording && choice.Recording is { } recording)
            shot.Voices.Insert(voiceIndex < 0 ? shot.Voices.Count : Math.Min(voiceIndex, shot.Voices.Count), recording with { });
        else if (choice.Source == CharacterVoiceSource.Reel && shot.Videos.FirstOrDefault(v => v.Id == choice.ReelBindingId && v.Media.Id == choice.ReelMediaId) is { } reel) {
            reel.UseSoundtrack = true; reel.Speaker = string.IsNullOrWhiteSpace(choice.Speaker) ? null : choice.Speaker;
            reel.AudioExcerpt = choice.Excerpt;
        }
    }
    // Dialogue speakers no voice input speaks for, each with the one library character it names.
    // Speakers that name no character, such as an unnamed extra, are left out. A character
    // without a managed choice keeps older inputs: any recording or reel audio they own.
    public static IReadOnlyList<(string Speaker, ReferenceAsset Character)> Unvoiced(Shot shot, AssetLibrary library)
    {
        var voiced = (shot.CharacterVoices ?? []).Where(c => c.Source is CharacterVoiceSource.Recording or CharacterVoiceSource.Reel).Select(c => c.Speaker)
            .Concat(shot.Voices.Select(v => v.Speaker)).Concat(shot.Videos.Where(v => v.UseSoundtrack).Select(v => v.Speaker ?? ""))
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unvoiced = new List<(string, ReferenceAsset)>();
        foreach (var speaker in shot.Dialogue.Select(d => d.Speaker.Trim()).Where(s => s.Length > 0 && !voiced.Contains(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (library.Assets.Where(a => a.Category == AssetCategory.Character && DialogueSpeakers.Names(speaker, a.Name)).ToArray() is [var character] &&
                (shot.CharacterVoices?.Any(c => c.AssetId == character.Id) == true ||
                 !shot.Voices.Any(v => (v.CharacterAssetId ?? v.AssetId) == character.Id) && !shot.Videos.Any(v => v.UseSoundtrack && Owner(v, library) == character.Id)))
                unvoiced.Add((speaker, character));
        return unvoiced;
    }
    public const string MadeUpVoice = "without one, the video model makes up a voice for these lines";
    public static string UnvoicedWarning(string speaker, ReferenceAsset character) =>
        $"No voice is chosen for {speaker}, which names {character.Name}. Choose {character.Name}'s voice under Character voices; {MadeUpVoice}.";
    public static string Label(CharacterVoiceSelection? choice) => choice switch {
        null => "Saved legacy inputs", { Source: CharacterVoiceSource.None } => "None",
        { Source: CharacterVoiceSource.Unselected } => "Choose voice",
        { FromDefault: true } => "Default · " + choice.SourceName, _ => choice.SourceName };
    public static void Validate(Shot shot)
    {
        if (shot.CharacterVoices is not { } choices) return;
        if (choices.Any(v => v is null) || choices.Select(v => v.AssetId).Distinct().Count() != choices.Count) throw new WorkspaceStoreException("Choose one voice per character.");
        foreach (var c in choices) {
            if (c.Version != 1 || c.AssetId == Guid.Empty || string.IsNullOrWhiteSpace(c.CharacterName) || c.Speaker is null || !Enum.IsDefined(c.Source) || c.Source == CharacterVoiceSource.Unselected)
                throw new WorkspaceStoreException($"Choose a voice or None for {c.CharacterName} in Manage references.");
            if (c.Source != CharacterVoiceSource.None && !c.SpeakerConfirmed)
                throw new WorkspaceStoreException($"Choose the dialogue speaker or vocalizations for {c.CharacterName}.");
            if (c.Source != CharacterVoiceSource.None && c.Speaker.Length > 0 && !shot.Dialogue.Any(d => d.Speaker.Equals(c.Speaker, StringComparison.OrdinalIgnoreCase)))
                throw new WorkspaceStoreException($"Choose a current dialogue speaker or vocalizations for {c.CharacterName}.");
            var voices = shot.Voices.Where(v => (v.CharacterAssetId ?? v.AssetId) == c.AssetId).ToArray();
            var reels = shot.Videos.Where(v => v.OwnerAssetId == c.AssetId && v.UseSoundtrack).ToArray();
            var speaker = string.IsNullOrWhiteSpace(c.Speaker) ? c.CharacterName : c.Speaker;
            if (c.Source == CharacterVoiceSource.Recording && (c.Recording is not { } r || voices.Length != 1 || reels.Length != 0 ||
                voices[0] != r || r.CharacterAssetId != c.AssetId || r.Speaker != speaker) ||
                c.Source == CharacterVoiceSource.Reel && (voices.Length != 0 || reels.Length != 1 || reels[0].Id != c.ReelBindingId || reels[0].Media.Id != c.ReelMediaId || reels[0].AudioExcerpt != c.Excerpt || (reels[0].Speaker ?? "") != c.Speaker) ||
                c.Source == CharacterVoiceSource.None && (voices.Length != 0 || reels.Length != 0))
                throw new WorkspaceStoreException($"The voice inputs for {c.CharacterName} changed. Review this character in Manage references.");
        }
        var speakers = choices.Where(c => c.Source is CharacterVoiceSource.Recording or CharacterVoiceSource.Reel && c.Speaker.Length > 0).Select(c => c.Speaker).ToArray();
        if (speakers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != speakers.Length)
            throw new WorkspaceStoreException("Choose only one character voice for each dialogue speaker.");
    }
}
