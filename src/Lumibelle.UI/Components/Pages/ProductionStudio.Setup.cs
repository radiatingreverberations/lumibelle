using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private IEnumerable<lumibelle.Models.LegacySetupContent> EarlierShotContent => _production.EarlierSetupContent.Where(s => s.Content.ShotId == _selected);
    private async Task RestoreEarlierShotContent(lumibelle.Models.LegacySetupContent earlier)
    {
        if (Current?.ShotId != earlier.Content.ShotId || _compositionBusy || CompositionJob?.LocksTarget == true) return;
        if (_promptEditor is not null) await _promptEditor.FlushAsync();
        EditComposition(c => { earlier.Content.Apply(c); c.ReviewJobId = null; });
        if (await Save()) { await RefreshCompositionResult(); Notify("Earlier prompt and references restored. Generation settings are unchanged."); }
    }
    private bool _setupDialogOpen, _closingSetupDialog, _restoreSetupFocus;
    private string _setupTab = "Prompt";
    private ElementReference _setupPromptButton, _setupSettingsButton;
    private string SetupPromptStatus => CompositionJob?.LocksTarget == true ? "Writing…"
        : _compositionResult is not null ? "Review ready"
        : CurrentPromptReferenceCheck.NeedsAttention ? CurrentPromptReferenceCheck.Label
        : PromptNeedsReview ? "Review prompt"
        : CurrentPromptReferenceCheck.State == lumibelle.Services.Production.PromptReferenceState.Checking ? CurrentPromptReferenceCheck.Label : "Reviewed";

    private string? ShotPreparationIssue => Selected is { } shot ? lumibelle.Services.Production.ProductionPolicy.ShotPreparationIssue(shot) : null;
    private string ShotPreparationAction => Selected?.SceneId is null || Selected?.ApprovedScriptId is null ? "Choose scene"
        : string.IsNullOrWhiteSpace(Selected.Description) ? "Add direction" : "Set duration";
    private string GenerationIssueAction => ActiveGlobalSetup?.Archived == true ? "Review preset"
        : ShotPreparationIssue is not null ? ShotPreparationAction
        : ReferenceRepairIssue is not null ? "Review references" : PromptReviewIssue is not null ? "Review prompt" : "Review preset";
    private bool PromptBlocksGeneration => GenerationIssue is not null && GenerationIssueAction == "Review prompt";
    private ElementReference _shotSceneField, _shotDurationField, _shotDirectionField;
    private async Task ReviewShotPrompt(Guid id)
    {
        if (_selected != id) await Select(id);
        if (_selected == id) await OpenSetupDialog("Prompt");
    }
    private async Task CompleteShotDetails()
    {
        if (_setupDialogOpen) { await CloseSetupDialog(); if (_setupDialogOpen) return; }
        _restoreSetupFocus = false;
        if (_workspace is not null) await _workspace.ShowCenterAsync("Shot");
        await (Selected?.SceneId is null || Selected?.ApprovedScriptId is null ? _shotSceneField
            : string.IsNullOrWhiteSpace(Selected.Description) ? _shotDirectionField : _shotDurationField).FocusAsync();
    }

    private async Task OpenSetupDialog(string tab)
    {
        if (Current is null) return;
        if (tab == "Settings") {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (!await Save()) return;
            try {
                _globalSetups = await GenerationSetups.LoadAsync(_lifetime.Token);
                _production = await Production.LoadAsync(Id, _lifetime.Token);
                _savedComposition = Current?.Copy();
            } catch (Exception e) { _error = e.Message; return; }
        }
        _setupTab = tab;
        _setupDialogOpen = true;
    }
    private Task SetupDialogVisibility(bool visible) => visible ? Task.CompletedTask : CloseSetupDialog();
    private async Task CloseSetupDialog()
    {
        if (!_setupDialogOpen || _closingSetupDialog || _compositionBusy || _setupBusy) return;
        _closingSetupDialog = true;
        try
        {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (!await Save()) return;
            _setupDialogOpen = false; _restoreSetupFocus = true;
            _promptEditor = null; _compositionAssist = null;
        }
        finally { _closingSetupDialog = false; }
    }
    private async Task RestoreSetupFocus()
    {
        if (_restoreSetupFocus)
        {
            _restoreSetupFocus = false;
            if (_workspace is not null) await _workspace.ShowToolsAsync();
            await (_setupTab == "Prompt" ? _setupPromptButton : _setupSettingsButton).FocusAsync();
        }
    }
    private async Task ReviewGenerationIssue()
    {
        if (ActiveGlobalSetup?.Archived == true) await OpenSetupDialog("Settings");
        else if (ShotPreparationIssue is not null) await CompleteShotDetails();
        else if (ReferenceRepairIssue is not null) OpenReferencePicker();
        else await OpenSetupDialog(PromptReviewIssue is not null ? "Prompt" : "Settings");
    }
}
