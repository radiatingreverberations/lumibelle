using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using lumibelle.Models;
using lumibelle.Services.Assets;

namespace lumibelle.Components.Assets;

public partial class ReferenceReelsPanel
{
    private static readonly DialogOptions SetupOptions = new() { MaxWidth = MaxWidth.Large, FullWidth = true, CloseOnEscapeKey = false, BackdropClick = false };
    private readonly string _promptHistoryScope = $"reel:{Guid.NewGuid():N}";
    private bool _setupDialogOpen, _closingSetupDialog, _restoreSetupFocus;
    private string _setupTab = "Prompt";
    private ElementReference _setupPromptButton, _setupSettingsButton;
    private string SaveStatus => _saving ? "Saving…" : _saveFailed ? "Save failed" : _draft?.Revision > 0 ? "Saved" : "New recipe";
    private string SetupPromptStatus => ActiveComposition is not null ? "Writing…" : !HasPromptPair ? "Missing"
        : _draft!.CheckedInputs != ReferenceReels.InputsFingerprint(_draft) ? "Review prompt" : "Ready";
    private string VoiceModeLabel => _draft?.VoiceMode switch { ReelVoiceMode.NewVoice => "New voice", ReelVoiceMode.ExistingRecording => "Existing recording", _ => "Silent" };

    private async Task OpenSetupDialog(string tab)
    {
        await _reset;
        if (_disposed || _draft is null) return;
        if (_presetBusy || _busy) return;
        await Run(RefreshPresets);
        _setupTab = tab;
        _setupDialogOpen = true;
    }

    private Task SetupDialogVisibility(bool visible) => visible ? Task.CompletedTask : CloseSetupDialog();
    private async Task CloseSetupDialog()
    {
        if (!_setupDialogOpen || _closingSetupDialog || _busy) return;
        _closingSetupDialog = true;
        try
        {
            if (!await FlushForClose()) return;
            _setupDialogOpen = false; _restoreSetupFocus = true;
            _prompt = null; _assist = null;
        }
        finally { _closingSetupDialog = false; }
    }

    private async Task RestoreSetupFocus()
    {
        if (_restoreSetupFocus)
        {
            _restoreSetupFocus = false;
            await RevealTools.InvokeAsync();
            await (_setupTab == "Prompt" ? _setupPromptButton : _setupSettingsButton).FocusAsync();
        }
    }
}
