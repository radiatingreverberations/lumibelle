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
    public AiModel? CatalogModel => Checks.GetValueOrDefault(Model.Backend)?.Models.FirstOrDefault(m => m.Id == Model.Model);
    public IEnumerable<TextModelReference> Choices => (Override is { } selected ? new[] { selected } : Array.Empty<TextModelReference>())
        .Concat(Settings.TextModelProfiles).Concat(Settings.StarredTextModels).Append(Base).DistinctBy(TextModelProfiles.ChoiceKey);
    public string? Issue => IssueFor(Model, Source != TextModelSelectionSource.GlobalDefault);
    public TextModelSelectionState State => new(Model, Issue is null, CatalogModel?.SupportsImages == true, Source == TextModelSelectionSource.GlobalDefault, Source, Issue);
    public string? IssueFor(TextModelReference model, bool requireTest = true) => TextModelPolicy.Issue(model, Settings, Checks.GetValueOrDefault(model.Backend), requireTest);
    public async Task RefreshAsync(bool allProviders = false, CancellationToken ct = default)
    {
        Settings = await settings.LoadAsync(ct);
        Preferences = await preferences.LoadAsync(projectId, ct);
        foreach (var backend in (allProviders ? Choices.Select(m => m.Backend) : [Model.Backend]).Distinct())
        {
            try { Checks[backend] = await providers.CheckAsync(backend, Settings, cancellationToken: ct); }
            catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { Checks[backend] = new(false, e.Message, []); }
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
