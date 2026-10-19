using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.Assets;

public static partial class ReferenceReels
{
    public static bool HasVisualReferences(ReferenceReelDraft draft)
    {
        var references = ResolvedReferences.For(Inputs(draft));
        return references.Pictures.Count > 0 || references.Videos.Count > 0;
    }

    private static string[] VideoGuidance(ReferenceReelDraft draft) => ResolvedReferences.For(Inputs(draft)).Videos
        .Select(v => v.Reel.Description).Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();

    private static string VideoDefinitions(ReferenceReelDraft draft) => string.Concat(ResolvedReferences.For(Inputs(draft)).Videos.Select(v =>
        $"\n<Video {v.Number}>: partially_preserved. {v.Reel.Description} " +
        (IsEnvironment(draft) ? "Preserve the environment's layout, materials and lighting. " : IsProp(draft) ? "Preserve the prop's form, proportions, materials and details. " : "Preserve the indicated identity and appearance. ") +
        (v.Reel.EffectiveVisuals == ReelVisuals.RefMod ? ReelRefMods.UseGuidance + " " : "") +
        "Do not copy source actions, camera movement, cuts, timing or dialogue. Audio is selected separately."));

    public static List<ChatMessage> Messages(ReelCompositionRequest request, IReadOnlyList<byte[]> images,
        IReadOnlyList<RefModInspectionFrame>? modFrames = null)
    {
        modFrames ??= [];
        var videos = ResolvedReferences.For(Inputs(request.Draft)).Videos;
        var expectedFrames = videos.Where(v => v.Reel.EffectiveVisuals == ReelVisuals.RefMod).SelectMany(v => {
            ReelRefMods.ValidateBinding(v.Reel, true);
            return v.Reel.RefMod!.Recipe.FrameHashes.Select((hash, index) => (v.Number, Frame: index + 1, Hash: hash));
        });
        if (!expectedFrames.SequenceEqual(modFrames.Select(f => (f.VideoNumber, f.FrameNumber, f.Sha256))))
            throw new WorkspaceStoreException("The RefMod inspection frames do not match the captured visual references.");
        foreach (var frame in modFrames) ReelRefModStore.CheckHash(frame.Png, frame.Sha256);

        var messages = IsEnvironment(request.Draft) ? EnvironmentMessages(request, images, modFrames)
            : IsProp(request.Draft) ? PropMessages(request, images, modFrames) : CharacterMessages(request, images, modFrames);
        if (videos.Count > 0)
        {
            messages[0].Contents.Add(new TextContent("Use the supplied Video labels exactly. A sparse RefMod supplies selected still angles under one Video label; its inspection attachments are not additional Picture inputs. " +
                "Full reels supply visual video references to generation, but the composer has only their author-provided descriptions. Never claim to have inspected full video, motion or audio. " +
                "Use the selected references for appearance or environment guidance without copying source camera movement, actions, timing or words. Audio remains separate."));
            if (videos.Any(v => v.Reel.EffectiveVisuals == ReelVisuals.RefMod))
                messages[0].Contents.Add(new TextContent(ReelRefMods.UseGuidance));
        }
        foreach (var frame in modFrames) messages[1].Contents.Add(new DataContent(frame.Png, "image/png"));
        return messages;
    }

    private static object VideoContext(ReferenceReelDraft draft, int pictureCount, IReadOnlyList<RefModInspectionFrame> frames) => new {
        videos = ResolvedReferences.For(Inputs(draft)).Videos.Select(v => new { label = $"<Video {v.Number}>", v.Reel.Name,
            representation = v.Reel.EffectiveVisuals.ToString(), authorProvidedDescription = v.Reel.Description }),
        sparseInspectionAttachments = frames.Select((f, i) => new { attachment = pictureCount + i + 1,
            video = f.VideoNumber, frame = f.FrameNumber, f.ReelName, f.ViewName, f.Notes, f.Sha256 })
    };
}
