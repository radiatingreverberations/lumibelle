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
    private sealed class VllmTextDraft { public float? Temperature { get; set; } public int? Tokens { get; set; } }
    private readonly Dictionary<string, VllmTextDraft> _vllmTextDrafts = new(StringComparer.Ordinal);
    private VllmTextDraft VllmDraft(TextModelReference model)
    {
        var key = TextModelPolicy.Key(model);
        if (!_vllmTextDrafts.TryGetValue(key, out var draft))
        {
            var saved = VllmTextSettings.Resolve(model, _settings!);
            _vllmTextDrafts[key] = draft = new() { Temperature = saved.Temperature, Tokens = saved.MaxOutputTokens };
        }
        return draft;
    }
    private void CancelVllmDraft(TextModelReference model) => _vllmTextDrafts.Remove(TextModelPolicy.Key(model));
    private async Task SaveVllmDraftAsync(TextModelReference model, bool reset = false)
    {
        var draft = VllmDraft(model);
        if (await SaveChangeAsync(settings =>
        {
            var models = new Dictionary<string, VllmTextModelSettings>(settings.VllmTextModels, StringComparer.Ordinal);
            if (reset || draft.Temperature is null && draft.Tokens is null) models.Remove(TextModelPolicy.Key(model));
            else models[TextModelPolicy.Key(model)] = new(draft.Temperature, draft.Tokens);
            return settings with { VllmTextModels = models };
        }, reset ? "Model now uses server defaults." : "Model generation settings saved.")) CancelVllmDraft(model);
    }

    private void CancelVllm()
    {
        _vllmUrl = _settings!.VllmUrl; _vllmConcurrency = _settings.VllmConcurrency;
        _vllmKey = ""; _removeVllmKey = false; _vllmCheck = null;
    }

    private async Task ReloadVllmTestAsync(VllmTextModelBenchmark benchmark)
    {
        if (_token.IsCancellationRequested) return;
        try
        {
            var known = _settings!.VllmTextModelBenchmarks.Any(b => b.Measurement.TestId == benchmark.Measurement.TestId);
            _settings = await Settings.LoadAsync(_token); if (!known) _saveMessage = "vLLM test saved.";
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { if (!_token.IsCancellationRequested) await InvokeAsync(StateHasChanged); }
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
