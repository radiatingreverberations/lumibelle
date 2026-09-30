using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Services.Assets;

public static partial class ReferenceReels
{
    public const string Profile = "character-reel-v1";
    public static string Label(ReelFraming value) => CharacterCapturePresets.FirstOrDefault(p => p.Framing == value)?.Label ?? EnvironmentPresets.FirstOrDefault(p => p.Framing == value)?.Label ?? PropPresets.FirstOrDefault(p => p.Framing == value)?.Label ?? (value switch { ReelFraming.CharacterVoiceReference => "Voice reference", ReelFraming.BodyToFace => "Body → face", ReelFraming.ThreeAngles => "Three angles", ReelFraming.Custom => "Custom", ReelFraming.SideRearFace => "Side → rear → face", _ => "Continuous turn" });
    public static string Views(ReelFraming value) => value switch
    {
        ReelFraming.CharacterVoiceReference => throw new ArgumentException("Voice reference timing requires the complete reel recipe.", nameof(value)),
        _ when EnvironmentPresets.Any(p => p.Framing == value) => throw new ArgumentException("Environment camera timing requires the complete reel recipe.", nameof(value)),
        _ when PropPresets.Any(p => p.Framing == value) => throw new ArgumentException("Prop camera timing requires the complete reel recipe.", nameof(value)),
        _ when CharacterCapturePresets.Any(p => p.Framing == value) => throw new ArgumentException("Character capture timing requires the complete reel recipe.", nameof(value)),
        ReelFraming.BodyToFace => "[Shot 1] Hold a stable full-body frontal view for the first 40% of the reel so the outfit, silhouette and hands are readable.\n\n[Shot 2] Cut to a stable face close-up for the remaining 60%, keeping facial detail clear.",
        ReelFraming.ThreeAngles => "[Shot 1] Hold a medium-full frontal view for the first 40%.\n\n[Shot 2] Cut to a held three-quarter view for the next 30%.\n\n[Shot 3] Cut to a held profile view for the final 30%. Preserve the same character and outfit across all cuts.",
        ReelFraming.SideRearFace => "[Shot 1] Hold a stable full-body profile view for the first 30%, showing the character's silhouette and clothing from the side.\n\n[Shot 2] Cut to a full-body rear view for the next 40%. Gently raise both arms to shoulder height and hold, keeping both hands visible and the back of the outfit readable.\n\n[Shot 3] Cut to a frontal face close-up for the final 30%, with a restrained push-in and readable facial detail. Preserve the same character and outfit across all cuts.",
        ReelFraming.Custom => "No prescribed framing. Design views from the author's instructions.",
        _ => "[Shot 1] One continuous shot begins in a stable medium-full frontal view. The character turns gently toward three-quarter, with restrained natural motion and visible hands. Keep the face and outfit readable throughout."
    };
    public static string Fingerprint(ReferenceReelDraft draft)
    {
        var copy = draft.Copy(); copy.Revision = 0; copy.PendingJobId = null; copy.ResolvedJobs = [];
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(copy, AtomicJsonFile.Options)));
    }
    public static string InputsFingerprint(ReferenceReelDraft draft)
    { var copy = draft.Copy(); copy.Prompt = copy.UseGuidance = ""; copy.CheckedInputs = null; return Fingerprint(copy); }

    public static int PictureCount(ReferenceReelDraft draft) => ResolvedReferences.For(Inputs(draft)).Pictures.Count;
    public static string[] PictureGuidance(ReferenceReelDraft draft, AssetLibrary? library) =>
        ResolvedReferences.For(Inputs(draft)).Pictures.Select(p => p.Image is { } image
            ? ShotReferences.Resolve(image, library ?? new() { ProjectId = Guid.Empty }, new()).Effective
            : $"Selected still from {p.Reel!.Name} at {p.Keyframe!.Frame.Seconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds. {p.Reel.Description} {p.Keyframe.Notes} Only this selected frame is visual evidence; source camera motion, actions and dialogue are not instructions for this reel.").ToArray();

    // Legacy engine projection only: no persisted shot, scene or source snapshot.
    public static Shot Inputs(ReferenceReelDraft d) => new()
    {
        Id = d.Id, Title = d.Name, Description = IsEnvironment(d) ? "Environment reference reel" : IsProp(d) ? "Prop reference reel" : "Character reference reel", Duration = d.Duration, Aspect = d.Aspect,
        GenerationPreset = d.GenerationPreset, NativeResolution = VideoResolutions.Selected(d) == VideoResolution.Native, Turbo = d.Turbo, TurboSteps = d.TurboSteps,
        Images = ShotCopy.Of(d.Images), Videos = ShotCopy.Of(d.KeyframeReels ?? []), Loras = d.Loras?.ToArray(), ShotLoras = d.ReelLoras?.ToArray(), SaveLosslessFrames = d.SaveLosslessFrames ?? false,
        UpscalePreview = ReelGenerationSetups.UpscalePreview(d),
        Dialogue = IsCameraReel(d) || d.VoiceMode == ReelVoiceMode.Silent ? [] : [new() { Id = d.Id, Speaker = d.Speaker, Language = d.Language, Text = d.Line }],
        Voices = !IsCameraReel(d) && d.VoiceMode == ReelVoiceMode.ExistingRecording && d.Voice is not null ? [d.Voice with { Speaker = d.Speaker }] : []
    };
    // Use a fresh identity so results from an earlier request cannot refill a cleared draft.
    public static ReferenceReelDraft SimilarDraft(ReferenceReelDraft source, ReferenceAsset owner) => source.Copy() with {
        Id = Guid.NewGuid(), Revision = 0, AssetId = owner.Id,
        LookId = source.AssetId == owner.Id && owner.Looks.Any(l => l.Id == source.LookId && !l.Archived) ? source.LookId : null,
        CheckedInputs = null, PendingJobId = null, ResolvedJobs = []
    };
    public static ReferenceReelDraft ClearDraftContent(ReferenceReelDraft source) => source.Copy() with {
        Id = Guid.NewGuid(), Revision = 0, LookId = null, Name = "", Speaker = "", Line = "", VoiceDescription = null,
        Images = [], KeyframeReels = null, Voice = null, Instructions = "", Prompt = "", UseGuidance = "",
        CheckedInputs = null, PendingJobId = null, ResolvedJobs = []
    };
    public static void Validate(ReferenceReelDraft d, bool ready = false)
    {
        ValidateProfile(d);
        if (ReelGenerationSetups.TakeCount(d) is < 1 or > 4 || d.GenerationSetup is { Settings.Seed: < 0 } ||
            d.GenerationSetup is { } setup && (setup.Id == Guid.Empty || setup.Version < 1))
            throw new WorkspaceStoreException("Choose a valid generation preset and between one and four takes.");
        VideoResolutions.Size(d);
        if (d.VoiceDescription?.Length > 2000)
            throw new WorkspaceStoreException("Keep the voice description under 2000 characters.");
        if (d.Id == Guid.Empty || d.AssetId == Guid.Empty || d.LookId == Guid.Empty || d.Revision < 0 ||
            !Enum.IsDefined(d.Framing) || !Enum.IsDefined(d.VoiceMode) ||
            d.Name is null || d.Name.Length > 500 || d.Speaker is null || d.Speaker.Length > 500 || d.Language is null || d.Language.Length > 100 ||
            d.Line is null || d.Line.Length > 10000 || d.Instructions is null || d.Instructions.Length > 20000 ||
            d.Prompt is null || d.Prompt.Length > 100000 || d.UseGuidance is null || d.UseGuidance.Length > 20000 || d.Images is null)
            throw new WorkspaceStoreException("Invalid reel recipe. Keep the prompt under 100000 characters and guidance under 20000.");
        if (d.KeyframeReels?.Any(r => r.EffectiveVisuals is not (ReelVisuals.Keyframes or ReelVisuals.RefMod or ReelVisuals.FullReel) || r.UseSoundtrack || r.Speaker is not null) == true)
            throw new WorkspaceStoreException("Choose keyframes, RefMod or full reel for visual references. Choose voices separately.");
        // An unfinished reel can be unnamed; the legacy shot validator always requires a title.
        var inputs = Inputs(d);
        if (!ready && string.IsNullOrWhiteSpace(inputs.Title)) inputs.Title = "Reference reel";
        H3Policy.Validate(inputs, ready, requireScene: false);
        if (d.Duration < 2) throw new WorkspaceStoreException("Reference reels need at least 2 seconds so they can be reused as video inputs.");
        if (ready && (string.IsNullOrWhiteSpace(d.Name) || !HasVisualReferences(d) ||
            d.VoiceMode == ReelVoiceMode.ExistingRecording && d.Voice is null))
            throw new WorkspaceStoreException("Name the reel, select at least one visual reference, and choose a recording when using an existing voice.");
        if (ready) ValidatePair(new(d.Prompt, d.UseGuidance), d);
    }
    public static void ValidatePair(ReelPromptPair pair, ReferenceReelDraft d)
    {
        ValidateProfile(d);
        if (string.IsNullOrWhiteSpace(pair.UseGuidance) || pair.UseGuidance.Length > 20000)
            throw new WorkspaceStoreException("Add guidance explaining how to use this reel.");
        ProductionPolicy.ValidatePrompt(pair.Prompt, Inputs(d), allowCuts: true);
        var detailed = pair.Prompt.Split("detailed_description:")[1].Split("overall_soundscape:")[0];
        var views = Regex.Matches(detailed, @"\[Shot (\d+)\]").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        if (views.Length == 0 || !views.SequenceEqual(Enumerable.Range(1, views.Length)))
            throw new WorkspaceStoreException("Number the reel's views consecutively from [Shot 1] in detailed_description.");
    }
    private static ReelPromptPair PresetCore(ReferenceReelDraft d, AssetLibrary? library)
    {
        if (IsEnvironment(d)) return EnvironmentPreset(d, library);
        if (IsProp(d)) return PropPreset(d, library);
        if (IsCharacterCapture(d)) return CharacterCapturePreset(d, library);
        if (d.Framing == ReelFraming.Custom) return new("", "");
        if (!HasVisualReferences(d)) throw new WorkspaceStoreException("Select visual references before building the preset.");
        var seconds = H3Policy.Seconds(d.Duration).ToString("0.###", CultureInfo.InvariantCulture);
        var guidanceByImage = PictureGuidance(d, library);
        var refs = string.Join("\n", guidanceByImage.Select((g, n) => $"<Picture {n + 1}>: {(string.IsNullOrWhiteSpace(g) ? "Use the character's visible appearance." : g)} Preserve only the indicated appearance; exclude the source pose, background and lighting.")) + VideoDefinitions(d);
        var audio = d.VoiceMode == ReelVoiceMode.ExistingRecording ? $"\n<Audio 1> supplies voice identity for {d.Speaker} (S1), excluding its recorded words and background sound." : "";
        var views = Views(d.Framing);
        var speech = d.VoiceMode == ReelVoiceMode.Silent ? "No speech or vocalization." :
            $"The character speaks once, beginning near the start and continuing naturally across any cuts without restarting. {d.Speaker} (S1) says: <d>[{d.Language}] {d.Line}</d>";
        if (d.VoiceMode != ReelVoiceMode.Silent && !string.IsNullOrWhiteSpace(d.VoiceDescription))
            speech += d.VoiceMode == ReelVoiceMode.NewVoice ? $"\nIntended voice: {d.VoiceDescription}" :
                $"\nPreserve the voice identity from <Audio 1>. Delivery direction: {d.VoiceDescription}";
        var prompt = $"subject_definitions:\n<Subject 1> is {d.Speaker}, the same single character throughout the reel.\n{refs}{audio}\n\nsummary:\nA {d.Aspect} character reference reel lasting {seconds} seconds, showing a consistent appearance with readable angles and restrained movement.\n\nretention_analysis:\n<Subject 1>: fully_preserved. Preserve identity, physical proportions, clothing, colors and accessories from the selected visual references.\n\ndetailed_description:\nNeutral studio background with soft even lighting. Exactly {seconds} seconds total. No extra characters, props or visible text.\n\n{views}\n\n{speech}\n\noverall_soundscape:\n{(d.VoiceMode == ReelVoiceMode.Silent ? "Silent reference, with no audible soundtrack." : "Only a clean, natural speaking voice with consistent timbre, faint breathing and minimal clothing sound. No music or distracting ambience.")}\n\nnon_diegetic_music:\nNo non-diegetic music.";
        var guidance = $"Reference of {d.Speaker}: {Label(d.Framing).ToLowerInvariant()}, intended to establish facial identity, body proportions and outfit detail. " +
            (d.VoiceMode == ReelVoiceMode.Silent ? "Visual reference only. " : "When its soundtrack is enabled, use it for voice identity and natural timbre. ") +
            "Do not copy the reference background, lighting, framing, camera motion, cuts, actions, timing or spoken words. Follow the target shot's action and dialogue.";
        if (d.Framing == ReelFraming.SideRearFace)
            guidance += " Intended views show the profile silhouette, rear appearance, arms and clothing with hands visible, and a frontal face close-up. These intended details are not verified output.";
        if (d.VoiceMode != ReelVoiceMode.Silent && !string.IsNullOrWhiteSpace(d.VoiceDescription))
            guidance += $"\nIntended voice direction (not verified output): {d.VoiceDescription}";
        var details = string.Join("\n", guidanceByImage.Concat(VideoGuidance(d)).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct());
        if (details.Length > 0) guidance += "\nIntended appearance details from the source visual references:\n" + details;
        var pair = new ReelPromptPair(prompt, guidance); ValidatePair(pair, d); return pair;
    }
    private const string SoundSectionInstructions = "Always complete non_diegetic_music with text: write 'No non-diegetic music.' when no music is intended. " +
        "For a silent reel, write 'Silent reference. No speech, ambient sound or audible soundtrack.' under overall_soundscape. Do not leave either sound section empty. ";
    private static List<ChatMessage> CharacterMessages(ReelCompositionRequest request, IReadOnlyList<byte[]> images, IReadOnlyList<RefModInspectionFrame> modFrames)
    {
        var rules = "Compose a character reference reel. Return ordinary JSON with exactly prompt and useGuidance strings. " +
            "The prompt must have these six headings with colons, in order: subject_definitions, summary, retention_analysis, detailed_description, overall_soundscape, non_diegetic_music. " + SoundSectionInstructions +
            "Define the character as <Subject 1> with the exact speaker name. Reference every selected <Picture N>, <Video N> and optional <Audio 1>. " +
            "Number views consecutively [Shot 1], [Shot 2], etc in detailed_description; multiple cuts are allowed. State the supplied generated duration exactly. " +
            "Respect explicit instructions and image roles; use the selected framing preset as a starting point. Custom means no prescribed framing: design the views from the author's instructions. Inspect only the supplied still images. Never claim video or audio inspection. " +
            "For speaking modes keep the exact language and line once as <d>[Language] line</d>, preceded on the same line by the character's exact name and (S1). " +
            "Existing audio must be mapped as <Audio 1> ... CHARACTER (S1) in subject_definitions. New voice has no audio input. Silent mode has no dialogue. " +
            "An optional voiceDescription is author direction: for NewVoice use it to describe the desired voice and delivery; for ExistingRecording preserve the recording's voice identity and use it only for delivery. Ignore voiceDescription in Silent mode. " +
            "Incorporate that direction into the generation prompt and matching intended voice guidance without changing or adding dialogue. " +
            "Use guidance must match the planned views and emphasized details, and be explicit that these are intended uses, not verified output. " +
            "Keep it independent of reference numbering. Separate reference identity/voice from target action: never carry source words, background, pose, camera or cuts into later shots. " +
            "Keep speech continuous across cuts, not repeated. Return both fields even when revising only one.";
        if (IsCharacterCapture(request.Draft))
        {
            ValidateComposition(request.Draft);
            rules += "\n\n" + (HasCharacterCaptureOptions(request.Draft) ? CharacterCaptureInstructions : CharacterStudyInstructions(request.Draft));
        }
        if (IsVoiceReference(request.Draft))
            rules += "\n\n" + CharacterReferenceInstructions + VoiceReferenceInstructions;
        rules += "\n\n" + ReelSpeech.CompositionInstructions(request.Draft);
        var context = request.Draft.VoiceMode == ReelVoiceMode.Silent
            ? request with { Draft = request.Draft with { VoiceDescription = null, Speech = null } } : request;
        List<AIContent> content = [new TextContent(JsonSerializer.Serialize(new { request = context, pictures = PictureGuidance(request.Draft, null),
            videoReferences = VideoContext(request.Draft, images.Count, modFrames), presetViews = Views(request.Draft), generatedSeconds = H3Policy.Seconds(request.Draft.Duration) }, AtomicJsonFile.Options))];
        foreach (var image in images) content.Add(new DataContent(image, "image/png"));
        return [new(ChatRole.System, rules), new(ChatRole.User, content)];
    }
    public static ReelPromptPair Parse(string raw, ReferenceReelDraft draft)
    {
        var pair = ParsePair(raw);
        ValidatePair(pair, draft);
        return pair;
    }
    // Keep readable text available for inspection even when production validation fails.
    public static ReelPromptPair ParsePair(string raw)
    {
        try
        {
            var text = raw.Trim(); if (text.StartsWith("```")) text = text[(text.IndexOf('\n') + 1)..text.LastIndexOf("```")];
            var pair = JsonSerializer.Deserialize<ReelPromptPair>(text, AtomicJsonFile.Options) ?? throw new JsonException();
            if (pair.Prompt is null || pair.UseGuidance is null || pair.Prompt.Length > 100000 || pair.UseGuidance.Length > 20000)
                throw new JsonException();
            return pair;
        }
        catch (Exception e) when (e is JsonException or ArgumentOutOfRangeException or NullReferenceException)
        { throw new WorkspaceStoreException("Return a complete JSON pair with prompt and useGuidance. Inspect the response and retry.", e); }
    }
}
