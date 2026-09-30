using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public static class AssetPickCatalog
{
    public const int MaximumRequestCharacters = 750_000;
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value, AtomicJsonFile.Options)));
    public static string Id(string kind, Guid id) => kind.ToLowerInvariant() + ":" + id.ToString("D");
    public static string ContextFingerprint(Guid project, Shot shot, string prompt, string notes) => Hash(new {
        project, Source = ProductionPolicy.SourceFingerprint(shot), shot.Characters,
        shot.Images, shot.Videos, shot.Voices, shot.CharacterVoices, prompt, notes
    });
    public static string DirectionFingerprint(Shot shot, string prompt, string notes) => Hash(new {
        Source = ProductionPolicy.SourceFingerprint(shot), shot.Characters, prompt, notes
    });
    private static bool ActiveLook(ReferenceAsset asset, Guid? look) => look is null || asset.Looks.Any(l => l.Id == look && !l.Archived);

    public static AssetPickCatalogue Capture(AssetLibrary library)
    {
        var owners = library.Assets.OrderBy(a => a.Id).Select(a => new AssetPickOwner(a.Id, a.Name, a.Category,
            a.Description, a.PreservationGuidance, a.Looks.Where(l => !l.Archived).OrderBy(l => l.Id)
                .Select(l => new AssetPickLook(l.Id, l.Name, l.Description, l.PreservationGuidance)).ToArray(), a.DefaultVoiceId)).ToArray();
        var candidates = new List<AssetPickCandidate>();
        foreach (var asset in library.Assets.OrderBy(a => a.Id))
        {
            foreach (var image in asset.Images.Where(i => ActiveLook(asset, i.LookId)).OrderBy(i => i.Id))
            {
                candidates.Add(new() {
                    Id = Id("image", image.Id), Kind = "Image", AssetId = asset.Id, SourceId = image.Id,
                    Name = image.Name ?? "Reference image", LookId = image.LookId,
                    Description = "", Guidance = image.PreservationGuidance,
                    Tags = image.Tags.ToArray(), Approved = image.IsReference, Cover = image.IsCover,
                    SourceFingerprint = Hash(new { image.Id, image.FileName, image.ContentType, image.Width, image.Height,
                        image.CreatedUtc, image.LookId, image.PreservationGuidance, image.Tags,
                        image.IsReference, image.IsCover })
                });
            }
            foreach (var reel in library.Reels.Where(r => r.AssetId == asset.Id && ActiveLook(asset, r.LookId)).OrderBy(r => r.Id))
            {
                var frames = reel.Keyframes?.Frames.Count ?? 0;
                var audio = reel.Media.HasAudio && reel.Generation?.Recipe.VoiceMode != ReelVoiceMode.Silent;
                var modes = new List<string> { nameof(ReelVisuals.FullReel) };
                if (frames > 0) modes.Add(nameof(ReelVisuals.Keyframes));
                if (frames is >= 2 and <= 9) modes.Add(nameof(ReelVisuals.RefMod));
                if (audio) modes.Add(nameof(ReelVisuals.None));
                var preferred = (asset.DefaultReelVisuals ?? ReelVisuals.Keyframes).ToString();
                if (!modes.Contains(preferred) || preferred == nameof(ReelVisuals.None))
                    preferred = frames > 0 ? nameof(ReelVisuals.Keyframes) : nameof(ReelVisuals.FullReel);
                candidates.Add(new() {
                    Id = Id("reel", reel.Id), Kind = "Reel", AssetId = asset.Id, SourceId = reel.Id,
                    Name = reel.Name, MediaId = reel.Media.Id, LookId = reel.LookId, Guidance = reel.UseGuidance,
                    Description = string.Join("\n", (reel.Keyframes?.Frames ?? []).Select(f => f.Notes).Where(n => !string.IsNullOrWhiteSpace(n))),
                    Duration = reel.Media.Duration, ExcerptDuration = Math.Min(5, reel.Media.Duration),
                    AudioAvailable = audio, VoiceDescription = reel.Generation?.Recipe.VoiceDescription ?? "",
                    Language = reel.Generation?.Recipe.Language ?? "", KeyframeCount = frames,
                    VisualModes = modes.ToArray(), PreferredVisualMode = preferred,
                    SourceFingerprint = Hash(new { reel.Id, reel.AssetId, reel.LookId, reel.Name, reel.UseGuidance, reel.Media,
                        reel.Keyframes, VoiceMode = reel.Generation?.Recipe.VoiceMode,
                        VoiceDescription = reel.Generation?.Recipe.VoiceDescription, Language = reel.Generation?.Recipe.Language })
                });
            }
            foreach (var voice in library.Voices.Where(v => v.AssetId == asset.Id).OrderBy(v => v.Id))
            {
                candidates.Add(new() {
                    Id = Id("voice", voice.Id), Kind = "Voice", AssetId = asset.Id, SourceId = voice.Id,
                    Name = voice.Name, DefaultVoice = voice.Id == asset.DefaultVoiceId, AudioAvailable = true,
                    Duration = voice.Duration, ExcerptStart = voice.Start, ExcerptDuration = voice.ExcerptDuration,
                    SourceFingerprint = Hash(voice)
                });
            }
        }
        return new(library.ProjectId, owners, candidates.ToArray());
    }

    public static void RequireCurrent(AssetPickRequest request, AssetLibrary library)
    {
        if (library.ProjectId != request.ProjectId || Hash(Capture(library)) != request.CatalogueFingerprint)
            throw new WorkspaceStoreException("The asset catalogue changed after this suggestion. Reopen references and request a new selection; your draft is unchanged.");
    }
}
