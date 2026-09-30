using System;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.Production;

public sealed record PromptDraftRecovery(PromptCompositionResult Result, IReadOnlyList<string> Sections, string AddedText);

public static class PromptComposer
{
    public static List<ChatMessage> BuildMessages(PromptCompositionRequest r, IReadOnlyList<byte[]> images, IReadOnlyList<RefModInspectionFrame>? modFrames = null)
    {
        CompositionDescriptions.Validate(r);
        var inspectImages = r.InspectReferenceImages != false;
        if (r.Images.Count != ResolvedReferences.For(r.Shot).Pictures.Count || images.Count != (inspectImages ? r.Images.Count : 0))
            throw new WorkspaceStoreException("The inspection images do not match the selected references.");
        modFrames ??= [];
        var selectedMods = ResolvedReferences.For(r.Shot).Videos.Where(v => v.Reel.EffectiveVisuals == ReelVisuals.RefMod).ToArray();
        var expectedFrames = selectedMods.Where(_ => inspectImages).SelectMany(v => {
            ReelRefMods.ValidateBinding(v.Reel, true);
            return v.Reel.RefMod!.Recipe.FrameHashes.Select((hash, index) => (v.Number, Frame: index + 1, Hash: hash));
        }).ToArray();
        if (!expectedFrames.SequenceEqual(modFrames.Select(f => (f.VideoNumber, f.FrameNumber, f.Sha256))))
            throw new WorkspaceStoreException("The RefMod inspection frames do not match the captured visual references.");
        foreach (var frame in modFrames) ReelRefModStore.CheckHash(frame.Png, frame.Sha256);
        var profile = r.Shot.Videos.Count > 0 ? "h3-compose-video-v1.txt" : "h3-compose-v1.txt";
        using var stream = typeof(PromptComposer).Assembly.GetManifestResourceStream("lumibelle.Services.AI.PromptProfiles." + profile)
            ?? throw new WorkspaceStoreException("The production composition profile is unavailable.");
        using var reader = new StreamReader(stream);
        var message = new ChatMessage(ChatRole.User, JsonSerializer.Serialize(new
        {
            shot = new { r.Shot.Title, r.Shot.Description, r.Shot.Duration, r.Shot.Dialogue, r.Shot.Characters, r.Shot.Atmosphere, r.Shot.Music, r.Shot.Aspect },
            generatedDurationSeconds = Shots.H3Policy.Seconds(r.Shot.Duration!.Value), r.SceneContext, r.NearbyShots,
            cutContinuity = new {
                transition = "hard_cut",
                previousShotProvided = r.NearbyShots.Any(s => s.StartsWith("Previous shot", StringComparison.OrdinalIgnoreCase)),
                nextShotProvided = r.NearbyShots.Any(s => s.StartsWith("Next shot", StringComparison.OrdinalIgnoreCase)),
                mustOpenDifferentlyFromPrevious = true },
            r.DirectingNotes, r.CurrentPrompt, r.RevisionNotes, r.Appearances,
            referenceImagesAttached = images.Count > 0 || modFrames.Count > 0,
            visualDescriptions = r.VisualDescriptions,
            references = r.Shot.Images.Select((b, i) => new { picture = i + 1, b.Name, b.AiUseHint, b.Crop, purpose = b.Purpose?.ToString(), use = b.Use?.ToString(),
                guidance = r.Guidance.Single(g => g.BindingId == b.Id).Effective }),
            reelKeyframes = ResolvedReferences.For(r.Shot).Pictures.Where(p => p.Keyframe is not null).Select(p => new { picture = p.Number, reel = p.Reel!.Name,
                ownerType = p.Reel.OwnerCategory?.ToString(), authorProvidedUseGuidance = p.Reel.Description, p.Keyframe!.Frame.Seconds, p.Keyframe.Crop, p.Keyframe.Notes }),
            sparseVisualReferences = selectedMods.Select(v => new { video = v.Number, v.Reel.Name,
                authorProvidedDescription = v.Reel.Description, v.Reel.RefMod!.Recipe.LatentFrames, v.Reel.RefMod.Recipe.Tokens,
                interpretation = ReelRefMods.UseGuidance }),
            sparseInspectionAttachments = modFrames.Select((f, i) => new { attachment = images.Count + i + 1,
                video = f.VideoNumber, frame = f.FrameNumber, f.ReelName, f.Notes, f.Sha256 }),
            videos = ResolvedReferences.For(r.Shot).Videos.Where(v => v.Reel.EffectiveVisuals == ReelVisuals.FullReel).Select(v => new { video = v.Number, v.Reel.Name, authorProvidedDescription = v.Reel.Description,
                duration = v.Reel.Media.Duration, effectiveDuration = ReferenceVideos.EffectiveSeconds(v.Reel.Media, r.Shot),
                v.Reel.UseSoundtrack, audio = ReferenceVideos.SoundtrackNumber(r.Shot, v.BindingIndex), speaker = v.Reel.UseSoundtrack ? v.Reel.Speaker : null }),
            reelAudio = ResolvedReferences.For(r.Shot).Audio.Where(a => a.Reel is not null && a.Reel.EffectiveVisuals != ReelVisuals.FullReel).Select(a => new { audio = a.Number, reel = a.Reel!.Name, authorProvidedUseGuidance = a.Reel.Description, a.Speaker, excerpt = ResolvedReferences.Excerpt(a.Reel) }),
            characterVoices = r.Shot.CharacterVoices?.Select(c => new { c.CharacterName, c.Source, c.SourceName, c.Speaker, c.FromDefault,
                audio = ResolvedReferences.For(r.Shot).Audio.FirstOrDefault(a => a.CharacterAssetId == c.AssetId)?.Number }),
            voices = ResolvedReferences.For(r.Shot).Audio.Where(a => a.Voice is not null).Select(a => new { audio = a.Number, a.SourceName, a.Speaker, a.Voice!.Start, a.Voice.Duration })
        }, AtomicJsonFile.Options));
        foreach (var image in images) message.Contents.Add(new DataContent(image, "image/png"));
        foreach (var frame in modFrames) message.Contents.Add(new DataContent(frame.Png, "image/png"));
        var instructions = reader.ReadToEnd() + "\nBefore returning JSON, verify that all six headings are present exactly once and in order. " +
            "Do not stop after detailed_description: overall_soundscape and non_diegetic_music are required even when there is no dialogue, no Audio input, or no music. " +
            "Use the supplied shot.Atmosphere and shot.Music as the sound directions. Always give non_diegetic_music explicit text; write 'No non-diegetic music.' when none is intended. " +
            "Silence is still an explicit sound direction, not a reason to omit a heading. State generatedDurationSeconds using digits followed by 'seconds'.";
        if (r.NearbyShots.Any(s => s.StartsWith("Previous shot", StringComparison.OrdinalIgnoreCase))) instructions +=
            "\nThe NearbyShots context may include a Previous shot entry with an accepted/composed prompt. Treat that previous shot as continuity context, not as a template to copy. " +
            "This shot follows the previous one in a normal hard cut. Preserve continuity of characters, props, wardrobe, environment and rough spatial orientation, but design a clearly distinct opening image. " +
            "Do not begin with the same camera position, framing, blocking emphasis or action beat as the previous shot unless explicitly requested. Never copy the previous shot's Picture, Video or Audio numbering into this shot.";
        if (r.Shot.CharacterVoices is not null) instructions += "\nCharacter voice choices are independent of appearance references. Only the enabled, numbered Audio inputs supply voice identity; disabled reel soundtracks do not. Use the supplied character/source names and explicit speaker mappings. A vocalizations-only voice supplies no dialogue. Never copy the reference recording's sample words or infer target dialogue from it.";
        if (inspectImages && r.Shot.Videos.Any(v => v.EffectiveVisuals is ReelVisuals.Keyframes or ReelVisuals.None)) instructions += "\nReels in keyframe mode are supplied as the numbered Picture images, not videos. Inspect these images directly. Their grouped use guidance describes intended uses; only the selected frames establish visible information. Character frames supply appearance; environment frames supply layout and setting. Audio-only reels provide voice references without visual information. Source actions, camera movements and sample dialogue are not target-shot instructions. Never invent Video identifiers for keyframe or audio-only reels.";
        if (inspectImages && selectedMods.Length > 0) instructions += "\nThe sparseVisualReferences are visual-only RefMods addressed by their numbered Video labels, not Picture labels. " +
            "The extra attached stills are inspection previews of those RefMods, mapped by sparseInspectionAttachments. Inspect them, but never create Picture identifiers for them. " +
            "They show selected views, not an actual motion sequence or its timing. Keep each reference's intended identity, outfit or setting without copying source poses, actions, cuts or camera travel. " +
            "Audio remains an independent numbered input. Do not infer voice or target dialogue from these silent previews. State only observed visual detail, not a claim to have watched a video. " +
            "The padded canvas previews are the original build inputs, not a fresh inspection of the remote latent. Do not describe the padding as part of the retained appearance.";
        if (r.VisualDescriptions is not null) instructions += CompositionDescriptions.Instructions;
        if (!inspectImages) instructions += CompositionDescriptions.TextOnlyInstructions;
        if (r.ReducedScriptContext) instructions += "\nThe scene text and neighbouring shots were left out to keep this request small. " +
            "Work from the shot, its references and the directing notes; do not invent surrounding scene events or claim continuity with unseen shots.";
        return [new(ChatRole.System, instructions), message];
    }
    public static PromptCompositionResult Parse(string raw, PromptCompositionRequest request)
    {
        var result = ReadResponse(raw);
        ProductionPolicy.ValidateGenerationPrompt(result.Prompt);
        return ProductionPolicy.PromptWarning(result.Prompt, request.Shot) is { } note ? result with { Notes = [note] } : result;
    }

    // Recheck saved failures after validation fixes without changing their raw response or submitting again.
    public static PromptDraftRecovery RecoverResponse(string raw, PromptCompositionRequest request)
    {
        // Recover the exact response for human review, including non-template prompts.
        return new(Parse(raw, request), [], "");
    }

    // Explicit local recovery only. Never modify the stored AI output or automatically apply it.
    public static PromptDraftRecovery RecoverSoundSections(string raw, PromptCompositionRequest request)
    {
        var result = ReadResponse(raw);
        var names = ProductionPolicy.SectionNames(result.Prompt);
        if (names.Length is not (4 or 5) || !names.SequenceEqual(ProductionPolicy.Sections.Take(names.Length)))
            throw new WorkspaceStoreException("Only missing final sound sections can be recovered from the saved shot settings.");
        var missing = ProductionPolicy.Sections.Skip(names.Length).ToArray();
        var prompt = result.Prompt;
        foreach (var section in missing)
        {
            var text = section == "overall_soundscape" ? request.Shot.Atmosphere : request.Shot.Music;
            if (string.IsNullOrWhiteSpace(text)) throw new WorkspaceStoreException($"The saved shot has no directions for {section}. Add that section manually.");
            prompt += $"\n\n{section}:\n{text}";
        }
        ProductionPolicy.ValidatePrompt(prompt, request.Shot);
        return new(result with { Prompt = prompt }, missing, prompt[result.Prompt.Length..].Trim());
    }

    private static PromptCompositionResult ReadResponse(string raw)
    {
        try
        {
            var text = raw.Trim();
            if (text.StartsWith("```json") && text.EndsWith("```")) text = text[7..^3].Trim();
            using var json = JsonDocument.Parse(text); var root = json.RootElement;
            // A blank field is not a clarification request. Models sometimes emit needsInput: "" alongside a
            // complete prompt; only a non-blank question should stop the response from being usable.
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("needsInput", out var needs) && needs.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(needs.GetString()))
                throw new WorkspaceStoreException("Composition needs your input: " + needs.GetString());
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("prompt", out var prompt) || !root.TryGetProperty("referenceUsage", out var usage) ||
                prompt.ValueKind != JsonValueKind.String || usage.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(usage.GetString()))
                throw new JsonException();
            return new(prompt.GetString()!, usage.GetString()!);
        }
        catch (JsonException) { throw new WorkspaceStoreException("The response is not a complete composition. Inspect it and retry; the accepted prompt is unchanged."); }
    }
}
