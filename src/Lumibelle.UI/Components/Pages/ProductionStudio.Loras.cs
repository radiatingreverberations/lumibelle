using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    [Inject] public IComfyLoraCatalog LoraCatalog { get; set; } = null!;
    [Inject] public IProjectAiPreferencesStore LoraPreferences { get; set; } = null!;
    private AiSettings? _loraSettings;
    private LoraVisibility _loraVisibility = new();
    private ComfyLoraCheck? _loraCheck;
    private string? _loraError;
    private int _loraRefreshVersion;
    private bool _loraRefreshing;
    private readonly Dictionary<Guid, bool> _loraValidity = [];
    // Strength validity of each shot's own LoRA picker, by shot.
    private readonly Dictionary<Guid, bool> _shotLoraValidity = [];
    private bool LorasLocked => _starting || Selected is { } shot && (_videoEnqueues.ContainsKey(shot.Id) ||
        AiJobs.View.Jobs.Any(j => j.Kind == AiJobKind.Video && j.Target.ProjectId == Id && j.Target.ShotId == shot.Id && j.LocksTarget));
    private string? LoraIssue
    {
        get
        {
            if (Selected is not { } shot) return null;
            if (_loraValidity.GetValueOrDefault(Current!.Id, true) == false || _shotLoraValidity.GetValueOrDefault(shot.Id, true) == false)
                return "Enter valid LoRA strengths before generating.";
            var active = H3Loras.Selections(shot).Where(l => l.Enabled && l.Strength != 0).ToArray();
            if (active.Length == 0) return null;
            if (_loraRefreshing) return "Checking H3 LoRAs…";
            if (_loraError is not null) return _loraError;
            if (_loraSettings is null) return "Refresh H3 LoRAs to check availability.";
            return active.Select(l => LoraPolicy.Issue(l, _loraSettings, LoraWorkflow.MiniMaxH3Ref2VA, _loraCheck, _loraVisibility) is { } issue
                ? $"{l.Reference.Name}: {issue}" : null).FirstOrDefault(i => i is not null);
        }
    }
    // Take refinement is hidden until its companion nodes are tested; see H3Settings.TakeRefinement.
    private bool _takeRefinement;
    private async Task RefreshH3LorasAsync()
    {
        var project = Id; var version = ++_loraRefreshVersion; _loraRefreshing = true;
        bool Current() => !_disposed && project == Id && version == _loraRefreshVersion;
        try
        {
            var settings = await Settings.LoadAsync(_lifetime.Token);
            _takeRefinement = settings.H3.TakeRefinement;
            var preferences = await LoraPreferences.LoadAsync(project, _lifetime.Token);
            LoraPolicy.ValidateVisibility(preferences.LoraVisibility);
            var check = await LoraCatalog.CheckAsync(settings, _lifetime.Token);
            if (!Current()) return;
            _loraSettings = settings; _loraVisibility = preferences.LoraVisibility; _loraCheck = check; _loraError = null;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (Current()) { _loraError = e.Message; _loraCheck = null; } }
        finally { if (Current()) _loraRefreshing = false; }
    }
    private void ChangeLoras(IReadOnlyList<LoraSelection> selections)
    {
        if (LorasLocked) return;
        Edit(s => s.Loras = selections.Count == 0 ? null : LoraPolicy.Capture(selections));
    }
    private void ChangeShotLoras(IReadOnlyList<LoraSelection> selections)
    {
        if (LorasLocked) return;
        Edit(s => s.ShotLoras = selections.Count == 0 ? null : LoraPolicy.Capture(selections));
    }
    private static string ShotLoraSummary(Shot shot)
    {
        var own = (shot.ShotLoras ?? []).Count(l => l.Enabled && l.Strength != 0);
        var total = H3Loras.Selections(shot).Count(l => l.Enabled && l.Strength != 0);
        return total == own ? $"{own} active" : $"{own} active · {total} with preset";
    }
    private static IReadOnlyList<string> PresetLoraNames(Shot shot) =>
        [.. (shot.Loras ?? []).Where(p => p.Enabled && p.Strength != 0 && !(shot.ShotLoras ?? []).Any(o => LoraPolicy.Same(o.Reference, p.Reference))).Select(p => p.Reference.Name)];
    private void InsertLoraTrigger(string trigger)
    {
        if (LorasLocked || string.IsNullOrWhiteSpace(trigger)) return;
        EditComposition(c => c.DirectingNotes = c.DirectingNotes.TrimEnd() + "\n" + trigger);
    }
}
