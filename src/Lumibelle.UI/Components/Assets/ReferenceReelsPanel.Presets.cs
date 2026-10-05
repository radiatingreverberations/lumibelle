using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Assets;

public partial class ReferenceReelsPanel
{
    [Inject] public IGenerationSetupStore GenerationSetups { get; set; } = null!;
    private GenerationSetupLibrary _globalSetups = new();
    private readonly SemaphoreSlim _presetGate = new(1);
    private bool _presetBusy, _showArchivedPresets, _presetsReady;
    private GenerationSetup? ActivePreset => _draft?.GenerationSetup;
    private string PresetName => ActivePreset is { Archived: true } preset ? preset.Name + " · Archived" : ActivePreset?.Name ?? "Custom settings";
    private GenerationSettings PresetSettings => ActivePreset?.Settings ?? ReelGenerationSetups.Settings(_draft!);
    private VideoResolution PresetResolution => PresetSettings.Resolution ?? (PresetSettings.NativeResolution ? VideoResolution.Native : VideoResolution.Preview);
    private int ReelTakeCount => _draft is null ? 1 : ReelGenerationSetups.TakeCount(_draft);
    private string ReelResolutionKey => ReelGenerationSetups.UpscalePreview(_draft!) ? "preview-upscale" : VideoResolutions.Key(VideoResolutions.Selected(_draft!));

    private async Task LoadPresets()
    {
        _globalSetups = await GenerationSetups.LoadAsync(_lifetime.Token);
        if (_globalSetups.Setups.Count > 0) return;
        try { _globalSetups = await GenerationSetups.SaveAsync(new GenerationSetup(), 0, _lifetime.Token); }
        catch (WorkspaceStoreException) {
            _globalSetups = await GenerationSetups.LoadAsync(_lifetime.Token);
            if (_globalSetups.Setups.Count == 0) throw;
        }
    }

    private async Task RefreshPresets()
    {
        await LoadPresets();
        if (_draft?.GenerationSetup is not { } saved) return;
        if (_globalSetups.Setups.FirstOrDefault(s => s.Id == saved.Id) is not { } current) return;
        if (current.Version != saved.Version) {
            ReelGenerationSetups.Apply(_draft, current);
            await Save();
        }
    }

    private async Task PresetMenuChanged(bool open)
    {
        if (!open) return;
        try { await LoadPresets(); }
        catch (Exception e) { _error = e.Message; }
    }
    private Task ChangeNamedPreset(ChangeEventArgs e) => Guid.TryParse(e.Value?.ToString(), out var id) ? SelectPreset(id) : Task.CompletedTask;
    private Task SelectPreset(Guid id) => RunChoice(async () => {
        await Flush();
        await LoadPresets();
        var preset = _globalSetups.Setups.Single(s => s.Id == id);
        if (!preset.Archived) _globalSetups = await GenerationSetups.SelectAsync(id, _lifetime.Token);
        ReelGenerationSetups.Apply(_draft!, preset);
        await Save();
    });

    private async Task EditPreset(Action<GenerationSetup> edit)
    {
        await _presetGate.WaitAsync(_lifetime.Token);
        _presetBusy = true;
        try {
            var preset = ActivePreset is { } active ? ShotCopy.Of(active) : new GenerationSetup {
                Name = FileGenerationSetupStore.UniqueName(_globalSetups, "Reel setup"), Settings = PresetSettings
            };
            edit(preset);
            _globalSetups = await GenerationSetups.SaveAsync(preset, preset.Version, _lifetime.Token);
            if (preset.Version == 0) _globalSetups = await GenerationSetups.SelectAsync(preset.Id, _lifetime.Token);
            ReelGenerationSetups.Apply(_draft!, _globalSetups.Setups.Single(s => s.Id == preset.Id));
            await Save(); _error = null;
        }
        catch (Exception e) { _error = e.Message; }
        finally { _presetBusy = false; _presetGate.Release(); }
    }

    private Task CreatePreset(bool duplicate) => Run(async () => {
        await Flush(); await LoadPresets();
        var preset = new GenerationSetup {
            Name = FileGenerationSetupStore.UniqueName(_globalSetups, duplicate ? (ActivePreset?.Name ?? "Reel setup") + " (copy)" : "New setup"),
            Settings = duplicate ? ShotCopy.Of(PresetSettings) : new()
        };
        _globalSetups = await GenerationSetups.SaveAsync(preset, 0, _lifetime.Token);
        _globalSetups = await GenerationSetups.SelectAsync(preset.Id, _lifetime.Token);
        ReelGenerationSetups.Apply(_draft!, _globalSetups.Setups.Single(s => s.Id == preset.Id));
        await Save();
    });

    private Task RenamePreset(ChangeEventArgs e) => EditPreset(p => p.Name = e.Value?.ToString() ?? "");
    private Task ArchivePreset() => EditPreset(p => p.Archived = !p.Archived);
    private Task ChangePresetTakes(ChangeEventArgs e) => int.TryParse(e.Value?.ToString(), out var count)
        ? EditPreset(p => p.Settings.TakeCount = count) : Task.CompletedTask;
    private Task ChangePresetSeed(ChangeEventArgs e) => EditPreset(p => p.Settings.Seed = string.IsNullOrWhiteSpace(e.Value?.ToString())
        ? null : long.TryParse(e.Value?.ToString(), out var seed) && seed >= 0 ? seed : throw new WorkspaceStoreException("Use a non-negative seed, or leave it blank for fresh seeds."));
    private Task ChangePresetResolution(ChangeEventArgs e)
    {
        var upscale = e.Value?.ToString() == "preview-upscale";
        if (!VideoResolutions.TryParse(upscale ? "preview" : e.Value?.ToString(), out var resolution)) return Task.CompletedTask;
        return EditPreset(p => {
            p.Settings.Resolution = resolution is VideoResolution.Preview or VideoResolution.Native ? null : resolution;
            p.Settings.NativeResolution = resolution == VideoResolution.Native; p.Settings.UpscalePreview = upscale;
        });
    }
    private async Task ChangeReelTakes(ChangeEventArgs e)
    {
        if (_draft is null || !int.TryParse(e.Value?.ToString(), out var count) || count is < 1 or > 4) return;
        _draft.OutputOverrides ??= new(); _draft.OutputOverrides.TakeCount = count; await Changed();
    }
}
