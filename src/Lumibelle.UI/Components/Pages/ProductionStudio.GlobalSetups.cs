using lumibelle.Models;
using lumibelle.Services.Production;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private GenerationSetupLibrary _globalSetups = new();
    private bool _setupBusy;
    private GenerationSetup? ActiveGlobalSetup => _globalSetups.Setups.FirstOrDefault(s => s.Id == Current?.GenerationSetupId);

    private async Task ApplySelectedGlobalSetup()
    {
        if (ScenesMode) return;
        _globalSetups = await GenerationSetups.LoadAsync(_lifetime.Token);
        var selected = _globalSetups.Setups.FirstOrDefault(s => s.Id == _globalSetups.SelectedId && !s.Archived)
            ?? _globalSetups.Setups.FirstOrDefault(s => s.Id == Current?.GenerationSetupId && !s.Archived)
            ?? _globalSetups.Setups.FirstOrDefault(s => !s.Archived);
        if (selected is null || _selected is null) return;
        _globalSetups = await GenerationSetups.SelectAsync(selected.Id, _lifetime.Token);
        await BindGlobalSetup(selected);
    }

    private async Task SelectGlobalSetup(Guid id)
    {
        if (_setupBusy) return;
        _setupBusy = true;
        try {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (!await Save()) return;
            _globalSetups = await GenerationSetups.LoadAsync(_lifetime.Token);
            var preset = _globalSetups.Setups.Single(s => s.Id == id);
            if (!preset.Archived) _globalSetups = await GenerationSetups.SelectAsync(id, _lifetime.Token);
            await BindGlobalSetup(preset);
            _undo.Clear(); await RefreshCompositionResult();
        } catch (Exception e) { _error = e.Message; }
        finally { _setupBusy = false; }
    }

    private async Task BindGlobalSetup(GenerationSetup preset)
    {
        if (SourceShot is not { } source) return;
        // Retain shot adapters for historical job/take identities, while the preset
        // definition and the user's selection are shared throughout the application.
        _production = await Production.LoadAsync(Id, _lifetime.Token);
        var c = _production.Compositions.FirstOrDefault(c => c.ShotId == source.Id && c.GenerationSetupId == preset.Id && !c.Archived);
        if (c is null) {
            c = new() { ShotId = source.Id, Shot = ProductionPolicy.CoverageCopy(source), SourceFingerprint = ProductionPolicy.SourceFingerprint(source) };
            c.Shot.EnvironmentAssetId = Current?.Shot.EnvironmentAssetId;
            preset.Apply(c);
            _production = await Production.SaveAsync(Id, c, 0, _lifetime.Token);
        }
        _compositionId = c.Id; _savedComposition = Current?.Copy(); ValidateTakeFilter();
    }

    private Task NewComposition() => CreateGlobalSetup(false);
    private Task DuplicateComposition() => CreateGlobalSetup(true);
    private async Task CreateGlobalSetup(bool duplicate)
    {
        if (_setupBusy) return;
        _setupBusy = true;
        try {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (Current is null || !await Save()) return;
            _globalSetups = await GenerationSetups.LoadAsync(_lifetime.Token);
            var preset = new GenerationSetup {
                Name = FileGenerationSetupStore.UniqueName(_globalSetups, duplicate ? Current.Name + " (copy)" : "New setup"),
                Settings = duplicate ? GenerationSettings.From(Current) : new()
            };
            _globalSetups = await GenerationSetups.SaveAsync(preset, 0, _lifetime.Token);
            _globalSetups = await GenerationSetups.SelectAsync(preset.Id, _lifetime.Token);
            await BindGlobalSetup(_globalSetups.Setups.Single(s => s.Id == preset.Id));
            _undo.Clear(); await RefreshCompositionResult();
        } catch (Exception e) { _error = e.Message; }
        finally { _setupBusy = false; }
    }
    private async Task ArchiveComposition()
    {
        if (_setupBusy) return;
        _setupBusy = true;
        try {
            if (!await Save() || ActiveGlobalSetup is not { } preset) return;
            _globalSetups = await GenerationSetups.SaveAsync(preset with { Archived = !preset.Archived }, preset.Version, _lifetime.Token);
            var latest = _globalSetups.Setups.Single(s => s.Id == preset.Id);
            if (Current is { } c) { latest.Apply(c); _savedComposition = c.Copy(); }
            _showArchived = true;
            if (!latest.Archived) _globalSetups = await GenerationSetups.SelectAsync(latest.Id, _lifetime.Token);
        } catch (Exception e) { _error = e.Message; }
        finally { _setupBusy = false; }
    }
}
