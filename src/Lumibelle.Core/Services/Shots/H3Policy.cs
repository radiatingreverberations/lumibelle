using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class H3Policy
{
    public const string Profile = "h3-single-take-v3";
    public static int Quality(Shot shot) => H3Presets.Key(shot) switch { "larry" => 6, "pdd" or "turbo8" or H3HyperFlow.Key => 8, "turbo4" or H3Presets.TurboLight => 4, _ => 20 };
    public static string QualityLabel(Shot shot) => H3Presets.Label(H3Presets.Key(shot));
    public static H3Sampling Sampling(Shot shot, H3Settings settings)
    {
        var key = H3Presets.Key(shot);
        if (key == H3HyperFlow.Key) return H3HyperFlow.Sampling(settings);
        return new(key switch { "standard" => "h3-standard-v1", "turbo8" => "h3-ref2v-turbo8-v1", "turbo4" => "h3-turbo4-v1", _ => "h3-" + key + "-v1" },
            Quality(shot), key is "turbo8" or "pdd" or H3Presets.EulerBeta ? "euler" : key == "larry" ? "MiniMaxH3TurboSampler" : "res_multistep",
            key is H3Presets.Beta or H3Presets.EulerBeta ? "beta" : "simple", 12, 3, H3Presets.Checkpoint(key, settings),
            key is "standard" or "spectrum" or H3Presets.Beta or H3Presets.EulerBeta ? 0 : key == H3Presets.TurboLight ? H3Presets.TurboLightStrength : 1);
    }
    public static string? TurboFileIssue(string file, int steps)
    {
        var name = Path.GetFileName(file.Replace('\\', '/'));
        if (name.Contains("hyperflow", StringComparison.OrdinalIgnoreCase)) return "Choose the HyperFlow generation preset; ordinary Turbo does not use its fixed sigma grid.";
        if (name.Contains("fl2v", StringComparison.OrdinalIgnoreCase)) return "Choose a Ref2V LoRA; FL2V requires a different workflow.";
        if (name.Contains(steps == 8 ? "4step" : "8step", StringComparison.OrdinalIgnoreCase)) return $"Choose a {steps}-step LoRA for this Turbo option.";
        return null;
    }
    public static int Frames(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds is < 1 or > 15) throw new WorkspaceStoreException("Choose a shot duration from 1 to 15 seconds.");
        return Math.Min(362, 5 + 17 * (int)Math.Ceiling(((seconds * 24) - 5) / 17d));
    }
    public static double Seconds(double requested) => Frames(requested) / 24d;
    public static (int Width, int Height) Size(string aspect, bool native) => (aspect, native) switch
    {
        ("16:9", false) => (832, 480), ("9:16", false) => (480, 832), ("1:1", false) => (640, 640),
        ("16:9", true) => (1344, 768), ("9:16", true) => (768, 1344), ("1:1", true) => (992, 992),
        _ => throw new WorkspaceStoreException("Choose landscape, portrait, or square.")
    };
    public static void ValidateSettings(H3Settings? s)
    {
        if (s is null || new[] { s.Model, s.Encoder, s.VideoVae, s.AudioVae, s.TurboLora, s.Turbo8StepLora, s.Ffmpeg, s.Ffprobe, s.LatentUpscaler }.Any(v => v is null) ||
            s.TimeoutSeconds is < 30 or > 86400) throw new WorkspaceStoreException("Video settings are invalid. Set a timeout from 30 to 86400 seconds.");
        H3Performance.Validate(s.Performance);
    }
    public static void Validate(Shot s, bool ready = false, bool requireScene = true, bool motionContext = false)
    {
        if (s is not null) lumibelle.Services.Production.ReferenceVideos.Validate(s, ready);
        if (s is null || s.Id == Guid.Empty || string.IsNullOrWhiteSpace(s.Title) || s.Title.Length > 500 || s.Description is null ||
            s.Dialogue is null || s.Images is null || s.Voices is null || s.Characters is null || s.Characters.Any(x => x is null) || s.Dialogue.Any(x => x is null) || s.Images.Any(x => x is null) || s.Voices.Any(x => x is null) || s.SourceBlockIds is null || s.SourceExcerpt is null || s.Atmosphere is null || s.Music is null)
            throw new WorkspaceStoreException("The shot contains invalid fields.");
        if (s.GenerationPreset is not null && !H3Presets.Keys.Contains(s.GenerationPreset)) throw new WorkspaceStoreException("Choose a supported generation preset.");
        H3Loras.ValidateSelections(s);
        if (s.NativeResolution && s.UpscalePreview) throw new WorkspaceStoreException("Choose either Native or Upscaled preview resolution.");
        if (s.Resolution is { } resolution && (!Enum.IsDefined(resolution) || s.NativeResolution != (resolution == VideoResolution.Native) || s.UpscalePreview))
            throw new WorkspaceStoreException("Choose one supported video resolution.");
        if (s.Characters.Any(c => c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 500) ||
            s.Characters.Select(c => c.Id).Distinct().Count() != s.Characters.Count ||
            s.Characters.Select(c => c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != s.Characters.Count)
            throw new WorkspaceStoreException("Give each shot character a distinct identity and name.");
        if (s.Characters.Any(c => c.Appearance is { } a && (a.AssetId == Guid.Empty || ready && (a.LookId == Guid.Empty || a.EndLookId == Guid.Empty || a.LookId == a.EndLookId))))
            throw new WorkspaceStoreException("Choose the character and distinct looks for each appearance assignment.");
        if (s.Duration.HasValue) Frames(s.Duration.Value);
        if (s.TurboSteps is not (4 or 8)) throw new WorkspaceStoreException("Choose Standard, 4-step Turbo, or 8-step Turbo.");
        VideoResolutions.Size(s);
        if (s.AspectOverride is not null) VideoResolutions.Size(s.AspectOverride, VideoResolutions.Selected(s));
        if (s.Images.Count > 9 || s.Voices.Count > 3 || s.Images.Select(x => x.Id).Distinct().Count() != s.Images.Count ||
            s.Images.Select(x => (x.Kind, x.AssetId, x.MediaId)).Distinct().Count() != s.Images.Count ||
            s.Voices.Select(x => x.VoiceId).Distinct().Count() != s.Voices.Count ||
            s.Dialogue.Select(x => x.Id).Distinct().Count() != s.Dialogue.Count)
            throw new WorkspaceStoreException("Use distinct references: up to nine images and three voices.");
        foreach (var image in s.Images)
        {
            if (image.InferUsage && (image.Use is not null || image.Purpose is not null)) throw new WorkspaceStoreException("Choose inferred usage or an explicit reference role.");
            if (!Enum.IsDefined(image.Kind) || image.Id == Guid.Empty || image.MediaId == Guid.Empty || image.Name is null || image.Role is null || image.Notes is null ||
                image.Purpose is { } purpose && !Enum.IsDefined(purpose) || image.Use is { } use && !Enum.IsDefined(use) || image.LookId == Guid.Empty ||
                image.Kind == ShotImageKind.AssetImage && image.AssetId == Guid.Empty) throw new WorkspaceStoreException("Invalid image reference.");
            if (image.PreservationOverride?.Length > 50000 || image.RepresentsId is { } character && !ShotReferences.Characters(s).Any(c => c.Id == character))
                throw new WorkspaceStoreException("Choose an existing character under Represents and keep preservation guidance under 50000 characters.");
            try { if (image.Crop is not null) ComfyReferenceImageEditor.ValidateCrop(image.Crop); }
            catch (lumibelle.Services.AI.AiGenerationException e) { throw new WorkspaceStoreException(e.Message, e); }
        }
        foreach (var role in new[] { ShotImageUse.FirstFrame, ShotImageUse.LastFrame })
            if (s.Images.Count(i => i.Use == role) > 1)
                throw new WorkspaceStoreException($"Choose only one {ReferenceSetups.UseLabel(role).ToLowerInvariant()} reference.");
        if (s.Images.Any(i => ReferenceSetups.IsAnchor(i) && (i.RepresentsId is not null || i.Purpose is not null)))
            throw new WorkspaceStoreException("Frame anchors cannot also be character identity or look references.");
        if (s.StartFrame is { } start)
        {
            if (start.TakeId == Guid.Empty || start.Frame < 0) throw new WorkspaceStoreException("Choose an existing take frame to start from.");
            if (s.Images.Any(i => i.Use == ShotImageUse.FirstFrame))
                throw new WorkspaceStoreException("This shot starts from a take frame, so it cannot also use a first-frame reference. Remove one of them.");
        }
        foreach (var line in s.Dialogue)
            if (line.Id == Guid.Empty || line.Speaker is null || line.Language is null || line.Text is null || ready &&
                (string.IsNullOrWhiteSpace(line.Speaker) || string.IsNullOrWhiteSpace(line.Language) || string.IsNullOrWhiteSpace(line.Text)))
                throw new WorkspaceStoreException("Give each dialogue line a speaker, language, and text.");
        foreach (var voice in s.Voices)
            if (voice.VoiceId == Guid.Empty || voice.AssetId == Guid.Empty || !double.IsFinite(voice.Start) || voice.Start < 0 ||
                !double.IsFinite(voice.Duration) || voice.Duration is < 1 or > 15 || string.IsNullOrWhiteSpace(voice.Speaker) ||
                ready && !motionContext && !s.Dialogue.Any(d => string.Equals(d.Speaker.Trim(), voice.Speaker.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                !(voice.CharacterAssetId is { } owner && s.CharacterVoices?.Any(c => c.AssetId == owner && c.Source == CharacterVoiceSource.Recording && string.IsNullOrEmpty(c.Speaker)) == true)) throw new WorkspaceStoreException("Assign each voice to a dialogue speaker and a 1–15 second excerpt.");
        if (s.Voices.Select(v => v.Speaker.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != s.Voices.Count)
            throw new WorkspaceStoreException("Assign only one voice reference to each speaker.");
        lumibelle.Services.Production.CharacterVoices.Validate(s);
        if (ready && (s.Duration is null || string.IsNullOrWhiteSpace(s.Description) || requireScene && (s.ApprovedScriptId is null || s.SceneId is null)))
            throw new WorkspaceStoreException(requireScene ? "Choose a script scene, describe the shot, and set its duration before generating." : "Describe the shot and set its duration before generating.");
    }
    public static string Fingerprint(Shot shot)
    {
        var s = shot.Copy(); s.SelectedTakeId = null;
        var json = JsonSerializer.SerializeToNode(s, AtomicJsonFile.Options)!;
        if (s.Videos.Count == 0) json.AsObject().Remove("videos");
        // Only effective patches change a take; preserve legacy fingerprints when none apply.
        var loras = H3Loras.Selections(s).Where(l => l.Enabled && l.Strength != 0).Select(l => new
            { server = AiProviderRegistry.NormalizeComfyUrl(l.Reference.ComfyUrl), file = l.Reference.FileName, strength = l.Strength }).ToArray();
        if (loras.Length == 0) json.AsObject().Remove("loras");
        else json["loras"] = JsonSerializer.SerializeToNode(loras, AtomicJsonFile.Options);
        // Preferences and setup provenance do not change the captured visual content.
        json.AsObject().Remove("aspectOverride");
        // Existing Standard/4-step shots must retain their original take fingerprints.
        if (!s.Turbo || s.TurboSteps == 4) json.AsObject().Remove("turboSteps");
        // Keep fingerprints of untouched v1 drafts stable when optional fields are absent.
        if (s.Characters.Count == 0) json.AsObject().Remove("characters");
        else foreach (var character in json["characters"]!.AsArray()) if (character!["appearance"] is null) character.AsObject().Remove("appearance");
        foreach (var image in json["images"]!.AsArray())
        {
            image!.AsObject().Remove("setupOrigin");
            if (image["use"] is null) image.AsObject().Remove("use");
            if (image!["representsId"] is null) image.AsObject().Remove("representsId");
            if (image["preservationOverride"] is null) image.AsObject().Remove("preservationOverride");
            if (image["lookId"] is null) image.AsObject().Remove("lookId");
            if (image["purpose"] is null) image.AsObject().Remove("purpose");
        }
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(json, AtomicJsonFile.Options)));
    }
    public static string Compile(Shot s, IReadOnlyList<ShotReferenceGuidance>? guidance = null, IReadOnlyList<ShotAppearanceContext>? appearances = null, bool motionContext = false)
    {
        Validate(s, true, requireScene: false, motionContext: motionContext);
        if (s.Characters.Any(c => c.Appearance is not null) && (appearances is null || !s.Characters.Where(c => c.Appearance is not null).Select(c => c.Id).SequenceEqual(appearances.Select(a => a.CharacterId))))
            throw new WorkspaceStoreException("Refresh the captured character looks before compiling the prompt.");
        return H3PromptCompiler.Compile(s, guidance, appearances, motionContext);
    }
}
