using System.Security.Cryptography;
using System.Text;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public static class AssetPickPlan
{
    // Pure, repeatable planning: never edits the supplied shot or library, reads
    // media, saves a project, changes a prompt, or creates a remote RefMod build.
    public static AssetPickDraft Create(AssetPickRequest request, IReadOnlyList<AssetPickItem> selected,
        Shot current, string prompt, string directingNotes, AssetLibrary library)
    {
        AssetPicker.ValidateRequest(request);
        if (request.ContextFingerprint != AssetPickCatalog.ContextFingerprint(library.ProjectId, current, prompt, directingNotes))
            throw new WorkspaceStoreException("The shot, prompt or pending references changed after this suggestion. Request a new selection; your draft is unchanged.");
        AssetPickCatalog.RequireCurrent(request, library);
        AssetPicker.ValidateSelection(request, selected);
        var draft = current.Copy();
        var notices = new List<string>();
        var reelSources = new Dictionary<Guid, Guid>();
        if (request.ReplaceExisting)
        {
            draft.Images = []; draft.Videos = []; draft.Voices = []; draft.CharacterVoices = [];
            notices.Add("This replaces the pending image, reel and voice selections. The shot prompt is not rewritten; check its reference labels before generating.");
        }
        var pendingAudio = new List<(AssetPickItem Item, AssetPickCandidate Candidate, ShotVideoBinding? Reel)>();
        foreach (var item in selected)
        {
            var candidate = request.Catalogue.Candidates.Single(c => c.Id == item.CandidateId);
            var asset = library.Assets.Single(a => a.Id == candidate.AssetId);
            if (candidate.Kind == "Image")
            {
                if (draft.Images.Any(b => b.Kind == ShotImageKind.AssetImage && b.AssetId == asset.Id && b.MediaId == candidate.SourceId))
                {
                    notices.Add($"Kept the existing image selection for {candidate.Name}, including its crop and use guidance.");
                    continue;
                }
                var image = asset.Images.Single(i => i.Id == candidate.SourceId);
                var binding = ReferenceSetups.Bind(asset, image, draft);
                binding.Id = BindingId(request, candidate.Id);
                binding.RepresentsId = null; binding.LookId = null; binding.InferUsage = true;
                binding.Use = null; binding.Purpose = null; binding.Role = "Let AI decide";
                binding.AiUseHint = string.IsNullOrWhiteSpace(item.UseHint) ? "Auto" : item.UseHint;
                draft.Images.Add(binding);
            }
            else if (candidate.Kind == "Reel")
            {
                var reel = library.Reels.Single(r => r.Id == candidate.SourceId && r.AssetId == asset.Id);
                if (draft.Videos.Any(v => v.Media.Id == reel.Media.Id))
                {
                    notices.Add($"Kept the existing reel selection for {reel.Name}, including its mode, frames and soundtrack. Change it manually or use replacement mode to alter it.");
                    continue;
                }
                var description = reel.UseGuidance;
                if (!string.IsNullOrWhiteSpace(item.UseHint)) description += "\nIntended use in this shot: " + item.UseHint;
                if (description.Length > 20_000) throw new WorkspaceStoreException($"The combined use guidance for {reel.Name} is too long. Shorten its guidance before selecting it.");
                var binding = new ShotVideoBinding {
                    Id = BindingId(request, candidate.Id), Media = ShotCopy.Of(reel.Media), Name = reel.Name,
                    Description = description, Visuals = Enum.Parse<ReelVisuals>(item.VisualMode!), UseSoundtrack = false,
                    OwnerAssetId = asset.Id, OwnerCategory = asset.Category,
                    Keyframes = reel.Keyframes is null ? null : ShotCopy.Of(reel.Keyframes),
                    AudioExcerpt = new(candidate.ExcerptStart, candidate.ExcerptDuration)
                };
                draft.Videos.Add(binding); reelSources.Add(binding.Id, reel.Id);
                if (item.Speaker is not null) pendingAudio.Add((item, candidate, binding));
            }
            else pendingAudio.Add((item, candidate, null));
        }
        foreach (var (item, candidate, reel) in pendingAudio)
        {
            var owner = library.Assets.Single(a => a.Id == candidate.AssetId);
            var speaker = item.Speaker!.Trim();
            if (speaker.Length > 0) speaker = draft.Dialogue.First(d => d.Speaker.Trim().Equals(speaker, StringComparison.OrdinalIgnoreCase)).Speaker;
            if (!request.ReplaceExisting && HasVoiceChoice(draft, owner.Id, speaker.Trim(), library))
            {
                notices.Add($"Kept the current voice choice for {owner.Name}{(speaker.Length > 0 ? " / " + speaker : "")}; did not assign {candidate.Name}.");
                if (reel?.EffectiveVisuals == ReelVisuals.None) { draft.Videos.Remove(reel); reelSources.Remove(reel.Id); }
                continue;
            }
            if (owner.Category == AssetCategory.Character)
            {
                var choice = new CharacterVoiceSelection {
                    AssetId = owner.Id, CharacterName = owner.Name, Speaker = speaker, SpeakerConfirmed = true,
                    FromDefault = false, SourceName = candidate.Name
                };
                if (reel is not null)
                {
                    choice.Source = CharacterVoiceSource.Reel; choice.ReelBindingId = reel.Id;
                    choice.ReelMediaId = reel.Media.Id; choice.Excerpt = reel.AudioExcerpt;
                }
                else
                {
                    var voice = library.Voices.Single(v => v.Id == candidate.SourceId && v.AssetId == owner.Id);
                    CharacterVoices.SelectRecording(choice, voice, voice.Id == owner.DefaultVoiceId);
                }
                CharacterVoices.Set(draft, choice, library);
            }
            else if (reel is not null)
            {
                reel.UseSoundtrack = true; reel.Speaker = speaker.Length == 0 ? null : speaker;
            }
            else
            {
                if (speaker.Length == 0) throw new WorkspaceStoreException("A vocalizations-only recording must belong to a Character asset. Assign a dialogue speaker or move that recording first.");
                var voice = library.Voices.Single(v => v.Id == candidate.SourceId && v.AssetId == owner.Id);
                draft.Voices.Add(new() { VoiceId = voice.Id, AssetId = voice.AssetId, Speaker = speaker, Start = voice.Start, Duration = voice.ExcerptDuration });
            }
        }
        // Selecting a visual must not implicitly choose a paid/provider-default
        // voice. Existing voice choices, including an explicit None, remain intact.
        foreach (var ownerId in CharacterVoices.Owners(draft, library))
        {
            if (draft.CharacterVoices?.Any(c => c.AssetId == ownerId) == true ||
                draft.Voices.Any(v => (v.CharacterAssetId ?? v.AssetId) == ownerId) ||
                draft.Videos.Any(v => CharacterVoices.Owner(v, library) == ownerId && v.UseSoundtrack)) continue;
            var owner = library.Assets.FirstOrDefault(a => a.Id == ownerId && a.Category == AssetCategory.Character);
            if (owner is null) continue;
            draft.CharacterVoices ??= [];
            draft.CharacterVoices.Add(new() { AssetId = owner.Id, CharacterName = owner.Name,
                Source = CharacterVoiceSource.None, Speaker = "", SpeakerConfirmed = true });
            notices.Add($"No voice was selected for {owner.Name}. Review the Voices section if this character speaks.");
        }
        if (!request.ReplaceExisting)
        {
            // CharacterVoices.Set normalizes even muted reels. Add-only selection
            // must retain their captured owner/speaker metadata as well as pixels.
            var retained = current.Videos.ToDictionary(v => v.Id);
            for (var i = 0; i < draft.Videos.Count; i++)
                if (retained.TryGetValue(draft.Videos[i].Id, out var original)) draft.Videos[i] = ShotCopy.Of(original);
        }
        ValidateBudget(draft);
        H3Policy.Validate(draft);
        return new(draft, reelSources, notices.ToArray());
    }

    private static bool HasVoiceChoice(Shot shot, Guid owner, string speaker, AssetLibrary library) =>
        shot.CharacterVoices?.Any(c => c.AssetId == owner && c.Source != CharacterVoiceSource.Unselected ||
            speaker.Length > 0 && c.Source is not (CharacterVoiceSource.None or CharacterVoiceSource.Unselected) && c.Speaker.Trim().Equals(speaker, StringComparison.OrdinalIgnoreCase)) == true ||
        shot.Voices.Any(v => (v.CharacterAssetId ?? v.AssetId) == owner || speaker.Length > 0 && v.Speaker.Trim().Equals(speaker, StringComparison.OrdinalIgnoreCase)) ||
        shot.Videos.Any(v => v.UseSoundtrack && (CharacterVoices.Owner(v, library) == owner || speaker.Length > 0 && string.Equals(v.Speaker?.Trim(), speaker, StringComparison.OrdinalIgnoreCase)));

    private static Guid BindingId(AssetPickRequest request, string candidate) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(request.ContextFingerprint + "/" + candidate)).AsSpan(0, 16));

    public static void ValidateBudget(Shot shot)
    {
        var refs = ResolvedReferences.For(shot);
        var full = refs.Videos.Where(v => v.Reel.EffectiveVisuals == ReelVisuals.FullReel).ToArray();
        var audioSeconds = refs.Audio.Sum(a => a.Voice?.Duration ??
            (a.Reel!.EffectiveVisuals == ReelVisuals.FullReel ? a.Reel.Media.Duration : ResolvedReferences.Excerpt(a.Reel).Duration));
        if (refs.Pictures.Count > 9 || ReferenceVideos.ReelSlotCount(shot) > 3 || refs.Audio.Count > 3 ||
            full.Sum(v => v.Reel.Media.Duration) > ReferenceVideos.MaximumSeconds + .001 ||
            audioSeconds > ReferenceVideos.MaximumSeconds + .001 ||
            refs.Pictures.Count + refs.Videos.Count + refs.Audio.Count(a => a.Reel?.EffectiveVisuals != ReelVisuals.FullReel) > 12)
            throw new WorkspaceStoreException("This selection exceeds the reference budget. Uncheck some suggestions: up to 9 pictures, 3 reels, 3 audio inputs, 15 seconds of video/audio, and 12 sources including retained references.");
    }
}
