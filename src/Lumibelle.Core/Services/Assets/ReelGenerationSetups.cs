using lumibelle.Models;
using lumibelle.Services.Shots;

namespace lumibelle.Services.Assets;

public static class ReelGenerationSetups
{
    public static int TakeCount(ReferenceReelDraft draft) => draft.OutputOverrides?.TakeCount ?? draft.GenerationSetup?.Settings.TakeCount ?? 1;
    public static bool UpscalePreview(ReferenceReelDraft draft) => draft.OutputOverrides?.Resolution is not null
        ? draft.OutputOverrides.UpscalePreview : draft.GenerationSetup?.Settings.UpscalePreview ?? false;

    public static GenerationSettings Settings(ReferenceReelDraft draft) => ShotCopy.Of(new GenerationSettings {
        Seed = draft.GenerationSetup?.Settings.Seed, TakeCount = TakeCount(draft),
        Resolution = draft.Resolution, NativeResolution = draft.NativeResolution, UpscalePreview = UpscalePreview(draft),
        GenerationPreset = draft.GenerationPreset, GenerationPresetMigration = draft.GenerationPresetMigration, Turbo = draft.Turbo, TurboSteps = draft.TurboSteps,
        SaveLosslessFrames = draft.SaveLosslessFrames ?? true, SaveLatents = draft.SaveLatents ?? false, Loras = draft.Loras
    });

    public static void Apply(ReferenceReelDraft draft, GenerationSetup preset)
    {
        var captured = ShotCopy.Of(preset);
        H3PresetMigrations.Apply(captured.Settings);
        var settings = captured.Settings;
        draft.GenerationSetup = captured;
        draft.GenerationPreset = settings.GenerationPreset;
        draft.GenerationPresetMigration = settings.GenerationPresetMigration;
        draft.Turbo = settings.Turbo; draft.TurboSteps = settings.TurboSteps;
        draft.SaveLosslessFrames = settings.SaveLosslessFrames;
        draft.SaveLatents = settings.SaveLatents;
        draft.Loras = ShotCopy.Of(settings.Loras);
        VideoResolutions.Select(draft, draft.OutputOverrides?.Resolution ?? settings.Resolution ??
            (settings.NativeResolution ? VideoResolution.Native : VideoResolution.Preview));
    }
}
