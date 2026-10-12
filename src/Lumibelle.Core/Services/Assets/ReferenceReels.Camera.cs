using System.Globalization;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Assets;

public sealed record CameraReelPreset(ReelFraming Framing, string Label, double Seconds, string Summary,
    bool UsesDirection = false, string? RequiredInstructions = null);

public static partial class ReferenceReels
{
    public const string LongReelKeyframeHint = "Use keyframes to reference all angles. Direct video references use only the start of this reel, up to the generated shot’s length.";
    public static IReadOnlyList<CameraReelPreset> EnvironmentPresets { get; } = Array.AsReadOnly<CameraReelPreset>([
        new(ReelFraming.EnvironmentFullSet, "Full set", 15, "Create reference views from all four sides of the acting area: travel around the perimeter looking inward, hold wide shooting positions, then return.", true),
        new(ReelFraming.EnvironmentFeatureStudy, "Feature study", 15, "Create reference views of a named feature and its immediate surroundings with a complete 360° orbit and holds at four angles.", true,
            "Name the feature in Instructions, for example: the defendant’s podium and its steps."),
        new(ReelFraming.EnvironmentWalkthrough, "Walkthrough", 15, "Establish a long or connected space in one continuous journey along your route, then hold at the destination.",
            RequiredInstructions: "Describe the route and destination in Instructions, for example: from the gate, along the arcade, to the courtroom door."),
        new(ReelFraming.EnvironmentPath, "Custom", 10, "Create reference coverage along your own starting view, route and destination in one continuous reel.",
            RequiredInstructions: "Describe the custom camera path in Instructions: starting view, route and destination.")
    ]);

    public static IReadOnlyList<CameraReelPreset> CameraPresets(ReferenceReelDraft draft) =>
        IsEnvironment(draft) ? EnvironmentPresets : IsProp(draft) ? PropPresets : [];
    public static CameraReelPreset? CameraPreset(ReferenceReelDraft draft) => CameraPresets(draft).FirstOrDefault(p => p.Framing == draft.Framing);
    public const string EnvironmentReplacementRequired = "Choose Full set, Feature study, Walkthrough or Custom to replace this removed preset. Your references and written prompts are retained.";
    // Stored identifiers remain readable for captured history and unfinished drafts, but have no generation plan.
    public static bool NeedsEnvironmentReplacement(ReferenceReelDraft draft) => IsEnvironment(draft) && IsRemovedEnvironmentFraming(draft.Framing);
    private static bool IsRemovedEnvironmentFraming(ReelFraming framing) => framing is ReelFraming.EnvironmentTurn or ReelFraming.EnvironmentPan
        or ReelFraming.EnvironmentHalfTurn or ReelFraming.EnvironmentHeldViews or ReelFraming.EnvironmentPullBack or ReelFraming.EnvironmentApproach or ReelFraming.EnvironmentArc;
    public static string CameraDirectionLabel(ReferenceReelDraft draft, ReelCameraDirection direction) => IsEnvironment(draft)
        ? direction == ReelCameraDirection.Left ? "Counterclockwise" : "Clockwise" : direction.ToString();
    public static string? CompositionInstructionsIssue(ReferenceReelDraft draft) => NeedsEnvironmentReplacement(draft) ? EnvironmentReplacementRequired
        : CapturePreset(draft) is { } capture && draft.Duration < capture.MinimumSeconds
        ? $"{capture.Label} needs at least {capture.MinimumSeconds.ToString("0", CultureInfo.InvariantCulture)} requested seconds. Choose Custom for a shorter authored plan."
        : string.IsNullOrWhiteSpace(draft.Instructions) ? CameraPreset(draft)?.RequiredInstructions : null;
    public static string? DurationAdvice(ReferenceReelDraft draft) => CameraPreset(draft) is { } preset && draft.Duration < preset.Seconds
        ? $"Suggested duration: {preset.Seconds:0} seconds. A shorter reel gives the camera less time to move and settle." : null;
    public static bool ShowKeyframeHint(ReferenceReelDraft draft) => (IsCameraReel(draft) || IsCharacterCapture(draft)) && draft.Duration >= 10;
    public static string Label(ReferenceReelDraft draft) => Label(draft.Framing) +
        (CameraPreset(draft)?.UsesDirection == true ? $" · {CameraDirectionLabel(draft, draft.CameraDirection ?? ReelCameraDirection.Right)}" : "");

    public static void SelectCameraPreset(ReferenceReelDraft draft, ReelFraming framing)
    {
        if (!IsCameraReel(draft)) throw new WorkspaceStoreException("Choose an environment or prop recipe.");
        var preset = CameraPresets(draft).SingleOrDefault(p => p.Framing == framing)
            ?? throw new WorkspaceStoreException(IsProp(draft) ? "Choose a prop camera preset." : "Choose an environment camera preset.");
        draft.Framing = framing;
        draft.Duration = preset.Seconds;
        draft.CameraDirection ??= ReelCameraDirection.Right;
        draft.CheckedInputs = null;
    }

    public static void ReuseDirections(ReferenceReelDraft target, ReferenceReelDraft source)
    {
        ValidateProfile(source);
        if (OwnerCategory(target) != OwnerCategory(source)) throw new WorkspaceStoreException("Choose directions for the same kind of asset.");
        target.PresetVersion = source.PresetVersion;
        target.Framing = source.Framing;
        target.CameraDirection = source.CameraDirection;
        target.CaptureArticulation = source.CaptureArticulation;
        target.CaptureCloseUp = source.CaptureCloseUp;
        target.Instructions = source.Instructions;
        target.CheckedInputs = null;
    }

    public static string Views(ReferenceReelDraft draft)
    {
        if (IsVoiceReference(draft)) return VoiceReferenceViews(draft);
        if (IsCharacterCapture(draft)) return CharacterCaptureViews(draft);
        if (NeedsEnvironmentReplacement(draft)) throw new WorkspaceStoreException(EnvironmentReplacementRequired);
        if (CameraPreset(draft) is not { } preset) return Views(draft.Framing);
        if (IsProp(draft)) return PropViews(draft, preset);
        var frames = H3Policy.Frames(draft.Duration);
        var direction = CameraDirectionLabel(draft, draft.CameraDirection ?? ReelCameraDirection.Right).ToLowerInvariant();
        static string Seconds(int frame) => (frame / 24d).ToString("0.###", CultureInfo.InvariantCulture);
        var opening = (PictureCount(draft) > 0
            ? "Use the source pictures as evidence of the environment's appearance and physical layout. Begin with a recognizable view appropriate to this coverage; use the first picture's composition when it already suits the route. "
            : "Establish a recognizable opening view of the referenced environment appropriate to this coverage. ") +
            "Keep the same physical environment as the camera travels. One continuous shot without cuts. Maintain camera height, a level horizon and a fixed focal length. " +
            "Follow a plausible clear route, never through walls, furniture or the central subject. Stationary landmarks change apparent angle, overlap and screen position through visible parallax. ";
        // All phases use integer output frames; short durations reduce holds rather than run past the reel.
        var finalHold = Math.Min(24, frames / 5);
        var pause = Math.Min(12, frames / 10);
        if (draft.Framing is ReelFraming.EnvironmentFullSet or ReelFraming.EnvironmentFeatureStudy)
        {
            var fullSet = draft.Framing == ReelFraming.EnvironmentFullSet;
            var travel = frames - finalHold - pause * 3;
            var movement = fullSet
                ? $"Travel {direction}, as seen from above, in a complete 360° perimeter circuit around the acting area, looking inward across the set. Infer the acting area from the references and asset notes unless the author names it. " +
                    "Stay outside the central acting area, near the edges of the usable space, with enough distance for wide views of backgrounds and staging space. " +
                    "Keep a broad, approximately constant clearance; adapt the perimeter to the set's boundaries and obstacles. Do not approach the centre, zoom in, spin in place or orbit tightly around one prop. " +
                    "The route supplies shooting positions on all four sides of one coherent set, rather than four outward-facing views from its centre."
                : $"Travel {direction}, as seen from above, in a complete 360° orbit around the stationary feature named in Instructions, looking inward to keep the whole feature and its immediate surroundings framed. " +
                    "Maintain enough clearance to see the feature's different faces without clipping it or entering it. The feature stays stationary while the camera travels and the background shifts through strong parallax. " +
                    "This is reference coverage of that feature and its immediate surroundings, not coverage of the entire environment. Do not spin in place or rotate the feature.";
            List<string> phases = [$"[Shot 1] {opening}{movement} Complete all four sides within this one reel. " +
                "Move at a controlled pace fitted to the allotted travel time, with brief completely still holds for sharp reference images."];
            var coverage = fullSet
                ? new[] { "Hold a wide shooting position on the adjacent side, looking inward across the acting area.",
                    "Hold a wide shooting position on the opposite side, revealing the reverse background behind the opening camera.",
                    "Hold a wide shooting position on the remaining side, preserving relationships to all previously revealed landmarks.",
                    "Return near the opening shooting position only after travelling around all three other sides, preserving the same layout." }
                : new[] { "Hold the quarter-orbit view, showing the feature's adjacent face and immediate surroundings.",
                    "Hold the half-orbit view, showing the back of the feature and the reverse background.",
                    "Hold the three-quarter-orbit view, showing the remaining face and its relationship to the surroundings.",
                    "Return near the opening view only after showing all three other angles of the same feature." };
            var start = 0;
            for (var leg = 1; leg <= 4; leg++)
            {
                var stop = travel * leg / 4 + pause * (leg - 1);
                var end = stop + (leg == 4 ? finalHold : pause);
                phases.Add($"From {Seconds(start)} to {Seconds(stop)} seconds, travel {direction} along the next quarter of the route, approximately {leg * 90}° from the opening position by its end. " +
                    $"Hold the camera completely still from {Seconds(stop)} to {Seconds(end)} seconds. {coverage[leg - 1]}");
                start = end;
            }
            return string.Join("\n", phases);
        }
        var move = draft.Framing == ReelFraming.EnvironmentWalkthrough
            ? "Follow the named route through the long or connected space to the author's destination. Establish each connected section as the camera passes through it; " +
                "doorways, corners and landmarks pass naturally into and out of view, revealing how the spaces join. Look along the route and into the spaces being established, turning smoothly as needed without a gratuitous orbit. " +
                "Show the intended route within this one reel, with continuous visible progress and stable geometry. Fit a controlled travel pace to the available time."
            : "Follow the author's starting view, route and destination in one continuous camera movement, using the extent and speed they specify. " +
                "Show the intended reference coverage within this one reel through changes in perspective, occlusion and framing, arriving at the specified destination before the final hold. Fit the route to the available movement time.";
        return $"[Shot 1] {opening}Begin travelling immediately. {move} " +
            $"Complete the route by {Seconds(frames - finalHold)} seconds, then hold the final view completely still until {Seconds(frames)} seconds for a sharp reference image.";
    }
}
