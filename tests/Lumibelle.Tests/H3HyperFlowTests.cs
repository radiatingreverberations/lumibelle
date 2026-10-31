using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Theory]
    [InlineData("spectrum")][InlineData("turbo8")][InlineData("turbo4-075")][InlineData("hyperflow")]
    public void RemovedPresetsRemainHistoricalButCannotBeSelectedCapturedOrExecuted(string key)
    {
        var historical = PresetSnapshot(key);
        H3Presets.Validate(ShotCopy.Of(historical));
        var original = JsonSerializer.Serialize(historical, AtomicJsonFile.Options);
        Assert.DoesNotContain(key, H3Presets.Offered);
        Assert.DoesNotContain(key, H3Presets.Choices(key));
        Assert.Empty(H3Requirements.ForPreset(key));
        Assert.Throws<WorkspaceStoreException>(() => H3Presets.Capture(historical.Shot, historical.Settings));
        Assert.Throws<WorkspaceStoreException>(() => ComfyH3Video.BuildWorkflow(historical, 42, "removed", []));
        var editable = historical.Shot.Copy();
        Assert.True(H3PresetMigrations.Apply(editable));
        Assert.Equal("standard", H3Presets.Key(editable)); Assert.False(editable.Turbo); Assert.Equal(4, editable.TurboSteps);
        Assert.Contains("reset to Standard", editable.GenerationPresetMigration);
        Assert.False(H3PresetMigrations.Apply(editable));
        Assert.Equal(original, JsonSerializer.Serialize(historical, AtomicJsonFile.Options));
    }

    [Theory]
    [InlineData("spectrum")][InlineData("turbo8")][InlineData("turbo4-075")][InlineData("hyperflow")]
    public async Task RemovedPresetGlobalLibrariesAndEditableShotsLoadAsStandardWithNotice(string key)
    {
        var paths = new ApplicationPaths(Path.Combine(_root, "removed-" + key)); Directory.CreateDirectory(paths.Data);
        var setup = new GenerationSetup { Version = 1, Name = "Saved recipe", Settings = new() { GenerationPreset = key, Resolution = VideoResolution.Quick, Seed = 42 } };
        await AtomicJsonFile.WriteAsync(Path.Combine(paths.Data, "generation-setups.json"), new GenerationSetupLibrary { Setups = [setup] }, _ct);
        var store = new FileGenerationSetupStore(paths);
        var migrated = Assert.Single((await store.LoadAsync(_ct)).Setups);
        Assert.Equal("standard", migrated.Settings.GenerationPreset); Assert.Equal(42, migrated.Settings.Seed);
        Assert.Equal(VideoResolution.Quick, migrated.Settings.Resolution); Assert.NotNull(migrated.Settings.GenerationPresetMigration);
        var composition = new ProductionComposition(); migrated.Apply(composition);
        Assert.Equal(migrated.Settings.GenerationPresetMigration, composition.Shot.GenerationPresetMigration);
        var f = Fixture(); var shot = Ready(); shot.GenerationPreset = key;
        var dir = await f.Files.DirectoryAsync(f.Project.Id, _ct);
        await AtomicJsonFile.WriteAsync(Path.Combine(dir, "shots.json"), new ShotDocument { ProjectId = f.Project.Id, Shots = [shot] }, _ct);
        var document = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal("standard", Assert.Single(document.Shots).GenerationPreset);
        Assert.NotNull(document.Shots[0].GenerationPresetMigration);
        var reel = new ReferenceReelDraft { GenerationPreset = key, GenerationSetup = setup };
        Assert.True(H3PresetMigrations.Apply(reel));
        Assert.Equal("standard", reel.GenerationPreset); Assert.Equal("standard", reel.GenerationSetup.Settings.GenerationPreset);
    }
}
