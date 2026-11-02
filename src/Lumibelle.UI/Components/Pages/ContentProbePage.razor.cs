using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace lumibelle.Components.Pages;

public partial class ContentProbePage
{
    [Inject] private IContentProbeStore Probes { get; set; } = null!;
    [Inject] private IAiSettingsStore SettingsStore { get; set; } = null!;
    [Inject] private IAiProviderRegistry Providers { get; set; } = null!;
    [Inject] private IAiJobStore Store { get; set; } = null!;
    [Inject] private ContentProbeCapture Capture { get; set; } = null!;
    [Inject] private AiJobCoordinator Jobs { get; set; } = null!;
    [Parameter, SupplyParameterFromQuery(Name = "jobId")] public Guid? JobId { get; set; }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    public ContentProbePage() { _token = _lifetime.Token; }
    private readonly Guid _tab = Guid.NewGuid();
    private readonly HashSet<Guid> _selectedTests = [], _confirmed = [], _ratingBusy = [];
    private readonly HashSet<string> _selectedModels = new(StringComparer.Ordinal);
    private readonly Dictionary<AiBackend, AiConnectionCheck> _checks = [];
    private readonly Dictionary<Guid, ContentProbeRequest> _requests = [];
    private ContentProbeLibrary? _library;
    private AiSettings? _settings;
    private bool _loading = true, _busy, _disposed, _rowsLoading, _rowsAgain;
    private string? _error, _message;
    private int _repetitions = 1, _page, _historyLimit = 200, _totalResponses;
    private const int PageSize = 20;
    private string _testFilter = "", _categoryFilter = "", _modelFilter = "", _outcomeFilter = "";
    private bool _unratedOnly, _hideModelNames;
    private IReadOnlyList<AiJobSubmission>? _plan;
    private IReadOnlyList<ContentProbeRequest> _plannedRequests = [];
    private List<ContentProbeRow> _rows = [];
    private List<string> _historyWarnings = [];
    private Guid? _openedJob, _lastQueryJob, _pendingQueryJob, _deleteProbe;
    private ContentProbeReview? _detailReview;
    private ElementReference _responseHeading;
    private bool _focusResponse;
    private string _notes = "";

    private ContentProbe? _editing;
    private string _editName = "", _editCategory = "", _editPrompt = "", _editCriteria = "", _editPhrases = "";
    private bool _editMarker = true;
    private bool _editorReadOnly => _editing is not null && ContentProbeBuiltIns.IsBuiltIn(_editing.Id);
    private string EditorPrompt => _editPrompt + (_editMarker ? "\n\n" + ContentProbePolicy.RefusalInstruction : "");
    private bool SelectionLocked => _busy || _plan is not null;
    private string? DeleteProbeName => Definitions.FirstOrDefault(p => p.Id == _deleteProbe)?.Name;
    private IReadOnlyList<ContentProbe> Definitions => _library is null ? [] : ContentProbeBuiltIns.All(_library);
    private IEnumerable<TextModelReference> ModelChoices => _settings is null ? [] : _settings.TextModelProfiles
        .Concat(_settings.StarredTextModels).Append(TextModelPolicy.Default(_settings))
        .Where(m => !string.IsNullOrWhiteSpace(m.Model)).DistinctBy(TextModelProfiles.ChoiceKey)
        .OrderBy(m => m.Backend).ThenBy(m => TextModelPolicy.DisplayName(m, _settings), StringComparer.OrdinalIgnoreCase);
    private long RequestCount => (long)_selectedTests.Count * _selectedModels.Count * _repetitions;
    private IReadOnlyList<ContentProbeRequest> PlannedRequests => _plannedRequests;
    private ContentProbeRow? OpenedRow => _rows.FirstOrDefault(r => r.Job.Id == _openedJob);
    private List<ContentProbeRow> FilteredRows => _rows.Where(r =>
        (_testFilter.Length == 0 || r.Request.Probe.Id.ToString() == _testFilter) &&
        (_categoryFilter.Length == 0 || r.Request.Probe.Category == _categoryFilter) &&
        (_modelFilter.Length == 0 || TextModelProfiles.ChoiceKey(r.Request.Model) == _modelFilter) &&
        (_outcomeFilter.Length == 0 || RowOutcome(r).ToString() == _outcomeFilter) &&
        (!_unratedOnly || ContentProbePolicy.CanRate(r) && ContentProbePolicy.CurrentScore(r) is null)).ToList();
    private int PageCount => Math.Max(1, (FilteredRows.Count + PageSize - 1) / PageSize);
    private IEnumerable<ContentProbeRow> PagedRows => FilteredRows.Skip(Math.Min(_page, PageCount - 1) * PageSize).Take(PageSize);
    private void ResetPage() => _page = 0;
    private static ContentProbeOutcome RowOutcome(ContentProbeRow row) => row.Job.State == AiJobState.Completed && !row.Job.CancelRequested && !row.Job.RemoteUnconfirmed
        ? ContentProbePolicy.Outcome(row.Request, row.Result) : ContentProbeOutcome.Inconclusive;
    private static string Status(ContentProbeRow row) => row.Job.State switch
    {
        AiJobState.Waiting => "Queued",
        AiJobState.Running => "Running",
        AiJobState.Cancelled => "Cancelled · inconclusive",
        AiJobState.NeedsAttention => "Needs attention · inconclusive",
        _ => ContentProbePolicy.Label(RowOutcome(row))
    };
    private static string Preview(ContentProbeRow row)
    {
        var text = string.IsNullOrWhiteSpace(row.Result?.Raw) ? row.Result?.ApiRefusal : row.Result.Raw;
        return string.IsNullOrWhiteSpace(text) ? "No response text saved." : text.Length > 240 ? text[..240] + "…" : text;
    }
    private string ModelLabel(TextModelReference model)
    {
        if (!_hideModelNames) return model.Backend + " · " + model.Name;
        var keys = _rows.Select(r => TextModelProfiles.ChoiceKey(r.Request.Model)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return "Configuration " + (Array.IndexOf(keys, TextModelProfiles.ChoiceKey(model)) + 1);
    }
    private string Availability(TextModelReference model) => _settings is null || !_checks.TryGetValue(model.Backend, out var check)
        ? "Availability not checked" : TextModelPolicy.Issue(TextModelPolicy.WithDefaultEffort(model, _settings), _settings, check) ?? "Available";

    protected override async Task OnInitializedAsync()
    {
        Jobs.Changed += QueueChanged;
        await ReloadLibraryAsync();
        await RefreshRowsAsync();
    }
    protected override async Task OnParametersSetAsync()
    {
        if (JobId is not { } id || id == _lastQueryJob || _disposed) return;
        _lastQueryJob = id; _pendingQueryJob = id;
        await RefreshRowsAsync();
    }
    private void QueueChanged()
    {
        if (_disposed) return;
        _ = InvokeAsync(async () =>
        {
            if (_disposed) return;
            await RefreshRowsAsync();
            if (!_disposed) StateHasChanged();
        });
    }
    private async Task ActionAsync(Func<Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true; _error = null;
        try { await action(); }
        catch (WorkspaceConflictException) { _error = "Another tab changed these settings. Reload saved tests and profiles, then review your draft before retrying."; }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException or IOException or JsonException) { _error = e.Message; }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _busy = false; }
    }
    private async Task ReloadLibraryAsync()
    {
        if (SelectionLocked) return;
        await ActionAsync(async () =>
        {
            _loading = true;
            try
            {
                _library = await Probes.LoadLibraryAsync(_token);
                _settings = await SettingsStore.LoadAsync(_token);
                _checks.Clear();
                _selectedTests.IntersectWith(Definitions.Where(p => p.Enabled).Select(p => p.Id));
                _selectedModels.IntersectWith(ModelChoices.Select(TextModelProfiles.ChoiceKey));
            }
            finally { _loading = false; }
        });
    }
    private void SelectTest(Guid id, ChangeEventArgs args)
    {
        if (SelectionLocked) return;
        if (args.Value is true) _selectedTests.Add(id); else _selectedTests.Remove(id);
    }
    private void SelectModel(string key, ChangeEventArgs args)
    {
        if (SelectionLocked) return;
        if (args.Value is true) _selectedModels.Add(key); else _selectedModels.Remove(key);
    }
    private void SelectEnabledTests()
    {
        if (SelectionLocked) return;
        _selectedTests.UnionWith(Definitions.Where(p => p.Enabled).Select(p => p.Id));
    }
    private void NewProbe() { if (!SelectionLocked) EditProbe(new() { Name = "New test" }); }
    private void EditProbe(ContentProbe probe)
    {
        if (SelectionLocked) return;
        _editing = ContentProbePolicy.Copy(probe); _editName = probe.Name; _editCategory = probe.Category;
        _editPrompt = probe.Prompt; _editCriteria = probe.SuccessCriteria;
        _editPhrases = string.Join("\n", probe.RequiredPhrases); _editMarker = probe.UseRefusalMarker;
    }
    private void DuplicateProbe(ContentProbe probe) => EditProbe(ContentProbePolicy.Copy(probe) with
    { Id = Guid.NewGuid(), Revision = 1, Name = probe.Name[..Math.Min(probe.Name.Length, 110)] + " copy", Enabled = true });
    private async Task SaveProbeAsync()
    {
        if (SelectionLocked || _editing is null || _editorReadOnly || _library is null) return;
        var probe = _editing with { Name = _editName, Category = _editCategory, Prompt = _editPrompt, SuccessCriteria = _editCriteria,
            RequiredPhrases = _editPhrases.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), UseRefusalMarker = _editMarker };
        await ActionAsync(async () =>
        {
            _library = await Probes.SaveProbeAsync(probe, _library.Revision, _token);
            _editing = null; _message = "Test saved. Existing responses and ratings are unchanged.";
        });
    }
    private async Task EnableProbeAsync(ContentProbe probe)
    {
        if (SelectionLocked || _library is null) return;
        await ActionAsync(async () =>
        {
            _library = await Probes.SetEnabledAsync(probe.Id, !probe.Enabled, _library.Revision, _token);
            if (probe.Enabled) _selectedTests.Remove(probe.Id);
            _message = probe.Enabled ? "Test disabled. Saved responses remain." : "Test enabled.";
        });
    }
    private async Task DeleteProbeAsync(Guid id)
    {
        if (SelectionLocked || _library is null) return;
        await ActionAsync(async () =>
        {
            _library = await Probes.DeleteProbeAsync(id, _library.Revision, _token);
            _selectedTests.Remove(id); _deleteProbe = null;
            if (_editing?.Id == id) _editing = null;
            _message = "Custom test deleted. Its saved responses and ratings remain.";
        });
    }
    private async Task CheckModelsAsync()
    {
        if (SelectionLocked || _settings is null) return;
        await ActionAsync(async () =>
        {
            foreach (var backend in ModelChoices.Select(m => m.Backend).Distinct())
                _checks[backend] = await Providers.CheckAsync(backend, _settings, cancellationToken: _token);
            _message = "Availability checked. No text was generated.";
        });
    }
    private async Task PrepareAsync()
    {
        if (SelectionLocked || _settings is null || _library is null) return;
        var models = ModelChoices.Where(m => _selectedModels.Contains(TextModelProfiles.ChoiceKey(m))).ToArray();
        await ActionAsync(async () =>
        {
            InstallPlan(await Capture.PrepareAsync(Guid.NewGuid(), _tab, _selectedTests.ToArray(), models, _repetitions,
                _library.Revision, _settings.Revision, _token));
            _message = "Run prepared. Review its captured prompts and configurations, then choose Run. No text has been generated.";
        });
    }
    private void InstallPlan(IReadOnlyList<AiJobSubmission> plan)
    {
        _plan = plan; _confirmed.Clear();
        _plannedRequests = plan.Select(s => s.Snapshot.Deserialize<ContentProbeRequest>(AtomicJsonFile.Options)!).ToArray();
    }
    private async Task EnqueuePlanAsync()
    {
        if (_plan is null) return;
        var plan = _plan;
        await ActionAsync(async () =>
        {
            try
            {
                foreach (var submission in plan)
                {
                    if (_confirmed.Contains(submission.Id)) continue;
                    await Jobs.EnqueueAsync(submission, _token);
                    _confirmed.Add(submission.Id);
                }
                _message = $"Queued {plan.Count} test requests. Follow progress and rate completed responses below.";
                _plan = null; _plannedRequests = []; _confirmed.Clear();
            }
            catch (WorkspaceStoreException e)
            {
                throw new WorkspaceStoreException($"Queueing stopped after {_confirmed.Count} confirmed requests. Accepted requests may already be running. Continue queueing retries these same IDs without duplicating them. {e.Message}", e);
            }
            finally { await RefreshRowsAsync(); }
        });
    }
    private void DiscardPlan()
    {
        if (_busy) return;
        _plan = null; _plannedRequests = []; _confirmed.Clear();
        _message = "Unqueued plan discarded. Already queued requests were not cancelled.";
    }
    private void PrepareRepeat(ContentProbeRow row)
    {
        if (SelectionLocked || row.Job.LocksTarget || row.Job.RemoteUnconfirmed) return;
        InstallPlan([ContentProbeCapture.Repeat(row.Request, _tab)]);
        _message = "Repeat prepared with the original prompt, criteria, and configuration, and a fresh seed. Review the captured run above before queueing.";
    }
    private Task CancelJobAsync(Guid id) => ActionAsync(async () => { await Jobs.CancelAsync(id, _token); await RefreshRowsAsync(); });
    private Task RecoverJobAsync(Guid id) => ActionAsync(async () => { await Jobs.ResumeAsync(id, _token); await RefreshRowsAsync(); });

    private async Task RefreshRowsAsync()
    {
        _rowsAgain = true;
        if (_rowsLoading || _disposed) return;
        _rowsLoading = true;
        try
        {
            do
            {
                _rowsAgain = false;
                var headers = (await Store.ReadAsync(_token)).Jobs.Where(j => j.Kind == AiJobKind.ContentProbe)
                    .OrderByDescending(j => j.CreatedUtc).ThenByDescending(j => j.Id).ToArray();
                _totalResponses = headers.Length;
                var shown = headers.Take(_historyLimit).Concat(headers.Where(j => j.Id == JobId || j.Id == _openedJob)).DistinctBy(j => j.Id).ToArray();
                var rows = new List<ContentProbeRow>(); var warnings = new List<string>();
                foreach (var job in shown)
                {
                    try
                    {
                        if (!_requests.TryGetValue(job.Id, out var request))
                        {
                            request = ContentProbePolicy.Read(job, await Store.ReadSnapshotAsync(job.Id, _token));
                            _requests[job.Id] = request;
                        }
                        var result = await Store.ReadArtifactAsync<ContentProbeResult>(job.Id, AiJobArtifact.Result, _token);
                        var review = await Probes.LoadReviewAsync(job.Id, _token);
                        var previous = _rows.FirstOrDefault(r => r.Job.Id == job.Id);
                        // A slow file read must not overwrite a just-saved local review.
                        if (previous is not null && previous.Review.Revision > review.Revision) review = previous.Review;
                        rows.Add(previous is not null && previous.Job.Version > job.Version ? previous : new(job, request, result, review));
                    }
                    catch (Exception e) when (e is WorkspaceStoreException or JsonException or IOException)
                    { warnings.Add($"Could not read test {job.Id}: {e.Message}"); }
                }
                _rows = rows; _historyWarnings = warnings;
                if (_pendingQueryJob is { } requested && rows.FirstOrDefault(r => r.Job.Id == requested) is { } selected)
                { _pendingQueryJob = null; OpenResponse(selected); }
                _page = Math.Min(_page, PageCount - 1);
            } while (_rowsAgain && !_disposed);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) when (e is WorkspaceStoreException or IOException or JsonException) { _error = e.Message; }
        finally { _rowsLoading = false; }
    }
    private async Task LoadOlderAsync() { _historyLimit = checked(_historyLimit + 200); await RefreshRowsAsync(); }
    private void OpenResponse(ContentProbeRow row)
    {
        _openedJob = row.Job.Id; _notes = row.Review.Notes; _focusResponse = true;
        _detailReview = row.Review with { Score = ContentProbePolicy.CurrentScore(row),
            OutputFingerprint = row.Result is null ? "" : ContentProbePolicy.OutputFingerprint(row.Result) };
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusResponse || _disposed || OpenedRow is null) return;
        _focusResponse = false;
        try { await _responseHeading.FocusAsync(); }
        catch (JSDisconnectedException) { }
    }
    private void CloseResponse() { _openedJob = null; _detailReview = null; _notes = ""; _focusResponse = false; }
    private async Task ReloadReviewDraftAsync(Guid id)
    {
        await RefreshRowsAsync();
        if (_rows.FirstOrDefault(r => r.Job.Id == id) is { } row) OpenResponse(row);
    }
    private Task RateAsync(ContentProbeRow row, int? score) => SaveRatingAsync(row, row.Review with
    { Score = score, OutputFingerprint = row.Result is null ? "" : ContentProbePolicy.OutputFingerprint(row.Result) }, false);
    private Task RateDetailAsync(int? score) => OpenedRow is { } row && _detailReview is { } draft
        ? SaveRatingAsync(row, draft with { Score = score, Notes = _notes }, true) : Task.CompletedTask;
    private Task SaveNotesAsync() => RateDetailAsync(_detailReview?.Score);
    private async Task SaveRatingAsync(ContentProbeRow row, ContentProbeReview draft, bool detail)
    {
        if (_disposed || !ContentProbePolicy.CanRate(row) || !_ratingBusy.Add(row.Job.Id)) return;
        _error = null;
        try
        {
            var saved = await Probes.SaveReviewAsync(draft, _token);
            _rows = _rows.Select(r => r.Job.Id == row.Job.Id ? r with { Review = saved } : r).ToList();
            if (detail && _openedJob == row.Job.Id) { _detailReview = saved; _notes = saved.Notes; }
            _message = saved.Score is null ? "Review saved without a rating." : $"Rating {saved.Score}/5 saved.";
        }
        catch (WorkspaceConflictException)
        { _error = "Another tab rated this response. Your review draft is still here. Refresh responses or discard the review draft and reload before retrying."; }
        catch (Exception e) when (e is WorkspaceStoreException or IOException) { _error = e.Message; }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _ratingBusy.Remove(row.Job.Id); }
    }
    public void Dispose()
    {
        _disposed = true; Jobs.Changed -= QueueChanged;
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}
