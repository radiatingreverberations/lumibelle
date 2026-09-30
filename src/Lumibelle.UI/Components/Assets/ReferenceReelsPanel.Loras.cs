using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Assets;

public partial class ReferenceReelsPanel
{
    [Inject] public IComfyLoraCatalog LoraCatalog { get; set; } = null!;
    [Inject] public IProjectAiPreferencesStore LoraPreferences { get; set; } = null!;
    private AiSettings? _loraSettings;
    private LoraVisibility _loraVisibility = new();
    private ComfyLoraCheck? _loraCheck;
    private string? _loraError;
    private bool _presetLoraValid = true, _reelLoraValid = true;
    private bool _loraValid => _presetLoraValid && _reelLoraValid;

    protected override async Task OnInitializedAsync() { await LoadPresets(); _presetsReady = true; await LoadLoraOptions(false); }
    private Task RefreshLoras() => LoadLoraOptions(true);
    private async Task LoadLoraOptions(bool checkCatalog)
    {
        try
        {
            var settings = await Settings.LoadAsync(_lifetime.Token);
            var preferences = await LoraPreferences.LoadAsync(ProjectId, _lifetime.Token);
            LoraPolicy.ValidateVisibility(preferences.LoraVisibility);
            var check = checkCatalog ? await LoraCatalog.CheckAsync(settings, _lifetime.Token) : null;
            if (_disposed) return;
            _loraSettings = settings; _loraVisibility = preferences.LoraVisibility; _loraCheck = check; _loraError = null;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) { _loraError = e.Message; _loraCheck = null; } }
    }

    private Task ChangeLoras(IReadOnlyList<LoraSelection> selections) => EditPreset(p =>
        p.Settings.Loras = selections.Count == 0 ? null : LoraPolicy.Capture(selections));

    private Task ChangeReelLoras(IReadOnlyList<LoraSelection> selections) => Run(async () =>
    {
        if (_draft is null || _enqueue is not null) return;
        _draft.ReelLoras = selections.Count == 0 ? null : LoraPolicy.Capture(selections);
        await Save();
    });
    private string ReelLoraSummary(ReferenceReelDraft draft)
    {
        var own = (draft.ReelLoras ?? []).Count(l => l.Enabled && l.Strength != 0);
        var total = lumibelle.Services.Shots.H3Loras.Merge(draft.Loras, draft.ReelLoras).Count(l => l.Enabled && l.Strength != 0);
        return total == own ? $"{own} active" : $"{own} active · {total} with preset";
    }
    private static IReadOnlyList<string> PresetLoraNames(ReferenceReelDraft draft) =>
        [.. (draft.Loras ?? []).Where(p => p.Enabled && p.Strength != 0 && !(draft.ReelLoras ?? []).Any(o => LoraPolicy.Same(o.Reference, p.Reference))).Select(p => p.Reference.Name)];
    private Task InsertLoraTrigger(string trigger) => Run(async () =>
    {
        if (_draft is null || _enqueue is not null || string.IsNullOrWhiteSpace(trigger)) return;
        _draft.Instructions = (_draft.Instructions.TrimEnd() + "\n" + trigger).TrimStart();
        await Save();
    });
}
