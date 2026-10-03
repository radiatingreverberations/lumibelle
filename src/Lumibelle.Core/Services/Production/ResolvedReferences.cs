using lumibelle.Models;

namespace lumibelle.Services.Production;

public sealed record ResolvedPicture(int Number, Guid BindingId, ShotImageBinding? Image, ShotVideoBinding? Reel, ReelKeyframe? Keyframe)
{
    public ShotContinuityFrame? Continuity { get; init; }
}
public sealed record ResolvedVideo(int Number, int BindingIndex, ShotVideoBinding Reel);
public sealed record ResolvedAudio(int Number, int? BindingIndex, ShotVideoBinding? Reel, ShotVoiceBinding? Voice)
{
    public CharacterVoiceSelection? CharacterVoice { get; init; }
    public Guid? CharacterAssetId => CharacterVoice?.AssetId;
    public string? SourceName => CharacterVoice?.SourceName ?? Reel?.Name;
    public string? Speaker => CharacterVoice?.Speaker ?? Reel?.Speaker ?? Voice?.Speaker;
}

// Native references keep their historical numbering. Fantastic processes ordinary media first,
// then RefMods: sparse Video labels therefore follow full videos, regardless of binding order.
public sealed class ResolvedReferences
{
    public List<ResolvedPicture> Pictures { get; } = [];
    public List<ResolvedVideo> Videos { get; } = [];
    public List<ResolvedAudio> Audio { get; } = [];
    /// <summary>The frame takes open on: uploaded with the references, but anchored as the first frame rather than referenced by a label.</summary>
    public ShotStartFrame? StartFrame { get; private init; }
    public static ResolvedReferences For(Shot shot)
    {
        var result = new ResolvedReferences { StartFrame = shot.StartFrame };
        foreach (var image in shot.Images) result.Pictures.Add(new(result.Pictures.Count + 1, image.Id, image, null, null));
        // After the images and before reel keyframes, so existing image numbers stay put.
        if (shot.ContinuityFrame is { } continuity) result.Pictures.Add(new(result.Pictures.Count + 1, continuity.Id, null, null, null) { Continuity = continuity });
        foreach (var reel in shot.Videos.Where(v => v.EffectiveVisuals == ReelVisuals.Keyframes))
            foreach (var frame in reel.Keyframes?.Frames ?? []) result.Pictures.Add(new(result.Pictures.Count + 1, frame.Id, null, reel, frame));
        for (var i = 0; i < shot.Videos.Count; i++)
            if (shot.Videos[i].EffectiveVisuals == ReelVisuals.FullReel)
            {
                var reel = shot.Videos[i]; result.Videos.Add(new(result.Videos.Count + 1, i, reel));
                if (reel.UseSoundtrack) result.Audio.Add(new(result.Audio.Count + 1, i, reel, null));
            }
        for (var i = 0; i < shot.Videos.Count; i++)
            if (shot.Videos[i].EffectiveVisuals != ReelVisuals.FullReel && shot.Videos[i].UseSoundtrack)
                result.Audio.Add(new(result.Audio.Count + 1, i, shot.Videos[i], null));
        foreach (var voice in shot.Voices) result.Audio.Add(new(result.Audio.Count + 1, null, null, voice));
        for (var i = 0; i < result.Audio.Count; i++) {
            var audio = result.Audio[i];
            result.Audio[i] = audio with { CharacterVoice = shot.CharacterVoices?.FirstOrDefault(c =>
                c.Source == CharacterVoiceSource.Recording && c.AssetId == audio.Voice?.CharacterAssetId ||
                c.Source == CharacterVoiceSource.Reel && c.ReelBindingId == audio.Reel?.Id) };
        }
        for (var i = 0; i < shot.Videos.Count; i++)
            if (shot.Videos[i].EffectiveVisuals == ReelVisuals.RefMod)
                result.Videos.Add(new(result.Videos.Count + 1, i, shot.Videos[i]));
        return result;
    }
    public IReadOnlyList<(VideoInputKind Kind, int? VideoIndex)> InputOrder()
    {
        List<(VideoInputKind, int?)> result = Pictures.Select(_ => (VideoInputKind.Image, (int?)null)).ToList();
        foreach (var video in Videos.Where(v => v.Reel.EffectiveVisuals == ReelVisuals.FullReel))
        {
            result.Add((VideoInputKind.Video, video.BindingIndex));
            if (video.Reel.UseSoundtrack) result.Add((VideoInputKind.VideoSoundtrack, video.BindingIndex));
        }
        result.AddRange(Audio.Where(a => a.Reel?.EffectiveVisuals != ReelVisuals.FullReel).Select(a => (VideoInputKind.Audio, a.BindingIndex)));
        // RefMods are server-side references captured in the shot snapshot, not uploaded media.
        if (StartFrame is not null) result.Add((VideoInputKind.StartFrame, null));
        return result;
    }
    public string Label(ShotVideoBinding reel) => string.Join(" · ",
        Pictures.Where(p => p.Reel == reel).Select(p => $"<Picture {p.Number}>")
            .Concat(Videos.Where(v => v.Reel == reel).Select(v => $"<Video {v.Number}>"))
            .Concat(Audio.Where(a => a.Reel == reel).Select(a => $"<Audio {a.Number}>")));
    public static ReelAudioExcerpt Excerpt(ShotVideoBinding reel) => reel.AudioExcerpt ?? new(0, Math.Min(15, reel.Media.Duration));
}
