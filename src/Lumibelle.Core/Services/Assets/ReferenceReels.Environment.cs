using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.Assets;

public static partial class ReferenceReels
{
    public const string EnvironmentProfile = "environment-reel-v1";
    private const string EnvironmentLayoutPreservation = "Preserve physical positions and relationships within the environment while screen positions change with the viewpoint. " +
        "Objects may become occluded, enter or leave the frame naturally. Image-relative left, right and centre describe the opening view only; they are not fixed positions in every composition. ";
    public const string EnvironmentUseGuidance = "Visual environment reference intended to establish spatial layout, architecture, relative placement, materials, furnishings and lighting, including plausible areas completed beyond the source pictures. These are intended uses, not verified output. Reuse the setting from different angles; choose the target shot's own framing, camera movement, action and dialogue. Do not copy the reel's camera turn, travel path or timing. Explicit target instructions may change lighting or other conditions.";
    public static bool Supports(AssetCategory category) => category is AssetCategory.Character or AssetCategory.Environment or AssetCategory.Prop;
    public static bool IsEnvironment(ReferenceReelDraft draft) => draft.PresetVersion == EnvironmentProfile;
    // Environment and prop reels are silent camera studies with no look, speaker or recording.
    public static bool IsCameraReel(ReferenceReelDraft draft) => IsEnvironment(draft) || IsProp(draft);
    public static AssetCategory OwnerCategory(ReferenceReelDraft draft) =>
        IsEnvironment(draft) ? AssetCategory.Environment : IsProp(draft) ? AssetCategory.Prop : AssetCategory.Character;
    public static IReadOnlyList<ReelFraming> Framings(ReferenceReelDraft draft) => IsCameraReel(draft)
        ? CameraPresets(draft).Select(p => p.Framing).ToArray()
        : [ReelFraming.ContinuousTurn, ReelFraming.BodyToFace, ReelFraming.ThreeAngles, ReelFraming.SideRearFace, .. CharacterCapturePresets.Select(p => p.Framing), ReelFraming.CharacterVoiceReference, ReelFraming.Custom];

    public static ReferenceReelDraft NewDraft(ReferenceAsset owner, Guid? look = null, AssetLibrary? library = null)
    {
        if (!Supports(owner.Category)) throw new WorkspaceStoreException("Reference reels belong to characters, environments or props.");
        var draft = new ReferenceReelDraft { SaveLosslessFrames = true, AssetId = owner.Id, LookId = look, Name = owner.Name + " reference", Speaker = owner.Name };
        if (owner.Category == AssetCategory.Environment)
            draft = draft with { PresetVersion = EnvironmentProfile, Framing = ReelFraming.EnvironmentFullSet, Duration = 15, CameraDirection = ReelCameraDirection.Right, Aspect = "16:9",
                VoiceMode = ReelVoiceMode.Silent, Speaker = "", Language = "", Line = "" };
        if (owner.Category == AssetCategory.Prop)
            draft = draft with { PresetVersion = PropProfile, Framing = ReelFraming.PropOrbit, Duration = 15, CameraDirection = ReelCameraDirection.Right,
                VoiceMode = ReelVoiceMode.Silent, Speaker = "", Language = "", Line = "" };
        ValidateOwner(draft, owner);
        if (owner.Category == AssetCategory.Character && library is not null && lumibelle.Services.Production.CharacterVoices.Default(owner, library) is { } voice) {
            draft.VoiceMode = ReelVoiceMode.ExistingRecording;
            draft.Voice = new() { AssetId = voice.AssetId, VoiceId = voice.Id, Speaker = draft.Speaker, Start = voice.Start, Duration = voice.ExcerptDuration };
        }
        return draft;
    }
    public static ReferenceReelDraft DraftFromImage(ReferenceAsset owner, Guid imageId, AssetLibrary library)
    {
        var image = owner.Images.SingleOrDefault(i => i.Id == imageId)
            ?? throw new WorkspaceStoreException("Restore this image before creating a reference reel from it.");
        var look = owner.Category == AssetCategory.Character && owner.Looks.Any(l => l.Id == image.LookId && !l.Archived) ? image.LookId : null;
        var draft = NewDraft(owner, look, library);
        draft.Images = [new() { Kind = ShotImageKind.AssetImage, AssetId = owner.Id, MediaId = image.Id,
            Name = image.Name ?? owner.Name, LookId = look, InferUsage = true }];
        return draft;
    }
    public static void ValidateProfile(ReferenceReelDraft draft)
    {
        if (draft.PresetVersion is not (Profile or EnvironmentProfile or PropProfile) ||
            !Framings(draft).Contains(draft.Framing) && !NeedsEnvironmentReplacement(draft))
            throw new WorkspaceStoreException("Choose a camera preset supported by this reel's profile.");
        ValidateCharacterCaptureOptions(draft);
        ReelSpeech.Validate(draft);
        if (draft.CameraDirection is { } direction && (!IsCameraReel(draft) || !Enum.IsDefined(direction)))
            throw new WorkspaceStoreException("Choose a valid camera direction for this environment or prop.");
        if (IsProp(draft) && (draft.LookId is not null || draft.VoiceMode != ReelVoiceMode.Silent || draft.Voice is not null ||
            !string.IsNullOrEmpty(draft.Speaker) || !string.IsNullOrEmpty(draft.Language) || !string.IsNullOrEmpty(draft.Line) || !string.IsNullOrEmpty(draft.VoiceDescription)))
            throw new WorkspaceStoreException("Prop reels are silent and have no look, speaker or recording. Use a prop recipe.");
        if (IsEnvironment(draft) && (draft.LookId is not null || draft.VoiceMode != ReelVoiceMode.Silent || draft.Voice is not null ||
            !string.IsNullOrEmpty(draft.Speaker) || !string.IsNullOrEmpty(draft.Language) || !string.IsNullOrEmpty(draft.Line) || !string.IsNullOrEmpty(draft.VoiceDescription)))
            throw new WorkspaceStoreException("Environment reels are silent and have no look, speaker or recording. Use an environment recipe.");
    }
    public static void ValidateOwner(ReferenceReelDraft draft, ReferenceAsset owner)
    {
        ValidateProfile(draft);
        if (draft.AssetId != owner.Id || owner.Category != OwnerCategory(draft))
            throw new WorkspaceStoreException("The reel recipe no longer matches its character, environment or prop. Restore the original asset or create a new recipe.");
    }
    public static void ValidateComposition(ReferenceReelDraft draft)
    {
        Validate(draft);
        if (CompositionInstructionsIssue(draft) is { } issue) throw new WorkspaceStoreException(issue);
    }
    private static ReelPromptPair EnvironmentPreset(ReferenceReelDraft d, AssetLibrary? library)
    {
        ValidateComposition(d);
        if (!HasVisualReferences(d)) throw new WorkspaceStoreException("Select visual references before building the preset.");
        var seconds = H3Policy.Seconds(d.Duration).ToString("0.###", CultureInfo.InvariantCulture);
        var owner = library?.Assets.FirstOrDefault(a => a.Id == d.AssetId);
        var details = PictureGuidance(d, library);
        var references = string.Join("\n", details.Select((g, n) => $"<Picture {n + 1}>: Evidence of the same environment's appearance and physical layout, not a requirement to keep its exact framing. Preserve environmental features and their physical relationships. {g}")) + VideoDefinitions(d);
        const string structure = "One continuous camera shot through a stable space, with pauses for readable reference views. Complete the intended coverage within this one reel.";
        var instructions = string.IsNullOrWhiteSpace(d.Instructions) ? "" : $"\nAuthor directions take precedence over conflicting parts of the default camera plan; follow one coherent plan:\n{d.Instructions}";
        var prompt = $"subject_definitions:\n<Subject 1> is {owner?.Name ?? d.Name}, the same environment throughout.\n{references}\n\nsummary:\nA silent {d.Aspect} environment reference reel lasting {seconds} seconds, establishing a coherent space for later shots from different angles.\n\nretention_analysis:\n<Subject 1>: fully_preserved. Preserve architecture, relative placement, materials, furnishings and lighting. {EnvironmentLayoutPreservation}Complete unseen areas plausibly and keep revealed structures stable. {owner?.Description} {owner?.PreservationGuidance}\n\ndetailed_description:\nExactly {seconds} seconds total. {structure} No people or unrelated activity. Preserve the source visual style and lighting. Keep furnishings stationary, geometry coherent, exposure stable and environmental details in deep focus.\n\n{Views(d)}{instructions}\n\noverall_soundscape:\nSilent reference. No speech, ambient sound or audible soundtrack.\n\nnon_diegetic_music:\nNo non-diegetic music.";
        var coverage = d.Framing switch {
            ReelFraming.EnvironmentFeatureStudy => "Intended coverage is one feature and its immediate surroundings, not the entire environment.",
            ReelFraming.EnvironmentWalkthrough => "Intended coverage is the connected spaces along the authored route, not areas outside it.",
            ReelFraming.EnvironmentFullSet => "Intended coverage is the acting area and its backgrounds from wide shooting positions on all four sides.",
            _ => "Intended coverage is the environment revealed by the authored reference plan."
        };
        var pair = new ReelPromptPair(prompt, EnvironmentUseGuidance + "\n" + coverage + "\nIntended environmental features: " + string.Join("\n", new[] { owner?.Description, owner?.PreservationGuidance }.Concat(details).Concat(VideoGuidance(d)).Where(t => !string.IsNullOrWhiteSpace(t))));
        ValidatePair(pair, d);
        return pair;
    }
    private static List<ChatMessage> EnvironmentMessages(ReelCompositionRequest request, IReadOnlyList<byte[]> images, IReadOnlyList<RefModInspectionFrame> modFrames)
    {
        ValidateComposition(request.Draft);
        var rules = "Compose an environment reference reel. Return ordinary JSON with exactly prompt and useGuidance strings, including both when revising. " +
            "Use six headings with colons, in order: subject_definitions, summary, retention_analysis, detailed_description, overall_soundscape, non_diegetic_music. " +
            "Define the environment as <Subject 1>. Reference every selected <Picture N> and <Video N> in order. Pictures establish appearance and physical layout; allow an opening composition appropriate to the coverage, using the first picture's composition when it already suits the route. Additional pictures constrain the same location. " +
            "Inspect only the supplied cropped still images. Asset notes and image guidance are author direction. Never claim video or audio inspection. " +
            "Preserve visible architecture, relative placement, materials, furnishings and lighting. " + EnvironmentLayoutPreservation +
            "Complete unseen areas plausibly; they are new design, not a verified reconstruction. Keep revealed structures stable throughout the camera movement. " +
            "All requested coverage must be generated within this one reel; never propose chaining separate generations. Use the supplied camera plan and timing as a starting point. Explicit author instructions take precedence: resolve conflicts into one coherent plan rather than appending contradictory moves. " +
            "Write camera motion as natural English within the shot, stating movement type, extent and pace. Angles and timestamps alone do not describe compositional change. Do not soften complete coverage into a modest adjustment or a short fragment requiring other generations. " +
            "Describe observable progress and the destination: which supplied-image landmarks leave the frame, what enters, and how the ending view differs. Confine screen-relative placement to the opening description; use physical relationships for retention and use guidance. " +
            "Full set travels a complete 360-degree perimeter circuit outside the central acting area, looking inward across it with wide framing and enough distance to show backgrounds and staging space. Infer the acting area from references and notes unless named. Never replace this with an outward-facing turn from the centre, a tight orbit of a prop or a push toward the centre. " +
            "Feature study completes a 360-degree orbit around the named stationary feature with the whole feature and immediate surroundings framed; it does not promise the entire set. Both circuits must pass adjacent, opposite and remaining-side shooting positions before returning, with four supplied holds; matching the opening and ending alone is insufficient. Clockwise and counterclockwise describe camera travel as seen from above, not a pan direction. " +
            "Walkthrough follows the author's route and destination through a long or connected space, revealing how its sections join. Custom follows the author's starting view, route and destination. Every preset uses one continuous shot without cuts; keep camera height and focal length stable, with no zoom. Translation follows a plausible clear route, never through walls or furniture. Preserve the source visual style, stationary furnishings, stable exposure, deep focus and newly revealed geometry. " +
            "Retain the supplied settling holds, scaling pacing to the generated duration and explicit author instructions. Use one [Shot 1] section in detailed_description for the entire continuous camera route; describe travel and holds within it, without cut timestamps. State the supplied generated duration exactly. No people or unrelated activity. " +
            "This reel is silent: no dialogue, speaker labels, audio references, ambience or music. " + SoundSectionInstructions +
            "Use guidance must describe the intended spatial layout and recognizable environmental features, including newly completed areas. It must be independent of reference numbering and clear that these are intended uses, not verified output. " +
            "Scope use guidance to the selected coverage: Full set establishes the acting area and backgrounds from all sides; Feature study covers only its feature and immediate surroundings; Walkthrough covers connected spaces along the route. Never promise whole-set coverage from a feature study or a partial route. " +
            "Later shots use the setting from different angles with their own framing, camera motion, action and dialogue; do not propagate this reel's camera turn, route or timing. Explicit target-shot instructions may change lighting or other conditions.";
        // Keep the historical request wire shape, but present the captured asset
        // notes under a neutral name to the environment composer.
        var asset = request.Character;
        List<AIContent> content = [new TextContent(JsonSerializer.Serialize(new {
            draft = request.Draft, environment = new { asset.AssetId, Name = asset.AssetName, VisualNotes = asset.IdentityNotes, PreservationGuidance = asset.IdentityGuidance },
            images = request.Images, imageGuidance = request.ImageGuidance, pictures = PictureGuidance(request.Draft, null),
            videoReferences = VideoContext(request.Draft, images.Count, modFrames), presetViews = Views(request.Draft), generatedSeconds = H3Policy.Seconds(request.Draft.Duration)
        }, AtomicJsonFile.Options))];
        foreach (var image in images) content.Add(new DataContent(image, "image/png"));
        return [new(ChatRole.System, rules), new(ChatRole.User, content)];
    }
}
