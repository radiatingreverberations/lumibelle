using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

/// <summary>A draft request's model choice. Only an explicit default update persists a choice.</summary>
public sealed class TextModelSession(Guid projectId, IAiSettingsStore settings, IProjectAiPreferencesStore preferences, IAiProviderRegistry providers)
{
    public AiSettings Settings { get; private set; } = new();
    public ProjectAiPreferences Preferences { get; private set; } = new() { ProjectId = projectId };
    public TextModelReference? Override { get; private set; }
    public Dictionary<AiBackend, AiConnectionCheck> Checks { get; } = [];
    public TextModelSelectionSource Source => Override is not null ? TextModelSelectionSource.RequestOverride
        : Preferences.TextDefault is not null ? TextModelSelectionSource.ProjectDefault : TextModelSelectionSource.GlobalDefault;
    public TextModelReference Base => Preferences.TextDefault ?? TextModelPolicy.Default(Settings);
    public TextModelReference Model => TextModelPolicy.WithDefaultEffort(Override ?? Base, Settings);
    public AiModel? CatalogModel => TextModelPolicy.CatalogModel(Model, Checks.GetValueOrDefault(Model.Backend));
    public IEnumerable<TextModelReference> Choices => (Override is { } selected ? new[] { selected } : Array.Empty<TextModelReference>())
        .Concat(Settings.TextModelProfiles).Concat(Settings.StarredTextModels).Append(Base).DistinctBy(TextModelProfiles.ChoiceKey);
    public string? Issue => IssueFor(Model, Source != TextModelSelectionSource.GlobalDefault);
    public TextModelSelectionState State => new(Model, Issue is null, CatalogModel?.SupportsImages == true, Source == TextModelSelectionSource.GlobalDefault, Source, Issue)
        { ImageInputKnown = CatalogModel?.ImageInputKnown == true };
    public bool HasAvailability(TextModelReference model) => Checks.TryGetValue(model.Backend, out var check) &&
        (model.Backend != AiBackend.Vllm || check.Connections?.Any(c => TextModelPolicy.SameVllmServer(c.Server, model.VllmUrl)) == true);
    public string? ChoiceIssueFor(TextModelReference model) => TextModelPolicy.ConfigurationIssue(model, Settings);
    public string? IssueFor(TextModelReference model, bool requireTest = true) => TextModelPolicy.Issue(model, Settings, HasAvailability(model) ? Checks[model.Backend] : null, requireTest);
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var configured = await settings.LoadAsync(ct);
        var preferred = await preferences.LoadAsync(projectId, ct);
        ct.ThrowIfCancellationRequested();
        foreach (var backend in Checks.Keys.ToArray())
            if (AvailabilityConfiguration(backend, Settings) != AvailabilityConfiguration(backend, configured)) Checks.Remove(backend);
        Settings = configured;
        Preferences = preferred;
    }
    private static string AvailabilityConfiguration(AiBackend backend, AiSettings value) => backend switch {
        AiBackend.ComfyUI => JsonSerializer.Serialize(new { value.ComfyUrl, value.ComfyTextModelVerifications }),
        AiBackend.Vllm => JsonSerializer.Serialize(new { Connections = OpenAiConnections.All(value), value.VllmTextModelBenchmarks }),
        AiBackend.Codex => JsonSerializer.Serialize(value.Codex),
        AiBackend.ClaudeCode => JsonSerializer.Serialize(value.ClaudeCode),
        _ => JsonSerializer.Serialize(value.HasOpenRouterKey)
    };
    public async Task RefreshAsync(bool allProviders = false, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        var configured = Settings;
        var selected = Model;
        if (!allProviders && TextModelPolicy.ConfigurationIssue(selected, configured) is not null) return;
        foreach (var backend in (allProviders ? Choices.Select(m => m.Backend) : [Model.Backend]).Distinct())
        {
            var checkSettings = backend == AiBackend.Vllm && !allProviders ? OpenAiConnections.Capture(selected, configured) : configured;
            AiConnectionCheck check;
            try { check = await providers.CheckAsync(backend, checkSettings, cancellationToken: ct).WaitAsync(ct); }
            catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { check = new(false, e.Message, []); }
            ct.ThrowIfCancellationRequested();
            if (AvailabilityConfiguration(backend, configured) != AvailabilityConfiguration(backend, Settings)) continue;
            if (backend == AiBackend.Vllm && !allProviders && check.Connections is null)
                check = check with { Connections = [new(VllmConnection.NormalizeUrl(selected.VllmUrl!), OpenAiConnections.Name(configured, selected.VllmUrl), check.Success, check.Message)] };
            if (backend == AiBackend.Vllm && !allProviders && Checks.TryGetValue(backend, out var previous)) {
                var models = previous.Models.Where(m => !TextModelPolicy.SameVllmServer(m.ServerUrl, selected.VllmUrl)).Concat(check.Models).ToArray();
                var connections = (previous.Connections ?? []).Where(c => !TextModelPolicy.SameVllmServer(c.Server, selected.VllmUrl)).Concat(check.Connections ?? []).ToArray();
                check = check with { Success = models.Length > 0, Models = models, Connections = connections };
            }
            Checks[backend] = check;
        }
    }
    public void Select(TextModelReference? model) => Override = model;
    public void SetEffort(string? effort) => Override = (Override ?? Base) with { ReasoningEffort = string.IsNullOrEmpty(effort) ? null : effort };
    public async Task SetDefaultAsync(bool inheritGlobal, CancellationToken ct = default)
    {
        Preferences = await preferences.SetTextDefaultAsync(projectId, inheritGlobal ? null : Override ?? Base, Preferences.TextDefaultRevision, ct);
        Override = null;
    }
    public async Task SubmittedAsync(CancellationToken ct = default) { Override = null; await RefreshAsync(ct: ct); }
    public static bool SameChoice(TextModelSelectionState before, TextModelSelectionState after) =>
        TextModelProfiles.SameConfiguration(before.Model, after.Model) && before.Source == after.Source;
    public static AiJobSubmission Attribute(AiJobSubmission submission, TextModelSelectionState? captured)
    {
        if (captured is null) return submission;
        var request = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options) ?? throw new WorkspaceStoreException("The captured text request is unavailable.");
        var selected = request.SelectedModel ?? request.Model;
        // Unset sampling values may be filled by capture. An explicit choice and the
        // provider/model/server/profile identity must still match exactly.
        var comparable = request.Model with {
            Temperature = selected.Temperature is null ? null : request.Model.Temperature,
            MaxOutputTokens = selected.MaxOutputTokens is null ? null : request.Model.MaxOutputTokens
        };
        if (!TextModelProfiles.SameConfiguration(selected, captured.Model) || !TextModelProfiles.SameConfiguration(comparable, selected))
            throw new WorkspaceStoreException("The request model changed. Reopen assistance and submit again.");
        return submission with { Snapshot = JsonSerializer.SerializeToElement(request with { SelectionSource = captured.Source }, AtomicJsonFile.Options) };
    }
}
