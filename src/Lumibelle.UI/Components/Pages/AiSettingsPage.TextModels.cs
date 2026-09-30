using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

public partial class AiSettingsPage
{
    private sealed class TextCatalogState
    {
        public string Search = "", Sort = "name";
        public bool StarredOnly, Attempted, Refreshing, Descending;
        public int Page, Epoch;
        public string? Expanded, Message;
        public AiConnectionCheck? Catalog;
        public DateTimeOffset? Refreshed;
        public Task? Loading;
    }
    private readonly Dictionary<AiBackend, TextCatalogState> _textCatalogs = Enum.GetValues<AiBackend>().ToDictionary(b => b, _ => new TextCatalogState());
    private readonly Dictionary<string, string> _aliasDrafts = new(StringComparer.Ordinal);
    private AiBackend TextBackend => _connectionProvider switch { "openrouter" => AiBackend.OpenRouter, "codex" => AiBackend.Codex, "claudecode" => AiBackend.ClaudeCode, _ => AiBackend.ComfyUI };
    private TextCatalogState TextState => _textCatalogs[TextBackend];
    private IEnumerable<TextModelReference> AllModels => _textCatalogs.SelectMany(pair => (pair.Value.Catalog?.Models ?? [])
            .Select(model => new TextModelReference(pair.Key, model.Id, model.Name, pair.Key == AiBackend.ComfyUI ? _settings!.ComfyUrl : null)))
        .Concat(_settings!.StarredTextModels).Append(TextModelProfiles.ModelOnly(TextModelPolicy.Default(_settings)))
        .Where(model => !string.IsNullOrWhiteSpace(model.Model)).DistinctBy(TextModelPolicy.Key);
    private TextModelReference[] DefaultChoices => _settings!.TextModelProfiles.Concat(_settings.StarredTextModels).Append(TextModelPolicy.Default(_settings))
        .Append(TextModelProfiles.ModelOnly(TextModelPolicy.Default(_settings)))
        .DistinctBy(TextModelProfiles.ChoiceKey).OrderBy(ModelName, StringComparer.OrdinalIgnoreCase).ToArray();
    private AiModel? CatalogModel(TextModelReference model) => model.Backend == AiBackend.ComfyUI && !TextModelPolicy.SameServer(model.ComfyUrl, _settings!.ComfyUrl) ? null
        : _textCatalogs[model.Backend].Catalog?.Models.FirstOrDefault(m => m.Id == model.Model);
    private string? CatalogName(TextModelReference model) => CatalogModel(model)?.Name;
    private string ModelName(TextModelReference model) => TextModelPolicy.DisplayName(model, _settings!, CatalogName(model));
    private string DefaultChoiceLabel(TextModelReference model) => string.IsNullOrWhiteSpace(model.Model) ? $"{TextModelPolicy.ProviderName(model.Backend)} · No model selected"
        : TextModelPolicy.PickerLabel(model, _settings!, DefaultChoices, CatalogName);
    private string DefaultKey => TextModelProfiles.ChoiceKey(TextModelPolicy.Default(_settings!));
    private bool IsStarred(TextModelReference model) => _settings!.StarredTextModels.Any(item => TextModelPolicy.Same(item, model));
    private string? ModelIssue(TextModelReference model) => TextModelPolicy.Issue(model, _settings!, _checks.GetValueOrDefault(model.Backend));
    private string? ModelStatus(TextModelReference model) => _checks.GetValueOrDefault(model.Backend) is not { Success: true } ? null
        : ModelIssue(model) ?? (model.Backend == AiBackend.ComfyUI ? "Verified" : null);
    private bool NeedsComfyTest(TextModelReference model) => model.Backend == AiBackend.ComfyUI &&
        _checks.GetValueOrDefault(AiBackend.ComfyUI) is { Success: true, BackendVersion: { } version } &&
        TextModelPolicy.SameServer(model.ComfyUrl, _settings!.ComfyUrl) && TextModelPolicy.Verification(model, _settings!, version) is null;
    private List<TextModelReference> FilteredModels
    {
        get
        {
            var models = AllModels.Where(m => m.Backend == TextBackend && (!_starredOnly || IsStarred(m)) &&
                (_search.Length == 0 || new[] { ModelName(m), m.Name, m.Model, CatalogName(m) }.Any(value => value?.Contains(_search, StringComparison.OrdinalIgnoreCase) == true)));
            var named = models.OrderBy(ModelName, StringComparer.OrdinalIgnoreCase).ThenBy(TextModelPolicy.Key, StringComparer.Ordinal);
            if (TextState.Sort == "name") return (TextState.Descending ? models.OrderByDescending(ModelName, StringComparer.OrdinalIgnoreCase).ThenBy(TextModelPolicy.Key, StringComparer.Ordinal) : named).ToList();
            var knownFirst = named.OrderBy(m => SortValue(m, TextState.Sort) is null);
            return (TextState.Descending ? knownFirst.ThenByDescending(m => SortValue(m, TextState.Sort)) : knownFirst.ThenBy(m => SortValue(m, TextState.Sort))).ToList();
        }
    }
    private decimal? SortPrice(TextModelReference model, bool output) => CatalogModel(model)?.Catalog?.Pricing is { Variable: false } price
        ? output ? price.OutputPerToken : price.InputPerToken : null;
    private sealed record ModelColumn(string Key, string Label, string Help, bool DescendingFirst = false);
    private static readonly ModelColumn NameColumn = new("name", "Model", "Sort by display name");
    private static readonly ModelColumn[] OpenRouterColumns = [NameColumn,
        new("input", "Input", "Listed USD per million input tokens"), new("output", "Output", "Listed USD per million output tokens"),
        new("context", "Context", "Maximum context window in tokens", true), new("added", "Added", "Date added to OpenRouter, not the original release date", true),
        new("popularity", "Popularity", "Position in OpenRouter's ordering by tokens processed over the past week; lower is more popular. Unreported usage is placed at the end by OpenRouter.")];
    private static readonly ModelColumn[] ComfyColumns = [NameColumn,
        new("speed", "Speed", "Tokens per second from the saved benchmark", true),
        new("vram", "VRAM", "Additional VRAM in GiB observed during the saved benchmark"),
        new("tested", "Tested", "Date of the saved model verification", true)];
    private ModelColumn[] ModelColumns => TextBackend switch { AiBackend.OpenRouter => OpenRouterColumns, AiBackend.ComfyUI => ComfyColumns, _ => [NameColumn] };
    private ComfyTextModelBenchmark? Benchmark(TextModelReference model) => TextModelPolicy.Verification(model, _settings!)?.Benchmarks?.OrderBy(item => item.CustomPrompt).ThenByDescending(item => item.MeasuredUtc).FirstOrDefault();
    private static decimal? AdditionalVram(ComfyTextModelBenchmark? benchmark) => benchmark is { PeakVramUsedBytes: { } peak, BaselineVramUsedBytes: { } baseline }
        ? Math.Max(0, peak - baseline) / 1073741824m : null;
    private decimal? SortValue(TextModelReference model, string key) => key switch
    {
        "input" => SortPrice(model, false), "output" => SortPrice(model, true),
        "context" => CatalogModel(model)?.Catalog?.ContextLength,
        "added" => CatalogModel(model)?.Catalog?.AddedUtc?.ToUnixTimeSeconds(),
        "popularity" => CatalogModel(model)?.Catalog?.PopularityOrder,
        "speed" => Benchmark(model)?.TokensPerSecond is { } speed && double.IsFinite(speed) && speed is > 0 and < (double)decimal.MaxValue ? (decimal)speed : null,
        "vram" => AdditionalVram(Benchmark(model)),
        "tested" => TextModelPolicy.Verification(model, _settings!)?.VerifiedUtc.ToUnixTimeSeconds(), _ => null
    };
    private string ColumnValue(TextModelReference model, string key) => key switch
    {
        "input" or "output" => CatalogModel(model)?.Catalog?.Pricing is { Variable: true } ? "Variable" : OpenRouterModelMetadata.PerMillion(SortPrice(model, key == "output")).Replace(" / 1M", ""),
        "context" => CatalogModel(model)?.Catalog?.ContextLength is { } context ? OpenRouterModelMetadata.Context(context) : "—",
        "added" => CatalogModel(model)?.Catalog?.AddedUtc?.ToString("yyyy-MM-dd") ?? "—",
        "popularity" => CatalogModel(model)?.Catalog?.PopularityOrder is { } rank ? $"#{rank}" : "—",
        "speed" => Benchmark(model)?.TokensPerSecond is { } speed ? FormattableString.Invariant($"{speed:0.0} tokens/s") : "—",
        "vram" => AdditionalVram(Benchmark(model)) is { } vram ? FormattableString.Invariant($"~{vram:0.0} GiB") : "—",
        "tested" => TextModelPolicy.Verification(model, _settings!)?.VerifiedUtc.ToString("yyyy-MM-dd") ?? "—", _ => "—"
    };
    private string SortAria(string key) => TextState.Sort == key ? TextState.Descending ? "descending" : "ascending" : "none";
    private string SortArrow(string key) => TextState.Sort == key ? TextState.Descending ? "↓" : "↑" : "↕";
    private string SortButtonLabel(ModelColumn column)
    {
        var descending = TextState.Sort == column.Key ? !TextState.Descending : column.DescendingFirst;
        return $"Sort by {column.Label.ToLowerInvariant()}, {(descending ? "descending" : "ascending")}";
    }
    private void SortBy(string key)
    {
        var column = ModelColumns.FirstOrDefault(c => c.Key == key); if (column is null) return;
        TextState.Descending = TextState.Sort == key ? !TextState.Descending : column.DescendingFirst;
        TextState.Sort = key; ResetPage();
    }
    private void MobileSortChanged() { TextState.Descending = ModelColumns.First(c => c.Key == TextState.Sort).DescendingFirst; ResetPage(); }
    private void ReverseSort() { TextState.Descending = !TextState.Descending; ResetPage(); }
    private int PageCount => Math.Max(1, (FilteredModels.Count + PageSize - 1) / PageSize);
    private IEnumerable<TextModelReference> PagedModels => FilteredModels.Skip(Math.Min(_page, PageCount - 1) * PageSize).Take(PageSize);
    private string ModelDomId(TextModelReference model) => "text-model-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(TextModelPolicy.Key(model))))[..16];
    private void ExpandModel(TextModelReference model)
    {
        var key = TextModelPolicy.Key(model);
        TextState.Expanded = TextState.Expanded == key ? null : key;
        _aliasDrafts.TryAdd(key, _settings!.TextModelAliases.GetValueOrDefault(key, ""));
    }
    private void CancelAlias(TextModelReference model) => _aliasDrafts[TextModelPolicy.Key(model)] = _settings!.TextModelAliases.GetValueOrDefault(TextModelPolicy.Key(model), "");
    private async Task SaveAliasAsync(TextModelReference model)
    {
        var key = TextModelPolicy.Key(model); var alias = _aliasDrafts[key].Trim();
        if (alias.Length > 120) { _error = "Model aliases must be at most 120 characters."; return; }
        if (await SaveChangeAsync(settings =>
        {
            var aliases = new Dictionary<string, string>(settings.TextModelAliases, StringComparer.Ordinal);
            if (alias.Length == 0) aliases.Remove(key); else aliases[key] = alias;
            return settings with { TextModelAliases = aliases };
        }, alias.Length == 0 ? "Model alias cleared." : "Model alias saved. Studio pickers use this name.")) CancelAlias(model);
    }
    private string ConnectionIdentity(AiBackend backend) => backend switch
    {
        AiBackend.ComfyUI => _settings!.ComfyUrl,
        AiBackend.Codex => $"{_settings!.Codex.Enabled}\n{_settings.Codex.ExecutablePath}",
        AiBackend.ClaudeCode => $"{_settings!.ClaudeCode.Enabled}\n{_settings.ClaudeCode.ExecutablePath}",
        _ => _settings!.HasOpenRouterKey.ToString()
    };
    private void InvalidateTextCatalog(AiBackend backend)
    {
        var state = _textCatalogs[backend]; state.Epoch++; state.Attempted = false; state.Catalog = null;
        state.Refreshed = null; state.Message = null; _checks.Remove(backend);
    }
    private Task RefreshTextAsync(AiBackend backend)
    {
        var state = _textCatalogs[backend];
        if (state.Loading is { IsCompleted: false } pending) return pending;
        state.Loading = ReadTextCatalogAsync(backend, state);
        return state.Loading;
    }
    private async Task ReadTextCatalogAsync(AiBackend backend, TextCatalogState state)
    {
        state.Attempted = true; state.Refreshing = true; state.Message = null;
        var epoch = state.Epoch; var identity = ConnectionIdentity(backend); var settings = _settings!;
        try
        {
            var check = backend == AiBackend.OpenRouter && !settings.HasOpenRouterKey ? new AiConnectionCheck(false, "Add an OpenRouter key in Connections to load models.", [])
                : backend == AiBackend.Codex && !settings.Codex.Enabled ? new(false, "Enable Codex in Connections to load models.", [])
                : backend == AiBackend.ClaudeCode && !settings.ClaudeCode.Enabled ? new(false, "Enable Claude Code in Connections to load models.", [])
                : await Providers.CheckAsync(backend, settings, cancellationToken: _token);
            if (state.Epoch != epoch || identity != ConnectionIdentity(backend)) return;
            _checks[backend] = check;
            if (check.Success) { state.Catalog = check; state.Refreshed = DateTimeOffset.UtcNow; }
            else state.Message = check.Message;
        }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException)
        {
            if (state.Epoch != epoch || identity != ConnectionIdentity(backend)) return;
            state.Message = e.Message; _checks[backend] = new(false, e.Message, []);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally
        {
            state.Refreshing = false; state.Loading = null;
            if (!_token.IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }
    private async Task ChangeDefaultAsync(string key)
    {
        if (Busy) return;
        var model = DefaultChoices.FirstOrDefault(m => TextModelProfiles.ChoiceKey(m) == key);
        if (model is null || key == DefaultKey || (model.ProfileId is null && !IsStarred(model) && !TextModelPolicy.Same(model, TextModelPolicy.Default(_settings!)))) return;
        _checking = true; _error = null;
        try
        {
            await RefreshTextAsync(model.Backend);
            if (ModelIssue(TextModelPolicy.WithDefaultEffort(model, _settings!)) is { } issue) { _error = issue; return; }
            await PersistAsync(settings => model.Backend switch
            {
                AiBackend.ComfyUI => settings with { DefaultBackend = model.Backend, ComfyModel = model.Model, TextDefault = model.ProfileId is not null ? model : null },
                AiBackend.Codex => settings with { DefaultBackend = model.Backend, Codex = settings.Codex with { TextModel = model.Model }, TextDefault = model.ProfileId is not null ? model : null },
                AiBackend.ClaudeCode => settings with { DefaultBackend = model.Backend, ClaudeCode = settings.ClaudeCode with { TextModel = model.Model }, TextDefault = model.ProfileId is not null ? model : null },
                _ => settings with { DefaultBackend = model.Backend, OpenRouterModel = model.Model, TextDefault = model.ProfileId is not null ? model : null }
            }, "Global default updated.");
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { _checking = false; }
    }
}
