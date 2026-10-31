using System.Text.RegularExpressions;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class MotionReferencePolicy
{
    public const string Profile = "h3-motion-reference-v1";
    public const string DefaultInstructions = "Use this clip as a guide to blocking, action order, timing, character positioning and camera movement. " +
        "Keep identity, clothing, props and style from the other references and the authored scene. Generate the authored dialogue and sound anew, " +
        "with synchronized lip movements. Do not copy the draft's soundtrack or visual defects.";

    public static string? Issue(ShotTake take) => take.Fps != 24 || take.FrameCount / take.Fps is < 2 or > ReferenceVideos.MaximumSeconds
        ? "Choose a visible clip lasting 2–15 seconds. Trim a longer take first."
        : null;
    public static string? Issue(AssetReferenceReel reel) => reel.Generation is null
        ? "This reel has no saved generation prompt or references."
        : reel.Media.Fps != 24 || reel.Media.Frames / reel.Media.Fps is < 2 or > ReferenceVideos.MaximumSeconds
            ? "Choose a reference reel lasting 2–15 seconds at 24 fps." : null;

    public static IReadOnlyList<VideoResolution> Resolutions(string aspect, int width, int height) => VideoResolutions.Choices
        .Where(r => { var size = VideoResolutions.Size(aspect, r); return size.Width >= width && size.Height >= height; }).ToArray();
    public static VideoResolution DefaultResolution(string aspect, int width, int height, bool reel)
    {
        var choices = Resolutions(aspect, width, height);
        if (choices.Count == 0) throw new WorkspaceStoreException("The source clip is larger than the available output presets.");
        var preferred = reel ? VideoResolution.Detail : VideoResolution.Native;
        return choices.Contains(preferred) ? preferred : choices.FirstOrDefault(r => (int)r >= (int)preferred, choices.Last());
    }

    // The same moment on another timeline: a source clip's frame on its refined final, first and last frames kept.
    public static int MapFrame(int frame, int sourceFrames, int outputFrames) =>
        sourceFrames <= 1 || outputFrames <= 1 ? 0 : Math.Clamp((int)Math.Round(frame * (outputFrames - 1d) / (sourceFrames - 1)), 0, outputFrames - 1);

    public static int GuideNumber(MotionReferenceDraft draft) =>
        ResolvedReferences.For(draft.Shot).Videos.FirstOrDefault(v => v.Reel.Id == draft.Source.BindingId)?.Number ?? 0;

    // Why these reviewed options cannot be queued, or null. Shared settings that do not fit one row surface here.
    public static string? Issue(MotionReferenceOptions options)
    {
        var draft = options.Draft; var media = draft.Source.Media;
        if (string.IsNullOrWhiteSpace(draft.Shot.Title) && draft.Reel is not null) return "Name the new reel.";
        if (!Resolutions(draft.Shot.Aspect, media.Width, media.Height).Contains(options.Resolution))
            return Resolutions(draft.Shot.Aspect, media.Width, media.Height).Count == 0 ? "The source clip is larger than the available output presets."
                : $"{VideoResolutions.Label(options.Resolution)} is smaller than this {media.Width} × {media.Height} source. Choose a larger size.";
        if (GuideNumber(draft) == 0) return "Keep the motion-reference clip selected.";
        try {
            ReferenceVideos.Validate(draft.Shot);
            _ = Render(options.BasePrompt, options.Instructions, GuideNumber(draft), draft.Reel?.Recipe.CapturedVisualStyle);
            if (draft.Reel is not null) _ = ReelRecipe(options);
        }
        catch (Exception e) when (e is WorkspaceStoreException or InvalidOperationException) { return e.Message; }
        return null;
    }

    public static string Render(string basePrompt, string instructions, int number, VisualStyle? style = null)
    {
        ProductionPolicy.ValidateGenerationPrompt(basePrompt);
        if (instructions.Length > 20000) throw new WorkspaceStoreException("Motion-reference instructions are too long.");
        if (string.IsNullOrWhiteSpace(instructions)) return VisualStyles.Apply(basePrompt, style);
        if (number < 1) throw new WorkspaceStoreException("Keep the motion-reference clip selected, or remove its instructions.");
        // Rebuild from the separately retained base every time. Do not append to a rendered prompt.
        var section = Regex.Match(basePrompt, @"(?m)^\s*retention_analysis:\s*");
        if (!section.Success) throw new WorkspaceStoreException("The prompt needs a retention_analysis section.");
        var rendered = basePrompt.Insert(section.Index + section.Length, $"Motion reference <Video {number}>: {instructions.Trim()}\n");
        ProductionPolicy.ValidateGenerationPrompt(rendered);
        return VisualStyles.Apply(rendered, style);
    }

    public static void Validate(VideoSnapshot snapshot)
    {
        if (snapshot.MotionReference is not { } source) return;
        var binding = snapshot.Shot.Videos.SingleOrDefault(v => v.Id == source.BindingId);
        if ((snapshot.Reel is null ? source.TakeId == Guid.Empty || source.ReelId is not null : source.TakeId != Guid.Empty || source.ReelId is null || source.ReelId == Guid.Empty) ||
            source.BindingId == Guid.Empty || source.Media is null || source.VisibleFrames != source.Media.Frames || source.Fps != source.Media.Fps || source.Fps != 24 ||
            snapshot.Motion is not null || binding is null || binding.Media != source.Media ||
            binding.UseSoundtrack || binding.Speaker is not null || binding.EffectiveVisuals != ReelVisuals.FullReel)
            throw new WorkspaceStoreException("Invalid captured motion reference.");
        var number = ResolvedReferences.For(snapshot.Shot).Videos.Single(v => v.Reel.Id == source.BindingId).Number;
        if (snapshot.Prompt != Render(source.BasePrompt, source.Instructions, number, snapshot.Reel?.Recipe.CapturedVisualStyle))
            throw new WorkspaceStoreException("The submitted motion-reference prompt does not match its reviewed instructions.");
    }

    public static ReferenceReelDraft ReelRecipe(MotionReferenceOptions options, Guid? requestId = null)
    {
        var context = options.Draft.Reel ?? throw new WorkspaceStoreException("The destination reel is unavailable.");
        var shot = options.Draft.Shot;
        if (shot.Voices.Count > 1) throw new WorkspaceStoreException("Reference reels support one voice recording. Remove additional voice recordings.");
        var recipe = context.Recipe.Copy() with {
            Id = requestId ?? shot.Id, Revision = 0, PendingJobId = null, ResolvedJobs = [], CheckedInputs = null,
            GenerationSetup = null, OutputOverrides = null, GenerationPreset = "standard", Turbo = false, TurboSteps = 4,
            Name = shot.Title, Duration = shot.Duration!.Value, Aspect = shot.Aspect,
            Images = ShotCopy.Of(shot.Images), KeyframeReels = ShotCopy.Of(shot.Videos), Voice = ShotCopy.Of(shot.Voices.SingleOrDefault()),
            SaveLatents = options.SaveLatents, SaveLosslessFrames = options.SaveLosslessFrames
        };
        if (ReferenceReels.IsCameraReel(recipe) || recipe.VoiceMode == ReelVoiceMode.Silent) {
            if (recipe.Voice is not null) throw new WorkspaceStoreException("This reel is silent. Remove voice recordings from its references.");
        }
        else recipe.VoiceMode = recipe.Voice is null ? ReelVoiceMode.NewVoice : ReelVoiceMode.ExistingRecording;
        VideoResolutions.Select(recipe, options.Resolution);
        var number = ResolvedReferences.For(shot).Videos.Single(v => v.Reel.Id == options.Draft.Source.BindingId).Number;
        recipe.Prompt = Render(options.BasePrompt, options.Instructions, number);
        ReferenceReels.Validate(recipe, true);
        return recipe;
    }
}
