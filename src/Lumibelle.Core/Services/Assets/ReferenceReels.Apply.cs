using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public static partial class ReferenceReels
{
    public const string PairTextEdited = "the prompt or use guidance was edited";

    // What changed since a prompt pair was requested, in the author's terms (see AssistedApply).
    // Render settings such as the generation preset, resolution, turbo, LoRAs or lossless frames
    // shape the video, not the text the composer writes, so they never count as changes.
    public static IReadOnlyList<string> PairChanges(ReelCompositionRequest request, ReferenceReelDraft draft, AssetLookContext character,
        IEnumerable<CompositionInput> images, IReadOnlyList<ShotReferenceGuidance> guidance)
    {
        var was = Composed(request.Draft); var now = Composed(draft); var changes = new List<string>();
        if (now.Instructions != was.Instructions) changes.Add("the instructions changed");
        if (Json(now.Images) != Json(was.Images) || Json(now.KeyframeReels) != Json(was.KeyframeReels) || !images.SequenceEqual(request.Images) ||
            request.ImageGuidance.Count > 0 && !ShotReferences.SameEffective(request.ImageGuidance, guidance))
            changes.Add("the references or their guidance changed");
        if (character != request.Character) changes.Add("the character’s look or notes changed");
        // Compare everything else as if the changes named above had not been made.
        now.Instructions = was.Instructions; now.Images = was.Images; now.KeyframeReels = was.KeyframeReels;
        now.Prompt = was.Prompt; now.UseGuidance = was.UseGuidance;
        if (Fingerprint(now) != Fingerprint(was)) changes.Add("other recipe settings changed, such as framing, timing, voice or speech");
        if (draft.Prompt != request.Draft.Prompt || draft.UseGuidance != request.Draft.UseGuidance) changes.Add(PairTextEdited);
        return changes;
    }
    private static ReferenceReelDraft Composed(ReferenceReelDraft draft)
    {
        var copy = draft.Copy(); copy.CheckedInputs = null;
        copy.GenerationPreset = null; copy.Turbo = false; copy.TurboSteps = 0; copy.NativeResolution = false;
        copy.Resolution = null; copy.SaveLosslessFrames = null; copy.Loras = null; copy.ReelLoras = null;
        return copy;
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, AtomicJsonFile.Options);
}
