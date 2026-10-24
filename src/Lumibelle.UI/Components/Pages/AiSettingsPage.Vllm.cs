using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

public partial class AiSettingsPage
{
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

    private void CancelVllm() => _vllmTextDrafts.Clear();

    private async Task ReloadVllmTestAsync(VllmTextModelBenchmark benchmark)
    {
        if (_token.IsCancellationRequested) return;
        try
        {
            var known = _settings!.VllmTextModelBenchmarks.Any(b => b.Measurement.TestId == benchmark.Measurement.TestId);
            _settings = await Settings.LoadAsync(_token); if (!known) _saveMessage = "Model test saved.";
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { if (!_token.IsCancellationRequested) await InvokeAsync(StateHasChanged); }
    }

    private async Task<bool> SaveOpenAiConnectionAsync(OpenAiConnectionSettings connection, string key, bool removeKey)
    {
        if (Busy) return false;
        _saving = true; _error = null; _saveMessage = null; StateHasChanged();
        try
        {
            connection = OpenAiConnections.Normalize(connection);
            var current = OpenAiConnections.All(_settings!);
            var connections = current.Any(c => c.Id == connection.Id)
                ? current.Select(c => c.Id == connection.Id ? connection : c).ToArray() : [.. current, connection];
            _settings = await Settings.SaveOpenAiConnectionAsync(_settings! with { OpenAiConnections = connections }, connection.Id, key, removeKey, _token);
            InvalidateTextCatalog(AiBackend.Vllm); _conflict = false;
            _saveMessage = $"{connection.Name} connection saved. Refresh OpenAI-compatible models under Text models.";
            return true;
        }
        catch (WorkspaceConflictException) { _conflict = true; _error = "Another tab changed AI settings. Reload saved settings, then retry your change."; }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { _saving = false; StateHasChanged(); }
        return false;
    }

    private async Task<bool> RemoveOpenAiConnectionAsync(Guid id)
    {
        var save = SaveChangeAsync(settings => settings with { OpenAiConnections = OpenAiConnections.All(settings).Where(c => c.Id != id).ToArray() },
            "Connection removed. Saved model choices and captured requests retain their server identity.");
        StateHasChanged();
        try
        {
            var saved = await save;
            if (saved) InvalidateTextCatalog(AiBackend.Vllm);
            return saved;
        }
        finally { StateHasChanged(); }
    }
}
