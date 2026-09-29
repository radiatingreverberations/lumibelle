using System.Security.Cryptography;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public static class ReferenceVideos
{
    public const long MaximumBytes = 250 * 1024 * 1024;
    public const double MaximumSeconds = 362 / 24d; // H3's 15-second frame-grid allowance.
    public static int EffectiveFrames(ReferenceVideoMedia media, Shot shot)
    {
        var frames = Math.Min((int)Math.Floor(media.Duration * 24 + .001), H3Policy.Frames(shot.Duration ?? 5));
        return Math.Max(5, 5 + 17 * ((frames - 5) / 17));
    }
    public static double EffectiveSeconds(ReferenceVideoMedia media, Shot shot) => EffectiveFrames(media, shot) / 24d;
    public static string Url(Guid project, Guid media) => $"/media/projects/{project}/reference-videos/{media}";
    public static string ThumbnailUrl(Guid project, Guid media) => Url(project, media) + "/thumbnail";
    public static int? SoundtrackNumber(Shot shot, int videoIndex) => ResolvedReferences.For(shot).Audio.FirstOrDefault(a => a.BindingIndex == videoIndex)?.Number;
    public static int VoiceNumber(Shot shot, int voiceIndex) => ResolvedReferences.For(shot).Audio.Single(a => a.Voice == shot.Voices[voiceIndex]).Number;
    public static IEnumerable<(int Number, string? Speaker)> AudioMappings(Shot shot) => ResolvedReferences.For(shot).Audio.Select(a => (a.Number, a.Speaker));
    public static IReadOnlyList<(VideoInputKind Kind, int? VideoIndex)> InputOrder(Shot shot) => ResolvedReferences.For(shot).InputOrder();
    public static string FrameUrl(Guid project, ReelFrameIdentity frame) => Url(project, frame.MediaId) + $"/frames/{frame.Index}?source={frame.Source}&seconds={frame.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    public static void Validate(Shot shot, bool ready = false)
    {
        if (shot.Videos is null || shot.Videos.Any(v => v is null || v.Media is null)) throw new WorkspaceStoreException("Invalid video references.");
        if (shot.Videos.Count == 0) return; // Do not change legacy input limits or fingerprints.
        foreach (var reel in shot.Videos) { ValidateMedia(reel.Media); if (reel.Keyframes is not null) ValidateKeyframes(reel.Media, reel.Keyframes); }
        var resolved = ResolvedReferences.For(shot);
        if (shot.Videos.Count > 3 || shot.Videos.Select(v => v.Id).Distinct().Count() != shot.Videos.Count ||
            shot.Videos.Select(v => v.Media.Id).Distinct().Count() != shot.Videos.Count || resolved.Pictures.Count > 9 ||
            resolved.Pictures.Select(p => p.BindingId).Distinct().Count() != resolved.Pictures.Count ||
            resolved.Videos.Where(v => v.Reel.EffectiveVisuals == ReelVisuals.FullReel).Sum(v => v.Reel.Media.Duration) > MaximumSeconds + .001 || resolved.Audio.Count > 3 ||
            resolved.Pictures.Count + resolved.Videos.Count + resolved.Audio.Count(a => a.Reel?.EffectiveVisuals != ReelVisuals.FullReel) > 12 ||
            resolved.Audio.Sum(a => a.Voice?.Duration ?? (a.Reel!.EffectiveVisuals == ReelVisuals.FullReel ? a.Reel.Media.Duration : ResolvedReferences.Excerpt(a.Reel).Duration)) > MaximumSeconds + .001)
            throw new WorkspaceStoreException("Use up to nine pictures, three reels and three enabled audio references, up to 15 seconds of video/audio, and 12 source files in total.");
        foreach (var v in shot.Videos)
        {
            ValidateMedia(v.Media);
            if (!Enum.IsDefined(v.EffectiveVisuals) || v.EffectiveVisuals == ReelVisuals.None && !v.UseSoundtrack)
                throw new WorkspaceStoreException("Choose keyframes, a full reel, or audio for each reel reference.");
            if (v.Keyframes is not null) ValidateKeyframes(v.Media, v.Keyframes);
            if (v.EffectiveVisuals == ReelVisuals.RefMod) ReelRefMods.ValidateBinding(v, ready);
            if (v.EffectiveVisuals == ReelVisuals.Keyframes && v.Keyframes?.Frames.Count is not > 0)
                throw new WorkspaceStoreException("Choose at least one keyframe for this reel.");
            if (v.UseSoundtrack && v.EffectiveVisuals != ReelVisuals.FullReel)
            {
                var excerpt = ResolvedReferences.Excerpt(v);
                if (!double.IsFinite(excerpt.Start) || !double.IsFinite(excerpt.Duration) || excerpt.Start < 0 || excerpt.Duration is < 1 or > 15 || excerpt.Start + excerpt.Duration > v.Media.Duration + .01)
                    throw new WorkspaceStoreException("Choose a reel audio excerpt lasting 1–15 seconds within the recording.");
            }
            if (v.Id == Guid.Empty || string.IsNullOrWhiteSpace(v.Name) || v.Name.Length > 500 || v.Description is null || v.Description.Length > 20000 ||
                v.UseSoundtrack && !v.Media.HasAudio || v.Speaker?.Length > 500)
                throw new WorkspaceStoreException("Give each video a name and select a soundtrack only when audio is available.");
            if (ready && v.UseSoundtrack && !string.IsNullOrWhiteSpace(v.Speaker) && !shot.Dialogue.Any(d => d.Speaker.Trim().Equals(v.Speaker.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new WorkspaceStoreException("Assign the video soundtrack to a current dialogue speaker, or clear its speaker mapping.");
        }
        var mods = shot.Videos.Where(v => v.EffectiveVisuals == ReelVisuals.RefMod && ReelRefMods.Matches(v, v.RefMod));
        if (mods.Sum(v => (long)v.RefMod!.Recipe.Tokens) > ReelRefMods.MaximumTotalTokens)
            throw new WorkspaceStoreException("The selected RefMods exceed this experiment's combined visual-token budget.");
        var speakers = AudioMappings(shot).Select(a => a.Speaker?.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
        if (speakers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != speakers.Length)
            throw new WorkspaceStoreException("Choose only one voice reference for each speaker, including video soundtracks.");
    }
    public static void ValidateKeyframes(ReferenceVideoMedia media, ReelKeyframeSet set)
    {
        if (set.Version != 1 || set.Frames is null || set.Frames.Count > 9 || set.Frames.Any(f => f is null || f.Frame is null) ||
            set.Frames.Select(f => f.Id).Distinct().Count() != set.Frames.Count || set.Frames.Select(f => (f.Frame.Source, f.Frame.Index)).Distinct().Count() != set.Frames.Count)
            throw new WorkspaceStoreException("Choose up to nine distinct keyframes.");
        foreach (var f in set.Frames)
        {
            if (f.Id == Guid.Empty || f.Frame.MediaId != media.Id || f.Frame.Source is null || f.Frame.Source.Length != 64 || f.Frame.Source.Any(c => !Uri.IsHexDigit(c)) || f.Frame.Index < 0 || f.Frame.Index >= media.Frames ||
                !double.IsFinite(f.Frame.Seconds) || f.Frame.Seconds < 0 || f.Frame.Seconds > media.Duration || f.Notes is null || f.Notes.Length > 20000)
                throw new WorkspaceStoreException("A keyframe does not match this reel.");
            if (f.Crop is not null) ComfyReferenceImageEditor.ValidateCrop(f.Crop);
        }
    }
    public static void ValidateMedia(ReferenceVideoMedia m)
    {
        if (m.Id == Guid.Empty || m.Bytes is <= 0 or > MaximumBytes || m.Sha256 is null || m.Sha256.Length != 64 || m.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
            !double.IsFinite(m.Duration) || m.Duration < 2 || m.Duration > MaximumSeconds + .001 || m.Width is < 16 or > 8192 || m.Height is < 16 or > 8192 ||
            m.Frames <= 0 || !double.IsFinite(m.Fps) || m.Fps <= 0 || m.Fps > 240)
            throw new WorkspaceStoreException("Choose a readable MP4 lasting 2–15 seconds, up to 250 MB and 8192 pixels per side. Trim longer clips before importing.");
    }
}

public interface IReferenceVideoStore
{
    Task<long> RemoveArchiveAsync(Guid project, Guid media, IReadOnlyCollection<ReelFrameIdentity> keep, H3Settings settings, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Removing reel archives is unavailable.");
    async Task PrepareFramesAsync(Guid project, IEnumerable<ReelFrameIdentity> frames, H3Settings settings, CancellationToken ct = default)
    { foreach (var frame in frames) { await using var png = await OpenFrameAsync(project, frame, settings, ct); } }
    Task PublishArchiveAsync(Guid project, ReferenceVideoMedia media, ShotTake take, string sourceDirectory, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Reel frame archives are unavailable.");
    Task<ReelFrameCatalog> FrameCatalogAsync(Guid project, ReferenceVideoMedia media, H3Settings settings, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Reel keyframes are unavailable.");
    Task<ReelKeyframeSet> SuggestFramesAsync(Guid project, ReferenceVideoMedia media, int count, H3Settings settings, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Reel keyframes are unavailable.");
    Task<AssetMedia> OpenFrameAsync(Guid project, ReelFrameIdentity frame, H3Settings settings, CancellationToken ct = default)
        => throw new WorkspaceStoreException("Reel keyframes are unavailable.");
    Task<ReferenceVideoMedia> ImportAsync(Guid project, Stream content, string name, H3Settings settings, CancellationToken ct = default);
    Task<ReferenceVideoMedia> CopyTakeAsync(Guid project, Guid take, H3Settings settings, CancellationToken ct = default);
    Task<AssetMedia?> OpenAsync(Guid project, Guid media, CancellationToken ct = default);
    Task<AssetMedia?> OpenThumbnailAsync(Guid project, Guid media, H3Settings settings, CancellationToken ct = default)
        => Task.FromResult<AssetMedia?>(null);
    Task ValidateAsync(Guid project, IEnumerable<ShotVideoBinding> bindings, CancellationToken ct = default);
    Task PrepareAsync(Guid project, Shot shot, string directory, List<PreparedVideoInput> inputs, H3Settings settings, CancellationToken ct = default);
}

public sealed partial class FileReferenceVideoStore(ProjectFiles files, IShotStore shots, IProductionMediaTools mediaTools) : IReferenceVideoStore
{
    private async Task<string> DirectoryAsync(Guid project, Guid id, CancellationToken ct) =>
        Path.Combine(await files.DirectoryAsync(project, ct), "reference-videos", id.ToString("D"));
    public async Task<ReferenceVideoMedia> ImportAsync(Guid project, Stream content, string name, H3Settings settings, CancellationToken ct = default)
    {
        if (!string.Equals(Path.GetExtension(name), ".mp4", StringComparison.OrdinalIgnoreCase)) throw new WorkspaceStoreException("Choose an MP4 reference clip.");
        var id = Guid.NewGuid(); var directory = await DirectoryAsync(project, id, ct); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "video.mp4");
        try
        {
            await using (var target = File.Create(path))
            {
                var buffer = new byte[65536]; long length = 0; int count;
                while ((count = await content.ReadAsync(buffer, ct)) > 0)
                {
                    length += count;
                    if (length > ReferenceVideos.MaximumBytes) throw new WorkspaceStoreException("Reference clips must be no larger than 250 MB.");
                    await target.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            var info = await mediaTools.VideoInfoAsync(path, settings, ct);
            await using var source = File.OpenRead(path);
            var record = new ReferenceVideoMedia(id, Convert.ToHexString(await SHA256.HashDataAsync(source, ct)), source.Length,
                info.Width, info.Height, info.Frames, info.Fps, info.Duration ?? info.Frames / info.Fps, info.HasAudio);
            ReferenceVideos.ValidateMedia(record);
            await AtomicJsonFile.WriteAsync(Path.Combine(directory, "media.json"), record, ct);
            return record;
        }
        catch { File.Delete(path); throw; } // No published metadata; never replace another attachment.
    }
    public async Task<ReferenceVideoMedia> CopyTakeAsync(Guid project, Guid take, H3Settings settings, CancellationToken ct = default)
    {
        await using var source = await shots.OpenAsync(project, take, ShotTrashKind.Take, ct: ct) ?? throw new WorkspaceStoreException("The selected take is unavailable.");
        return await ImportAsync(project, source.Content, "take.mp4", settings, ct);
    }
    public async Task<AssetMedia?> OpenAsync(Guid project, Guid media, CancellationToken ct = default)
    {
        var directory = await DirectoryAsync(project, media, ct);
        var record = await AtomicJsonFile.ReadAsync<ReferenceVideoMedia>(Path.Combine(directory, "media.json"), ct);
        var path = Path.Combine(directory, "video.mp4");
        return record?.Id == media && File.Exists(path) ? new(File.OpenRead(path), "video/mp4", File.GetLastWriteTimeUtc(path)) : null;
    }
    public async Task ValidateAsync(Guid project, IEnumerable<ShotVideoBinding> bindings, CancellationToken ct = default)
    {
        foreach (var b in bindings)
        {
            ReferenceVideos.ValidateMedia(b.Media);
            var directory = await DirectoryAsync(project, b.Media.Id, ct);
            var metadata = await AtomicJsonFile.ReadAsync<ReferenceVideoMedia>(Path.Combine(directory, "media.json"), ct);
            await using var source = await OpenAsync(project, b.Media.Id, ct) ?? throw new WorkspaceStoreException($"Video reference {b.Name} is unavailable. Replace or remove it.");
            if (metadata != b.Media || source.Content.Length != b.Media.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(source.Content, ct)) != b.Media.Sha256)
                throw new WorkspaceStoreException($"Video reference {b.Name} changed on disk. Import it again before using it.");
        }
    }
    public async Task PrepareAsync(Guid project, Shot shot, string directory, List<PreparedVideoInput> inputs, H3Settings settings, CancellationToken ct = default)
    {
        await ValidateAsync(project, shot.Videos, ct);
        var resolved = ResolvedReferences.For(shot);
        await PrepareFramesAsync(project, resolved.Pictures.Where(p => p.Keyframe is not null).Select(p => p.Keyframe!.Frame), settings, ct);
        foreach (var picture in resolved.Pictures.Where(p => p.Keyframe is not null))
        {
            await using var source = await OpenFrameAsync(project, picture.Keyframe!.Frame, settings, ct);
            var file = $"keyframe-{picture.Number:D2}.png";
            await File.WriteAllBytesAsync(Path.Combine(directory, file), await ComfyReferenceImageEditor.PrepareSourcePngAsync(source.Content, picture.Keyframe.Crop, ct), ct);
            inputs.Add(new(file, false));
        }
        foreach (var entry in resolved.Videos.Where(v => v.Reel.EffectiveVisuals == ReelVisuals.FullReel))
        {
            var i = entry.BindingIndex; var binding = entry.Reel; var original = Path.Combine(directory, $"video-source-{i}.mp4");
            try
            {
                await using (var source = await OpenAsync(project, binding.Media.Id, ct) ?? throw new WorkspaceStoreException("Reference video disappeared during capture."))
                await using (var target = File.Create(original)) await source.Content.CopyToAsync(target, ct);
                await using (var captured = File.OpenRead(original))
                    if (captured.Length != binding.Media.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(captured, ct)) != binding.Media.Sha256)
                        throw new WorkspaceStoreException("Reference video changed during capture. Import it again.");
                var video = $"video-{i:D2}.mp4"; var audio = binding.UseSoundtrack ? $"video-audio-{i:D2}.wav" : null;
                await mediaTools.PrepareReferenceVideoAsync(original, Path.Combine(directory, video), audio is null ? null : Path.Combine(directory, audio),
                    binding.Media, ReferenceVideos.EffectiveFrames(binding.Media, shot), settings, ct);
                inputs.Add(new(video, false) { Kind = VideoInputKind.Video, VideoIndex = i });
                if (audio is not null) inputs.Add(new(audio, true) { Kind = VideoInputKind.VideoSoundtrack, VideoIndex = i });
            }
            finally { File.Delete(original); }
        }
        foreach (var audio in resolved.Audio.Where(a => a.Reel is not null && a.Reel.EffectiveVisuals != ReelVisuals.FullReel))
        {
            var binding = audio.Reel!; var excerpt = ResolvedReferences.Excerpt(binding);
            var file = $"reel-audio-{audio.BindingIndex:D2}.wav";
            await mediaTools.PrepareVoiceAsync(Path.Combine(await DirectoryAsync(project, binding.Media.Id, ct), "video.mp4"), Path.Combine(directory, file), excerpt.Start, excerpt.Duration, settings, ct);
            inputs.Add(new(file, true) { Kind = VideoInputKind.Audio, VideoIndex = audio.BindingIndex });
        }
    }
}
