using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace lumibelle.Components.Pages;

public partial class AiSettingsPage
{
    [Parameter, SupplyParameterFromQuery(Name = "tab")] public string? InitialTab { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "provider")] public string? InitialProvider { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "returnUrl")] public string? RequestedReturnUrl { get; set; }
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    private async Task<bool> SaveCodexAsync(CodexSettings draft)
    {
        var save = SaveChangeAsync(settings => settings with { Codex = settings.Codex with { Enabled = draft.Enabled, ExecutablePath = draft.ExecutablePath, Concurrency = draft.Concurrency } }, "Codex connection saved.");
        StateHasChanged();
        try { var saved = await save; if (saved) InvalidateTextCatalog(AiBackend.Codex); return saved; } finally { StateHasChanged(); }
    }
    private async Task<bool> SaveClaudeCodeAsync(ClaudeCodeSettings draft)
    {
        var save = SaveChangeAsync(settings => settings with { ClaudeCode = settings.ClaudeCode with { Enabled = draft.Enabled, ExecutablePath = draft.ExecutablePath, Concurrency = draft.Concurrency } }, "Claude Code connection saved.");
        StateHasChanged();
        try { var saved = await save; if (saved) InvalidateTextCatalog(AiBackend.ClaudeCode); return saved; } finally { StateHasChanged(); }
    }
    private async Task<bool> SaveLoraLibraryAsync(IReadOnlyList<LoraDefinition> library)
    {
        var save = SaveChangeAsync(settings => settings with { LoraLibrary = library.ToArray() }, "LoRA library saved.");
        StateHasChanged();
        try { return await save; }
        finally { StateHasChanged(); }
    }
    [Parameter, SupplyParameterFromQuery(Name = "projectId")] public Guid? ProjectId { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "returnTo")] public string? ReturnTo { get; set; }
    private Task<bool> SaveVideoSettingsAsync(H3Settings video) => SaveChangeAsync(settings => settings with { H3 = ShotCopy.Of(video) }, "Video settings saved.");
    private string ReturnUrl => AiSettingsNavigation.Return(RequestedReturnUrl, ProjectId, ReturnTo).Url;
    private string ReturnLabel => AiSettingsNavigation.Return(RequestedReturnUrl, ProjectId, ReturnTo).Label;
    private static readonly (string Key, string Label)[] Tabs =
        [("connections", "Connections"), ("text", "Text models"), ("images", "Image models"),
         ("video", "Video models"), ("loras", "LoRAs")];
    private readonly ElementReference[] _tabElements = new ElementReference[Tabs.Length];
    private readonly HashSet<string> _visitedProviders = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    public AiSettingsPage() { _token = _lifetime.Token; }
    private readonly Dictionary<AiBackend, AiConnectionCheck> _checks = [];
    private AiSettings? _settings;
    private string _tab = "connections", _connectionProvider = "comfyui";
    private int _page { get => TextState.Page; set => TextState.Page = value; }
    private const int PageSize = 25;
    private string _search { get => TextState.Search; set => TextState.Search = value; }
    private bool _starredOnly { get => TextState.StarredOnly; set => TextState.StarredOnly = value; }
    private bool _loading = true, _saving, _checking, _dialogOpen, _conflict;
    private string? _error, _saveMessage, _comfyCheck, _openRouterCheck, _imageEditCheck;
    private string _comfyUrl = "", _replacementKey = "";
    private bool _removeKey;
    private string _comfyImageModel = "", _comfyImageEncoder = "", _comfyImageVae = "", _comfyImageEditLora = "";
    // The workflow whose settings are being edited, and the separately chosen default.
    private ImageWorkflow _imageWorkflow, _defaultImageWorkflow;
    private bool _imageDefaultsSelected, _imageEntryChosen;
    private string _videoSection = lumibelle.Components.Shots.VideoSettingsPanel.H3Section;
    private static readonly (string Key, string Label, string Kind)[] ImageEntries =
        new[] { ImageWorkflow.Krea2, ImageWorkflow.Flux2Klein9bKv, ImageWorkflow.QwenImage21, ImageWorkflow.CodexImages }
        .Select(w => (w.ToString(), w.Label(), w == ImageWorkflow.CodexImages ? "Local CLI" : "ComfyUI")).ToArray();
    private string ImageEntry => _imageDefaultsSelected ? AiProviderSidebar.DefaultsKey : _imageWorkflow.ToString();
    private void SelectImageEntry(string key)
    {
        _imageEntryChosen = true;
        _imageDefaultsSelected = key == AiProviderSidebar.DefaultsKey;
        if (!_imageDefaultsSelected && Enum.TryParse<ImageWorkflow>(key, out var workflow) && workflow != _imageWorkflow)
        { _imageWorkflow = workflow; ImageWorkflowChanged(); }
    }
    private string _codexImageModel = "", _codexImageEffort = "";
    // Drafts of the global text effort for each subscription CLI.
    private readonly Dictionary<AiBackend, string> _textEfforts = [];
    private string TextEffortDraft { get => _textEfforts.GetValueOrDefault(TextBackend, ""); set => _textEfforts[TextBackend] = value; }
    private IReadOnlyList<string> TextEfforts => TextBackend == AiBackend.ClaudeCode ? ClaudeCodeClient.Efforts : (_checks.GetValueOrDefault(TextBackend)?.Models ?? [])
        .SelectMany(model => model.ReasoningEfforts ?? []).Distinct(StringComparer.Ordinal).ToArray();
    private void CancelTextEfforts() { foreach (var backend in new[] { AiBackend.Codex, AiBackend.ClaudeCode }) CancelTextEffort(backend); }
    private void CancelTextEffort(AiBackend backend) => _textEfforts[backend] = TextModelPolicy.DefaultEffort(backend, _settings!) ?? "";
    private async Task SaveTextEffortAsync()
    {
        if (Busy) return;
        var backend = TextBackend; var name = TextModelPolicy.ProviderName(backend);
        if (TextEffortDraft.Length > 0 && !TextEfforts.Contains(TextEffortDraft))
        { _error = $"Refresh {name} and choose a reported reasoning effort."; return; }
        var effort = TextEffortDraft.Length == 0 ? null : TextEffortDraft;
        if (await SaveChangeAsync(settings => backend == AiBackend.Codex ? settings with { Codex = settings.Codex with { TextEffort = effort } }
                : settings with { ClaudeCode = settings.ClaudeCode with { TextEffort = effort } }, $"Default {name} reasoning effort saved."))
            CancelTextEffort(backend);
    }
    private bool IsCodex => _imageWorkflow == ImageWorkflow.CodexImages;
    private string _fluxModel = "", _fluxEncoder = "", _fluxVae = "";
    private bool IsKlein => _imageWorkflow == ImageWorkflow.Flux2Klein9bKv;
    private bool IsQwen => _imageWorkflow == ImageWorkflow.QwenImage21;
    private QwenImage21Settings _qwen = new();
    private string ImageModel { get => IsQwen ? _qwen.Model : IsKlein ? _fluxModel : _comfyImageModel; set { if (IsQwen) _qwen = _qwen with { Model = value }; else if (IsKlein) _fluxModel = value; else _comfyImageModel = value; } }
    private string ImageEncoder { get => IsQwen ? _qwen.TextEncoder : IsKlein ? _fluxEncoder : _comfyImageEncoder; set { if (IsQwen) _qwen = _qwen with { TextEncoder = value }; else if (IsKlein) _fluxEncoder = value; else _comfyImageEncoder = value; } }
    private string ImageVae { get => IsQwen ? _qwen.Vae : IsKlein ? _fluxVae : _comfyImageVae; set { if (IsQwen) _qwen = _qwen with { Vae = value }; else if (IsKlein) _fluxVae = value; else _comfyImageVae = value; } }
    private void ImageWorkflowChanged() { _imageModels = []; _imageEncoders = []; _imageVaes = []; _imageEditLoras = []; _imageEditCheck = null; }
    private float _temperature;
    private int _maxTokens, _timeout, _imageTimeout, _openRouterConcurrency;
    private IReadOnlyList<AiModel> _imageModels = [], _imageEncoders = [], _imageVaes = [], _imageEditLoras = [];
    private bool Busy => _saving || _checking || _dialogOpen;
    private void ResetPage() => _page = 0;
    private void FilterStars(bool value) { _starredOnly = value; ResetPage(); }
    private async Task TabKeyAsync(KeyboardEventArgs args, int index)
    {
        var next = args.Key switch { "ArrowRight" => (index + 1) % Tabs.Length, "ArrowLeft" => (index + Tabs.Length - 1) % Tabs.Length, "Home" => 0, "End" => Tabs.Length - 1, _ => index };
        if (next == index) return;
        SelectTab(Tabs[next].Key); await _tabElements[next].FocusAsync();
    }
    // The URL carries only the page and its tab. A provider in an incoming link opens
    // that pane once; later selections stay in page state and are not written back.
    private bool _parametersApplied;
    private string? _appliedTab, _appliedProvider;
    protected override void OnParametersSet()
    {
        if (!_parametersApplied || InitialTab != _appliedTab)
        {
            _tab = Tabs.Any(t => t.Key == InitialTab) ? InitialTab! : string.IsNullOrEmpty(InitialTab) && RequestedJobId.HasValue ? "text" : "connections";
            // Advanced held the timeouts, which now live with their tabs; old links open Text defaults.
            if (InitialTab == "advanced") { _tab = "text"; _connectionProvider = AiProviderSidebar.DefaultsKey; }
        }
        if (InitialTab != "advanced" && (!_parametersApplied || InitialProvider is not null && InitialProvider != _appliedProvider))
        {
            _connectionProvider = AiProviderSidebar.Providers.Any(p => p.Key == InitialProvider) || InitialProvider == AiProviderSidebar.DefaultsKey ? InitialProvider! : "comfyui";
            if (_connectionProvider != AiProviderSidebar.DefaultsKey) _lastProvider = _connectionProvider;
        }
        _parametersApplied = true; _appliedTab = InitialTab; _appliedProvider = InitialProvider;
        if (RequestedJobId.HasValue && _openedJob != RequestedJobId) { _tab = "text"; _connectionProvider = "comfyui"; }
        VisitProvider();
    }
    // Connections has no Defaults entry; while Defaults is selected it keeps the last provider.
    private string _lastProvider = "comfyui";
    private string ConnectionProvider => _connectionProvider == AiProviderSidebar.DefaultsKey ? _lastProvider : _connectionProvider;
    private void VisitProvider() { if (_tab == "connections") _visitedProviders.Add(ConnectionProvider); }
    private void SelectTab(string key) { _tab = key; VisitProvider(); UpdateSelectionUrl(); }
    private void SelectProvider(string key)
    {
        _connectionProvider = key;
        if (key != AiProviderSidebar.DefaultsKey) _lastProvider = key;
        VisitProvider(); UpdateSelectionUrl();
    }
    private void UpdateSelectionUrl()
    {
        var url = Navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["tab"] = _tab, ["provider"] = null
        });
        if (url != Navigation.Uri) Navigation.NavigateTo(url, replace: true);
    }
    private AiJobCoordinator? _modelQueue;
    private readonly HashSet<Guid> _seenModelTests = [];
    private AiJobHeader[] _modelTestView = [];
    private TextModelReference? _pendingStar;
    private OpenRouterTestStatus HostedTestStatus(TextModelReference model) =>
        OpenRouterTestStatus.For(model, _settings!.OpenRouterTextModelBenchmarks, _modelQueue?.View.Jobs ?? []);
    protected override async Task OnInitializedAsync()
    {
        _modelQueue = Services.GetService<AiJobCoordinator>();
        if (_modelQueue is not null)
        {
            foreach (var job in _modelQueue.View.Jobs.Where(j => j.State == AiJobState.Completed)) _seenModelTests.Add(job.Id);
            _modelQueue.Changed += ModelTestsChanged;
        }
        await LoadAsync(); ModelTestsChanged();
    }
    private void ModelTestsChanged()
    {
        if (_token.IsCancellationRequested || _modelQueue is null || _settings is null) return;
        _ = InvokeAsync(async () =>
        {
            if (_token.IsCancellationRequested) return;
            var tests = _modelQueue.View.Jobs.Where(j => j.Kind is AiJobKind.TextBenchmark or AiJobKind.TextAdvancedTest).ToArray();
            if (tests.SequenceEqual(_modelTestView)) return;
            _modelTestView = tests;
            var completed = tests.Where(j => j.State is AiJobState.Completed or AiJobState.NeedsAttention && !_seenModelTests.Contains(j.Id)).ToArray();
            foreach (var job in completed)
            {
                try
                {
                    var result = await Services.GetRequiredService<IAiJobStore>().ReadArtifactAsync<AiModelTestJobResult>(job.Id, AiJobArtifact.Result, _token);
                    if (result?.Saved == true)
                    {
                        _seenModelTests.Add(job.Id);
                        if (result.OpenRouter is { } hosted) await ReloadOpenRouterTestAsync(hosted);
                        else if (result.Verification is { } verification) await ReloadVerificationAsync(verification);
                    }
                }
                catch (WorkspaceStoreException e) { _error = e.Message; }
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            }
            if (!_token.IsCancellationRequested) StateHasChanged();
        });
    }
    private async Task LoadAsync()
    {
        _loading = true; _error = null;
        try { _settings = await Settings.LoadAsync(_token); CancelComfy(); CancelOpenRouter(); CancelImages(); CancelComfyText(); CancelTextTimeout(); CancelTextEfforts(); }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { _loading = false; }
    }
    private async Task ReloadSnapshotAsync()
    {
        if (Busy) return;
        _saving = true;
        try { _settings = await Settings.LoadAsync(_token); foreach (var backend in _textCatalogs.Keys) InvalidateTextCatalog(backend); _conflict = false; _error = null; _saveMessage = "Saved settings reloaded. Your form drafts are still here; review them before retrying."; }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { _saving = false; }
    }
    // Build each write from the last saved snapshot, never from unrelated form drafts.
    private async Task PersistAsync(Func<AiSettings, AiSettings> change, string message, string? replacementKey = null, bool removeKey = false)
    {
        _saving = true; _error = null; _saveMessage = null;
        try { _settings = await Settings.SaveAsync(change(_settings!), replacementKey, removeKey, _token); _saveMessage = message; _conflict = false; }
        catch (WorkspaceConflictException) { _conflict = true; throw new WorkspaceStoreException("Another tab changed AI settings. Reload saved settings, then retry your change."); }
        finally { _saving = false; }
    }
    private async Task<bool> SaveChangeAsync(Func<AiSettings, AiSettings> change, string message, string? key = null, bool remove = false)
    {
        if (Busy) return false;
        try { await PersistAsync(change, message, key, remove); return true; }
        catch (WorkspaceStoreException e) { _error = e.Message; return false; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { return false; }
    }
    private async Task ToggleStarAsync(TextModelReference model, bool confirmed = false)
    {
        if (Busy) return;
        var starred = IsStarred(model);
        if (!starred && ModelIssue(model) is not null) return;
        if (!starred && !confirmed && model.Backend == AiBackend.OpenRouter && HostedTestStatus(model).WarnBeforeStarring)
        { _pendingStar = model; return; }
        if (await SaveChangeAsync(settings => settings with { StarredTextModels = starred
            ? settings.StarredTextModels.Where(item => !TextModelPolicy.Same(item, model)).ToList()
            : [.. settings.StarredTextModels, model] }, starred ? "Model removed from starred models." : "Model starred. Available in your studio pickers."))
            _pendingStar = null;
        _page = Math.Min(_page, PageCount - 1);
    }
    private async Task TestBeforeStarringAsync(TextModelReference model)
    {
        _pendingStar = null;
        await OpenModelAsync(model);
    }
    private async Task OpenModelAsync(TextModelReference model, Guid? jobId = null, bool startTest = false, bool advanced = false)
    {
        if (Busy) return;
        _dialogOpen = true;
        try
        {
            var check = model.Backend == AiBackend.OpenRouter ? _checks.GetValueOrDefault(AiBackend.OpenRouter)
                : TextModelPolicy.SameServer(model.ComfyUrl, _settings!.ComfyUrl) ? _checks.GetValueOrDefault(AiBackend.ComfyUI) : null;
            advanced &= model.Backend == AiBackend.ComfyUI;
            var dialog = await Dialogs.ShowAsync<ComfyModelDialog>(advanced ? "Advanced model test" : "Model details & test", new DialogParameters
            {
                [nameof(ComfyModelDialog.Model)] = model,
                [nameof(ComfyModelDialog.SettingsSnapshot)] = _settings,
                [nameof(ComfyModelDialog.Check)] = check,
                [nameof(ComfyModelDialog.JobId)] = jobId,
                [nameof(ComfyModelDialog.StartTest)] = startTest,
                [nameof(ComfyModelDialog.Advanced)] = advanced,
                [nameof(ComfyModelDialog.VerificationSaved)] = EventCallback.Factory.Create<ComfyTextModelVerification>(this, ReloadVerificationAsync),
                [nameof(ComfyModelDialog.OpenRouterTestSaved)] = EventCallback.Factory.Create<OpenRouterTextModelBenchmark>(this, ReloadOpenRouterTestAsync)
            }, new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true, BackdropClick = false, CloseOnEscapeKey = true });
            await dialog.Result;
        }
        finally
        {
            _dialogOpen = false;
            if (!_token.IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }
    private async Task ReloadVerificationAsync(ComfyTextModelVerification verification)
    {
        if (_token.IsCancellationRequested) return;
        try
        {
            var latest = await Settings.LoadAsync(_token);
            if (!TextModelPolicy.SameServer(latest.ComfyUrl, _settings!.ComfyUrl)) InvalidateTextCatalog(AiBackend.ComfyUI);
            // The dialog also reports a test that finished earlier; only announce new ones.
            var known = _settings.ComfyTextModelVerifications.Any(v => v.Model == verification.Model && v.VerifiedUtc == verification.VerifiedUtc &&
                TextModelPolicy.SameServer(v.ComfyUrl, verification.ComfyUrl));
            _settings = latest; if (!known) _saveMessage = "Model test saved.";
            if (TextModelPolicy.SameServer(latest.ComfyUrl, verification.ComfyUrl) && _checks.TryGetValue(AiBackend.ComfyUI, out var check))
                _checks[AiBackend.ComfyUI] = check with { BackendVersion = verification.ComfyVersion };
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { if (!_token.IsCancellationRequested) await InvokeAsync(StateHasChanged); }
    }
    private async Task ReloadOpenRouterTestAsync(OpenRouterTextModelBenchmark benchmark)
    {
        if (_token.IsCancellationRequested) return;
        try
        {
            var known = _settings!.OpenRouterTextModelBenchmarks.Any(b => b.TestId == benchmark.TestId);
            _settings = await Settings.LoadAsync(_token); if (!known) _saveMessage = "OpenRouter test saved.";
        }
        catch (WorkspaceStoreException e) { _error = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { if (!_token.IsCancellationRequested) await InvokeAsync(StateHasChanged); }
    }
    [Parameter, SupplyParameterFromQuery(Name = "jobId")] public Guid? RequestedJobId { get; set; }
    [Inject] private IServiceProvider Services { get; set; } = null!;
    private Guid? _openedJob;
    private (TextModelReference Model, Guid JobId, bool Advanced)? _pendingModelTest;
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_tab == "text" && !_loading && _settings is not null && (RequestedJobId is null || _openedJob == RequestedJobId) && !TextState.Attempted && TextState.Loading is not { IsCompleted: false })
        {
            var load = RefreshTextAsync(TextBackend); StateHasChanged(); await load;
            return;
        }
        if (!_loading && !Busy && !TextState.Refreshing && _pendingModelTest is { } pending && _tab == "text")
        {
            _pendingModelTest = null;
            await OpenModelAsync(pending.Model, pending.JobId, advanced: pending.Advanced);
            return;
        }
        if (_loading || _settings is null || Busy || RequestedJobId is not { } id || _openedJob == id) return;
        _openedJob = id;
        try
        {
            var store = Services.GetRequiredService<IAiJobStore>();
            var job = (await store.ReadAsync(_token)).Jobs.SingleOrDefault(j => j.Id == id) ?? throw new WorkspaceStoreException("Model test not found.");
            var request = AiModelTestJobHandler.Read(job, await store.ReadSnapshotAsync(id, _token));
            _pendingModelTest = (request.Model, id, request.Advanced);
            SelectProvider(request.Model.Backend == AiBackend.OpenRouter ? "openrouter" : "comfyui");
            await InvokeAsync(StateHasChanged);
        }
        catch (WorkspaceStoreException e) { _error = e.Message; await InvokeAsync(StateHasChanged); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
    }
    private void CancelComfy() { _comfyUrl = _settings!.ComfyUrl; _comfyCheck = null; }
    private void CancelOpenRouter() { _replacementKey = ""; _removeKey = false; _openRouterCheck = null; _openRouterConcurrency = _settings!.OpenRouterConcurrency; }
    private void CancelImages()
    {
        // The Image models tab opens on the default workflow until another entry is chosen.
        if (!_imageEntryChosen) _imageWorkflow = _settings!.DefaultImageWorkflow;
        foreach (var workflow in Enum.GetValues<ImageWorkflow>()) CancelImageWorkflow(workflow);
        CancelImageDefaults(); ImageWorkflowChanged();
    }
    private void CancelImageWorkflow() { CancelImageWorkflow(_imageWorkflow); _imageEditCheck = null; }
    private void CancelImageWorkflow(ImageWorkflow workflow)
    {
        switch (workflow)
        {
            case ImageWorkflow.QwenImage21: _qwen = _settings!.QwenImage21 ?? new(); break;
            case ImageWorkflow.CodexImages: _codexImageModel = _settings!.Codex.ImageModel; _codexImageEffort = _settings.Codex.ImageEffort ?? ""; break;
            case ImageWorkflow.Flux2Klein9bKv: _fluxModel = _settings!.FluxKleinModel; _fluxEncoder = _settings.FluxKleinTextEncoder; _fluxVae = _settings.FluxKleinVae; break;
            default:
                _comfyImageModel = _settings!.ComfyImageModel; _comfyImageEncoder = _settings.ComfyImageTextEncoder;
                _comfyImageVae = _settings.ComfyImageVae; _comfyImageEditLora = _settings.ComfyImageEditLora; break;
        }
    }
    private void CancelImageDefaults() { _defaultImageWorkflow = _settings!.DefaultImageWorkflow; _imageTimeout = _settings.ImageTimeoutSeconds; }
    private void CancelComfyText() { _temperature = _settings!.Temperature; _maxTokens = _settings.MaxOutputTokens; }
    private void CancelTextTimeout() => _timeout = _settings!.TimeoutSeconds;
    private async Task SaveComfyAsync()
    {
        if (await SaveChangeAsync(settings => settings with { ComfyUrl = _comfyUrl }, "ComfyUI connection saved. Refresh models to check availability."))
        { CancelComfy(); InvalidateTextCatalog(AiBackend.ComfyUI); _imageModels = []; _imageEncoders = []; _imageVaes = []; _imageEditLoras = []; _imageEditCheck = null; }
    }
    private async Task SaveOpenRouterAsync()
    {
        var keyChanged = _removeKey || !string.IsNullOrWhiteSpace(_replacementKey);
        if (await SaveChangeAsync(settings => settings with { OpenRouterConcurrency = _openRouterConcurrency }, "OpenRouter connection saved.", _replacementKey, _removeKey))
        { CancelOpenRouter(); if (keyChanged) InvalidateTextCatalog(AiBackend.OpenRouter); }
    }
    private async Task CheckComfyConnectionAsync()
    {
        if (Busy) return;
        _checking = true; _error = null;
        try { _comfyCheck = (await Providers.CheckAsync(AiBackend.ComfyUI, _settings! with { ComfyUrl = _comfyUrl }, cancellationToken: _token)).Message; }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { _comfyCheck = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { _checking = false; }
    }
    private async Task CheckOpenRouterConnectionAsync()
    {
        if (Busy || _removeKey) return;
        _checking = true; _error = null;
        try { _openRouterCheck = (await Providers.CheckAsync(AiBackend.OpenRouter, _settings!, _replacementKey, _token)).Message; }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { _openRouterCheck = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { _checking = false; }
    }
    // Each workflow saves only its own fields; the default workflow is saved from Defaults.
    private AiSettings ApplyImageWorkflowDraft(AiSettings settings, ImageWorkflow workflow) => workflow switch
    {
        ImageWorkflow.CodexImages => settings with { Codex = settings.Codex with { ImageModel = _codexImageModel, ImageEffort = string.IsNullOrEmpty(_codexImageEffort) ? null : _codexImageEffort } },
        ImageWorkflow.QwenImage21 => settings with { QwenImage21 = _qwen },
        ImageWorkflow.Flux2Klein9bKv => settings with { FluxKleinModel = _fluxModel, FluxKleinTextEncoder = _fluxEncoder, FluxKleinVae = _fluxVae },
        _ => settings with { ComfyImageModel = _comfyImageModel, ComfyImageTextEncoder = _comfyImageEncoder, ComfyImageVae = _comfyImageVae, ComfyImageEditLora = _comfyImageEditLora }
    };
    // Availability checks read the workflow from DefaultImageWorkflow; check the one being edited.
    private AiSettings ImageDraft => ApplyImageWorkflowDraft(_settings!, _imageWorkflow) with { DefaultImageWorkflow = _imageWorkflow };
    private async Task SaveImageWorkflowAsync()
    {
        var workflow = _imageWorkflow;
        await SaveChangeAsync(settings => ApplyImageWorkflowDraft(settings, workflow), $"{workflow.Label()} saved.");
    }
    private async Task SaveImageDefaultsAsync()
    {
        if (await SaveChangeAsync(settings => settings with { DefaultImageWorkflow = _defaultImageWorkflow, ImageTimeoutSeconds = _imageTimeout }, "Image defaults saved."))
            CancelImageDefaults();
    }
    private async Task SaveComfyTextAsync() => await SaveChangeAsync(settings => settings with { Temperature = _temperature, MaxOutputTokens = _maxTokens }, "ComfyUI generation defaults saved.");
    private async Task SaveTextTimeoutAsync() => await SaveChangeAsync(settings => settings with { TimeoutSeconds = _timeout }, "Text timeout saved.");
    private async Task RefreshImagesAsync()
    {
        if (Busy) return;
        _checking = true; _error = null;
        try
        {
            if (IsCodex)
            {
                var check = await Providers.CheckAsync(AiBackend.Codex, _settings!, cancellationToken: _token);
                _checks[AiBackend.Codex] = check; _imageModels = check.Models; _imageEditCheck = check.Message; return;
            }
            var images = await ImageGenerator.CheckAsync(ImageDraft, _token);
            var edits = await ImageEditor.CheckAsync(ImageDraft, _token);
            _imageModels = images.DiffusionModels; _imageEncoders = images.TextEncoders; _imageVaes = images.Vaes; _imageEditLoras = edits.Loras;
            if (!IsQwen && _imageModels.Count > 0 && _imageModels.All(model => model.Id != ImageModel)) ImageModel = _imageModels[0].Id;
            if (!IsQwen && _imageEncoders.Count > 0 && _imageEncoders.All(model => model.Id != ImageEncoder)) ImageEncoder = _imageEncoders[0].Id;
            if (!IsQwen && _imageVaes.Count > 0 && _imageVaes.All(model => model.Id != ImageVae)) ImageVae = _imageVaes[0].Id;
            if (_imageEditLoras.Count > 0 && _imageEditLoras.All(model => model.Id != _comfyImageEditLora))
                _comfyImageEditLora = _imageEditLoras.FirstOrDefault(model => Path.GetFileName(model.Id).Equals("krea2_identity_edit_v1_2.safetensors", StringComparison.OrdinalIgnoreCase))?.Id ?? _imageEditLoras[0].Id;
            images = await ImageGenerator.CheckAsync(ImageDraft, _token);
            edits = await ImageEditor.CheckAsync(ImageDraft, _token);
            _imageEditCheck = $"{images.Message} {edits.Message} {edits.ReferenceCapabilityMessage}";
        }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException) { _error = e.Message; }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { _checking = false; }
    }
    public void Dispose() { if (_modelQueue is not null) _modelQueue.Changed -= ModelTestsChanged; _lifetime.Cancel(); _lifetime.Dispose(); }
}
