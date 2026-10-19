using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

public partial class AiSettingsPage
{
    private string _vllmUrl = "", _vllmKey = "";
    private string? _vllmCheck;
    private int _vllmConcurrency = 1;
    private bool _removeVllmKey;

    private void CancelVllm()
    {
        _vllmUrl = _settings!.VllmUrl; _vllmConcurrency = _settings.VllmConcurrency;
        _vllmKey = ""; _removeVllmKey = false; _vllmCheck = null;
    }

    private async Task CheckVllmAsync()
    {
        if (Busy) return;
        _checking = true; _error = null; _vllmCheck = null;
        try
        {
            var check = await Providers.CheckAsync(AiBackend.Vllm, _settings! with { VllmUrl = _vllmUrl },
                _removeVllmKey ? "" : string.IsNullOrWhiteSpace(_vllmKey) ? null : _vllmKey, _token);
            _vllmCheck = check.Message;
        }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { _error = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { _checking = false; }
    }

    private async Task SaveVllmAsync()
    {
        if (Busy) return;
        _saving = true; _error = null; _saveMessage = null;
        try
        {
            _settings = await Settings.SaveVllmAsync(_settings! with { VllmUrl = _vllmUrl, VllmConcurrency = _vllmConcurrency },
                _vllmKey, _removeVllmKey, _token);
            InvalidateTextCatalog(AiBackend.Vllm); CancelVllm(); _conflict = false;
            _saveMessage = "vLLM connection saved. Refresh vLLM in Text models to choose a served model.";
        }
        catch (WorkspaceConflictException) { _conflict = true; _error = "Another tab changed AI settings. Reload saved settings, then retry your change."; }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { _saving = false; }
    }
}
