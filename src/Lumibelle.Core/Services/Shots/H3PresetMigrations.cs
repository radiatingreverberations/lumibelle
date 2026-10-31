using lumibelle.Models;

namespace lumibelle.Services.Shots;

public static class H3PresetMigrations
{
    private static string Notice(string key) => $"{H3Presets.Label(key)} was removed. This setup has been reset to Standard.";
    private static string Key(string? preset, bool turbo, int steps) => preset ?? (turbo ? steps == 8 ? "turbo8" : "turbo4" : "standard");

    public static bool Apply(Shot shot)
    {
        var key = H3Presets.Key(shot);
        if (!H3Presets.Retired(key)) return false;
        shot.GenerationPresetMigration = Notice(key);
        shot.GenerationPreset = "standard"; shot.Turbo = false; shot.TurboSteps = 4;
        return true;
    }
    public static bool Apply(GenerationSettings settings)
    {
        var key = Key(settings.GenerationPreset, settings.Turbo, settings.TurboSteps);
        if (!H3Presets.Retired(key)) return false;
        settings.GenerationPresetMigration = Notice(key);
        settings.GenerationPreset = "standard"; settings.Turbo = false; settings.TurboSteps = 4;
        return true;
    }
    public static bool Apply(ReferenceReelDraft draft)
    {
        if (draft.GenerationSetup is { } setup) Apply(setup.Settings);
        var key = Key(draft.GenerationPreset, draft.Turbo, draft.TurboSteps);
        if (!H3Presets.Retired(key)) return false;
        draft.GenerationPresetMigration = Notice(key);
        draft.GenerationPreset = "standard"; draft.Turbo = false; draft.TurboSteps = 4;
        return true;
    }
}
