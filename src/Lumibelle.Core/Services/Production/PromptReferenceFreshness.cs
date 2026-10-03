using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Production;

public enum PromptReferenceState { Current, Missing, ReferencesChanged, NeedsReview, Checking }

public sealed record PromptReferenceCheck(PromptReferenceState State, string Detail)
{
    // Prompts and legacy baselines are still loading. Claim neither a problem nor a match.
    public static PromptReferenceCheck Checking { get; } =
        new(PromptReferenceState.Checking, "Checking this prompt against its references.");
    public bool NeedsAttention => State is not (PromptReferenceState.Current or PromptReferenceState.Checking);
    public string Label => State switch {
        PromptReferenceState.Missing => "Prompt missing",
        PromptReferenceState.ReferencesChanged => "References changed",
        PromptReferenceState.NeedsReview => "Review prompt",
        PromptReferenceState.Checking => "Checking…",
        _ => "References current"
    };
    public bool Matches(string filter) => State == PromptReferenceState.Checking || filter switch {
        "attention" => NeedsAttention,
        "missing" => State == PromptReferenceState.Missing,
        "changed" => State == PromptReferenceState.ReferencesChanged,
        "review" => State == PromptReferenceState.NeedsReview,
        _ => true
    };
}

// This is a reference-content signature, NOT a generation readiness check. Do not
// add save times, revision numbers, sampling settings, or remote cache receipts.
public static class PromptReferenceFreshness
{
    public const string Profile = "shot-prompt-references-v1:";
    public static bool IsFingerprint(string? value) => value is not null &&
        value.StartsWith(Profile, StringComparison.Ordinal) && value.Length == Profile.Length + 64 &&
        value[Profile.Length..].All(Uri.IsHexDigit);

    public static string Fingerprint(Shot shot, IReadOnlyList<ShotReferenceGuidance> guidance)
    {
        var resolved = ResolvedReferences.For(shot);
        return Profile + ReferenceSetups.Hash(new {
            Pictures = resolved.Pictures.Select(p => p.Image is { } image
                ? Image(image, guidance)
                : p.Continuity is { } frame ? new { Continuity = new { frame.TakeId, frame.Frame } }
                : Keyframe(p.Reel!, p.Keyframe!)).ToArray(),
            Videos = resolved.Videos.Select(v => new {
                v.Number, Name = Text(v.Reel.Name), Description = Text(v.Reel.Description),
                v.Reel.OwnerAssetId, v.Reel.OwnerCategory, Visuals = v.Reel.EffectiveVisuals,
                Media = Media(v.Reel.Media),
                Frames = v.Reel.EffectiveVisuals == ReelVisuals.RefMod
                    ? v.Reel.Keyframes?.Frames.Select(f => Frame(f)).ToArray() : null,
                // Source pixels/canvas matter; the VAE filename, server, build ID,
                // recipe key and selected-frame IDs do not describe prompt content.
                RefMod = v.Reel.EffectiveVisuals == ReelVisuals.RefMod && v.Reel.RefMod is { } mod
                    ? new { mod.Recipe.Width, mod.Recipe.Height, mod.Recipe.FrameHashes } : null
            }).ToArray(),
            Audio = resolved.Audio.Select(a => new {
                a.Number, Speaker = Text(a.Speaker), SourceName = Text(a.SourceName), a.CharacterAssetId,
                Recording = a.Voice is { } voice ? new {
                    voice.VoiceId, voice.AssetId, voice.CharacterAssetId, voice.Start, voice.Duration
                } : null,
                Reel = a.Reel is { } reel ? new {
                    Media = Media(reel.Media), Name = Text(reel.Name), Description = Text(reel.Description),
                    Excerpt = ResolvedReferences.Excerpt(reel)
                } : null
            }).ToArray()
        });
    }

    private static object Image(ShotImageBinding image, IReadOnlyList<ShotReferenceGuidance> guidance)
    {
        var resolved = guidance.SingleOrDefault(g => g.BindingId == image.Id)
            ?? throw new WorkspaceStoreException("Reference guidance is unavailable for prompt comparison.");
        return new {
            Source = "image", image.Kind, image.AssetId, image.MediaId,
            Name = Text(image.Name), Role = Text(image.Role), Notes = Text(image.Notes),
            image.RepresentsId, image.LookId, image.Purpose, image.Use, image.InferUsage,
            AiUseHint = Text(image.AiUseHint), Crop = Crop(image.Crop),
            Guidance = Text(resolved.Effective), Phase = Text(resolved.Phase)
        };
    }
    private static object Keyframe(ShotVideoBinding reel, ReelKeyframe frame) => new {
        Kind = "keyframe", reel.OwnerAssetId, reel.OwnerCategory,
        Name = Text(reel.Name), Description = Text(reel.Description), Frame = Frame(frame)
    };
    private static object Frame(ReelKeyframe frame) => new {
        frame.Frame, Crop = Crop(frame.Crop), Notes = Text(frame.Notes)
    };
    private static object Media(ReferenceVideoMedia media) => new {
        media.Id, media.Sha256, media.Width, media.Height, media.Frames, media.Fps, media.Duration, media.HasAudio
    };
    private static object Crop(ImageCropRegion? crop) => new {
        X = crop?.X ?? 0d, Y = crop?.Y ?? 0d,
        Width = crop?.Width ?? 1d, Height = crop?.Height ?? 1d
    };
    private static string Text(string? value) => (value ?? "").Replace("\r\n", "\n").Trim();

    public static PromptReferenceCheck Compare(ProductionComposition? composition, string? currentFingerprint)
    {
        if (composition is null || string.IsNullOrWhiteSpace(composition.Prompt))
            return new(PromptReferenceState.Missing, "Write or compose a prompt for this shot.");
        if (composition.Accepted is not { } accepted)
            return new(PromptReferenceState.NeedsReview, "This prompt has not been reviewed against the selected references.");
        var baseline = accepted.ReferenceFingerprint;
        if (!IsFingerprint(currentFingerprint))
            return new(PromptReferenceState.NeedsReview, "The references could not be compared. Check the selected references before reviewing the prompt.");
        if (baseline is not null && !IsFingerprint(baseline))
            return new(PromptReferenceState.NeedsReview, "This prompt's reference comparison uses an unsupported format. Review it against the current references.");
        if (baseline is not null && baseline != currentFingerprint)
            return new(PromptReferenceState.ReferencesChanged,
                "The selected references, their order, crops, use guidance or voice mappings differ from the last reviewed prompt. Revise the prompt, or mark it reviewed if the text still fits.");
        if (Text(accepted.Prompt) != Text(composition.Prompt))
            return new(PromptReferenceState.NeedsReview, "The prompt was edited after its last review. Review it against the selected references.");
        if (baseline is null)
            return new(PromptReferenceState.NeedsReview,
                "This prompt has no saved reference comparison. Review it once to establish a baseline; this is not evidence that its references changed.");
        return new(PromptReferenceState.Current, "The prompt's reviewed references match the current selection.");
    }
}
