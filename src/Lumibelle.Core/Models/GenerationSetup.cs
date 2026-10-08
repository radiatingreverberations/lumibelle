namespace lumibelle.Models;

// Global, reusable generation choices. Never contains story, prompts or references.
public sealed record GenerationSettings
{
    public long? Seed { get; set; }
    public int TakeCount { get; set; } = 1;
    public bool NativeResolution { get; set; }
    public VideoResolution? Resolution { get; set; }
    public bool UpscalePreview { get; set; }
    public string? GenerationPreset { get; set; }
    public bool SaveLosslessFrames { get; set; }
    public bool SaveLatents { get; set; }
    public bool Turbo { get; set; }
    public int TurboSteps { get; set; } = 4;
    public IReadOnlyList<LoraSelection>? Loras { get; set; }

    public static GenerationSettings From(ProductionComposition c) => ShotCopy.Of(new GenerationSettings {
        Seed = c.Seed, TakeCount = c.TakeCount,
        NativeResolution = c.Shot.NativeResolution, Resolution = c.Shot.Resolution, UpscalePreview = c.Shot.UpscalePreview,
        GenerationPreset = c.Shot.GenerationPreset, SaveLosslessFrames = c.Shot.SaveLosslessFrames, SaveLatents = c.Shot.SaveLatents,
        Turbo = c.Shot.Turbo, TurboSteps = c.Shot.TurboSteps, Loras = c.Shot.Loras
    });
    public void Apply(ProductionComposition c)
    {
        var s = ShotCopy.Of(this);
        c.Seed = s.Seed; c.TakeCount = s.TakeCount;
        c.Shot.NativeResolution = s.NativeResolution; c.Shot.Resolution = s.Resolution;
        c.Shot.UpscalePreview = s.UpscalePreview; c.Shot.GenerationPreset = s.GenerationPreset;
        c.Shot.SaveLosslessFrames = s.SaveLosslessFrames; c.Shot.SaveLatents = s.SaveLatents; c.Shot.Turbo = s.Turbo;
        c.Shot.TurboSteps = s.TurboSteps; c.Shot.Loras = s.Loras;
    }

    // Quick output choices belong to this composition, never to the shared preset.
    public static GenerationSettings ForPreset(ProductionComposition c, GenerationSettings defaults)
    {
        var settings = From(c);
        if (c.OutputOverrides?.TakeCount is not null) settings.TakeCount = defaults.TakeCount;
        if (c.OutputOverrides?.Resolution is not null) {
            settings.Resolution = defaults.Resolution;
            settings.NativeResolution = defaults.NativeResolution;
            settings.UpscalePreview = defaults.UpscalePreview;
        }
        return settings;
    }
}

public sealed record GenerationOutputOverrides
{
    public int? TakeCount { get; set; }
    public VideoResolution? Resolution { get; set; }
    public bool UpscalePreview { get; set; }

    public void Apply(ProductionComposition c)
    {
        if (TakeCount is { } count) c.TakeCount = count;
        if (Resolution is { } resolution) {
            c.Shot.Resolution = resolution is VideoResolution.Preview or VideoResolution.Native ? null : resolution;
            c.Shot.NativeResolution = resolution == VideoResolution.Native;
            c.Shot.UpscalePreview = UpscalePreview;
        }
    }
}

public sealed record GenerationSetup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default setup";
    public long Version { get; set; }
    public bool Archived { get; set; }
    public GenerationSettings Settings { get; set; } = new();
    public void Apply(ProductionComposition c)
    {
        Settings.Apply(c); c.Name = Name; c.GenerationSetupId = Id; c.GenerationSetupVersion = Version; c.GenerationSetupArchived = Archived;
        c.OutputOverrides?.Apply(c);
    }
}

public sealed record GenerationSetupLibrary
{
    public int SchemaVersion { get; set; } = 1;
    public Guid? SelectedId { get; set; }
    public List<GenerationSetup> Setups { get; set; } = [];
    public List<ImportedGenerationSetup> Imports { get; set; } = [];
}
public sealed record ImportedGenerationSetup(Guid ProjectId, Guid CompositionId, Guid SetupId, string SourceName);
