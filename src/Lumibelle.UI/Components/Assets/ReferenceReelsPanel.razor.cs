using lumibelle.Components.AI;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using Microsoft.JSInterop;

namespace lumibelle.Components.Assets;

public partial class ReferenceReelsPanel
{
    [Inject] public NavigationManager Navigation { get; set; } = null!;
    [Parameter] public bool Embedded { get; set; }
    [Parameter] public object? DetailsSection { get; set; }
    [Parameter] public Guid? SelectedReelId { get; set; }
    private AssetReferenceReel? SelectedReel => Library.Reels.FirstOrDefault(r => r.Id == SelectedReelId && r.AssetId == AssetId);
    private bool _renameDetails, _confirmDetailsClose;
    private bool DetailsDirty => _details is { } d && (d.Name != _detailName || d.UseGuidance != _detailGuidance || d.LookId != _detailLook);
    private static readonly DialogOptions DetailOptions = new() { MaxWidth = MaxWidth.Large, FullWidth = true, CloseOnEscapeKey = false, BackdropClick = false };
    private Guid? _detailLook;
    [Parameter] public Guid ProjectId { get; set; }
    [Parameter] public Guid AssetId { get; set; }
    [Parameter] public Guid? RequestedJobId { get; set; }
    [Parameter, EditorRequired] public object ToolsSection { get; set; } = null!;
    [Parameter] public EventCallback<AssetReferenceReel> SaveVoiceRequested { get; set; }
    [Parameter] public EventCallback RevealTools { get; set; }
    [Parameter] public EventCallback RevealPrompts { get; set; }
    [Parameter] public Action<ReferenceReelsPanel>? RegisterPanel { get; set; }
    [Parameter] public EventCallback<bool> ClearCreationAvailabilityChanged { get; set; }
    [Parameter] public Action<Func<Task<bool>>>? RegisterFlush { get; set; }
    [Parameter, EditorRequired] public AssetLibrary Library { get; set; } = null!;
    [Parameter, EditorRequired] public Func<Func<long, Task<AssetLibrary>>, Task> Mutate { get; set; } = null!;
    private static readonly DialogOptions EditorOptions = new() { MaxWidth = MaxWidth.Medium, FullWidth = true, CloseOnEscapeKey = true, BackdropClick = false };
    private static readonly DialogOptions WideOptions = new() { MaxWidth = MaxWidth.ExtraLarge, FullWidth = true, CloseOnEscapeKey = true, BackdropClick = false };
    private readonly SemaphoreSlim _saveGate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private ReferenceReelDraft? _draft, _saved;
    private ShotVideoBinding? _previewReferenceVideo;
    private RenderFragment? _toolsContent;
    private CancellationTokenSource? _preparation;
    private bool _preparing;
    private PromptEditor? _prompt;
    private TextAssistance? _assist;
    private TextModelSelectionState? _model;
    private bool _busy, _saving, _saveFailed, _disposed, _picturesOpen, _reviewOpen, _importOpen;
    private string _filter = "all", _raw = "", _takeId = "", _importName = "", _importGuidance = ShotVideoBinding.DefaultDescription;
    private string _detailName = "", _detailGuidance = "";
    private string _directionSourceId = "";
    private string? _directionsNotice, _directionsCopyNotice;
    private IEnumerable<AssetReferenceReel> ReusableDirections => Library.Reels.Where(r => r.Generation is { } g &&
        (ReferenceReels.IsEnvironment(g.Recipe) || ReferenceReels.IsCharacterCapture(g.Recipe) || !string.IsNullOrWhiteSpace(g.Recipe.Instructions)) && g.Recipe.PresetVersion == _draft?.PresetVersion &&
        ReferenceReels.Framings(_draft!).Contains(g.Recipe.Framing)).OrderByDescending(r => r.CreatedUtc).ThenBy(r => r.Id);
    private AssetReferenceReel? DirectionSource => ReusableDirections.FirstOrDefault(r => r.Id.ToString() == _directionSourceId);
    private string DirectionSourceLabel(AssetReferenceReel reel) => $"{Library.Assets.FirstOrDefault(a => a.Id == reel.AssetId)?.Name ?? "Saved asset"} · {reel.Name} · {reel.CreatedUtc.ToLocalTime():g}";
    private static string DirectionText(ReferenceReelDraft recipe) => string.IsNullOrWhiteSpace(recipe.Instructions) && (ReferenceReels.IsEnvironment(recipe) || ReferenceReels.IsCharacterCapture(recipe))
        ? ReferenceReels.Views(recipe) : recipe.Instructions;
    private Task ReuseDirections() => Run(async () => {
        if (_draft is null || DirectionSource?.Generation?.Recipe is not { } source) return;
        await Flush();
        ReferenceReels.ReuseDirections(_draft, source);
        await Save();
        _directionsNotice = "Directions loaded. Review them for these pictures, then compose or revise the prompt pair.";
    });
    private async Task CopyDirections(string text)
    {
        try { await JS.InvokeVoidAsync("navigator.clipboard.writeText", text); _directionsCopyNotice = "Copied. Paste into Instructions in another reel's Assist."; }
        catch (JSException) { _directionsCopyNotice = "Select and copy the saved directions."; }
    }
    private string? _error, _reviewError, _saveError;
    private Shot? _pictureDraft;
    private Guid? _pictureExpanded;
    private ReelPromptPair? _pair, _unappliedPair;
    private ReferenceReelDraft? _responseDraft;
    private string? _copyNotice;
    private AiJobHeader? _reviewJob;
    private AiTextJobRequest? _reviewRequest;
    private AiVideoJobRequest? _videoRequest;
    private AiJobHeader? _videoJob;
    private AssetReferenceReel? _details, _keyframeReel;
    private AssetReferenceReel? _regenerateReel;
    public void Regenerate(AssetReferenceReel reel) { if (_busy || reel.Generation is null) return; _regenerateReel = reel; StateHasChanged(); }
    public Task ManageKeyframes(AssetReferenceReel reel) { _keyframeReel = reel; StateHasChanged(); return Task.CompletedTask; }
    private AssetReferenceReel? _replaceReel;
    public bool CanReplace(AssetReferenceReel reel) => Library.Reels.Any(r => r.AssetId == reel.AssetId && r.Media.Id != reel.Media.Id);
    public void ReplaceInShots(AssetReferenceReel reel) { if (_busy) return; _replaceReel = reel; StateHasChanged(); }
    private async Task SaveKeyframes(ReelKeyframeSet frames)
    {
        var before = _keyframeReel!;
        await Mutate(revision => Reels.SaveKeyframesAsync(ProjectId, before, frames, revision));
        if (_details?.Id == before.Id) _details = _details with { Keyframes = ShotCopy.Of(frames) };
        _keyframeReel = null;
    }
    private Task RestoreTrashedImage(Guid trashId) => Mutate(revision => ImageTrash.RestoreImagesAsync(ProjectId, [trashId], revision, _lifetime.Token));
    private async Task<SavedAssetImage> SaveFrameImage(DerivedImageRequest request)
    {
        SavedAssetImage? result = null;
        await Mutate(async revision => {
            result = await AssetStore.SaveDerivedImageAsync(ProjectId, request, revision, _lifetime.Token);
            return result.Library;
        });
        return result!;
    }
    private Task ChangeKeepFrames(ChangeEventArgs e) => EditPreset(p => p.Settings.SaveLosslessFrames = e.Value is true);
    private async Task ChangeResolution(ChangeEventArgs e)
    {
        var upscale = e.Value?.ToString() == "preview-upscale";
        if (_draft is null || !VideoResolutions.TryParse(upscale ? "preview" : e.Value?.ToString(), out var resolution)) return;
        _draft.OutputOverrides ??= new(); _draft.OutputOverrides.Resolution = resolution; _draft.OutputOverrides.UpscalePreview = upscale;
        VideoResolutions.Select(_draft, resolution); await Changed();
    }

    private ReferenceVideoMedia? _imported;
    private Guid? _importTake, _handledJob, _openedRequestedJob;
    private Guid _importId;
    private DateTimeOffset _importTime;
    private ElementReference _voicePlayer;
    private IJSObjectReference? _voiceModule;
    private List<ShotTake> _takes = [];
    private AiJobSubmission? _enqueue;
    private bool _pendingText;
    private ReferenceAsset Owner => Library.Assets.First(a => a.Id == AssetId);
    private bool EnvironmentReel => Owner.Category == AssetCategory.Environment;
    private bool PropReel => Owner.Category == AssetCategory.Prop;
    // Environment and prop reels are silent camera studies without looks or voices.
    private bool CameraReel => EnvironmentReel || PropReel;
    private string? InstructionsIssue => _draft is null ? null : ReferenceReels.CompositionInstructionsIssue(_draft);
    private IEnumerable<AssetReferenceReel> VisibleReels => Library.Reels.Where(r => r.AssetId == AssetId &&
        (_filter == "all" || _filter == "general" && r.LookId is null || r.LookId?.ToString() == _filter)).OrderByDescending(r => r.CreatedUtc).ThenBy(r => r.Id);
    private IEnumerable<AiJobHeader> RelevantJobs => Jobs.View.Jobs.Where(j => j.Target.ProjectId == ProjectId && j.Target.AssetId == AssetId &&
        j.Kind is AiJobKind.ReelComposition or AiJobKind.ReelVideo).OrderByDescending(j => j.CreatedUtc);
    private bool _compositionSubmitting;
    private AiJobHeader? CompositionJob => RelevantJobs.FirstOrDefault(j => j.Kind == AiJobKind.ReelComposition && j.Target.ReelId == _draft?.Id && j.Id == _draft?.PendingJobId);
    private TextRequestPresentation? ReelCompositionPresentation => CompositionJob is { } job ? new(job, "Writing prompt pair…",
        _draft!.ResolvedJobs.Contains(job.Id) ? TextRequestOutcome.Resolved : TextRequestOutcome.Proposal, "Prompt pair") : null;
    // Starts over in the composer; the reviewed pair stays to apply or discard later.
    private async Task NewPairFromReview() { _reviewOpen = false; if (_assist is not null) await _assist.NewRequestAsync(); }
    private Task InspectComposition() => CompositionJob is { } job ? ViewJob(job) : Task.CompletedTask;
    private Task CancelComposition() => CompositionJob is { } job ? Jobs.CancelAsync(job.Id) : Task.CompletedTask;
    private AiJobHeader? ActiveComposition => RelevantJobs.FirstOrDefault(j => j.Kind == AiJobKind.ReelComposition && j.Target.ReelId == _draft?.Id && j.LocksTarget);
    private AiJobHeader? LatestVideo => RelevantJobs.FirstOrDefault(j => j.Kind == AiJobKind.ReelVideo && j.Target.ReelId == _draft?.Id);
    private AiJobHeader? ActiveVideo => RelevantJobs.FirstOrDefault(j => j.Kind == AiJobKind.ReelVideo && (j.State is AiJobState.Waiting or AiJobState.Running || j.RemoteUnconfirmed));
    private AiJobHeader? DisplayVideo => ActiveVideo ?? LatestVideo ?? RelevantJobs.FirstOrDefault(j => j.Id == _completedVideoId);
    private Guid? _completedVideoId;
    // Starting fresh after a finished reel replaces _draft once the store has saved the new recipe.
    private Task _reset = Task.CompletedTask;
    private bool _resettingDraft => !_reset.IsCompleted;
    private bool? _lastClearAvailability;
    public bool CanClearCreation => _draft is not null && !_disposed && !_busy && !_presetBusy && !_saving && !_preparing && !_resettingDraft &&
        !_picturesOpen && !_reviewOpen && !_setupDialogOpen && _enqueue is null && ActiveComposition is null && ActiveVideo is null;
    public async Task ClearCreationAsync()
    {
        if (!CanClearCreation) throw new WorkspaceStoreException("Finish the current reel operation before clearing its draft.");
        _busy = true;
        try
        {
            var blank = ReferenceReels.ClearDraftContent(_draft!);
            ReferenceReelDraft? saved = null;
            await Mutate(async _ => {
                saved = await Reels.SaveDraftAsync(ProjectId, blank, 0, _lifetime.Token);
                return await AssetStore.LoadAsync(ProjectId, _lifetime.Token);
            });
            _draft = saved!; _saved = saved!.Copy(); _saveFailed = false;
            _directionSourceId = ""; _directionsNotice = null; _directionsCopyNotice = null;
            _unappliedPair = null; _responseDraft = null; _copyNotice = null; _error = null; _completedVideoId = null;
        }
        finally { _busy = false; await InvokeAsync(StateHasChanged); }
    }
    private string GenerateLabel => _preparing ? "Preparing request…" : ActiveVideo is { CancelRequested: true } ? "Cancellation requested…"
        : ActiveVideo?.State == AiJobState.Waiting ? "Queued…" : ActiveVideo is not null ? "Generating…" : "Generate reel";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await RestoreSetupFocus();
        if (_lastClearAvailability != CanClearCreation)
        {
            _lastClearAvailability = CanClearCreation;
            await ClearCreationAvailabilityChanged.InvokeAsync(_lastClearAvailability.Value);
        }
        // A request link is a one-time instruction. Once its dialog closes,
        // remove it so switching assets cannot replay it in a new keyed panel.
        // Wait until after rendering: Apply/Edit response may still be releasing
        // their busy state, and URL changes use the normal draft-save guard.
        if (!_disposed && !_busy && _openedRequestedJob is { } opened && !_reviewOpen && _videoRequest is null)
        {
            _openedRequestedJob = null;
            if (RequestedJobId == opened)
            {
                Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("jobId", (string?)null), replace: true);
                return;
            }
        }
        if (_disposed || _busy || _presetBusy || _saving || _resettingDraft || _picturesOpen || _reviewOpen || _enqueue is not null || ActiveComposition is not null ||
            LatestVideo is not { State: AiJobState.Completed, CancelRequested: false } job || _draft!.ResolvedJobs.Contains(job.Id) ||
            Library.ReelDrafts.LastOrDefault(d => d.AssetId == AssetId)?.Id != _draft.Id) return;
        var submitted = Library.Reels.Concat(Library.ReelTrash.Select(t => t.Reel)).FirstOrDefault(r => r.Generation?.JobId == job.Id)?.Generation?.Recipe;
        if (submitted is null || ReferenceReels.Fingerprint(_draft) != ReferenceReels.Fingerprint(submitted)) return;
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _reset = reset.Task;
        try {
            // Flush browser typing before comparing with the immutable request.
            await Flush();
            if (_disposed || _busy || _draft.Id != submitted.Id || ReferenceReels.Fingerprint(_draft) != ReferenceReels.Fingerprint(submitted)) return;
            ReferenceReelDraft? fresh = null;
            await Mutate(async _ => { fresh = await Reels.StartFreshAfterSuccessAsync(job, submitted, _lifetime.Token); return await AssetStore.LoadAsync(ProjectId, _lifetime.Token); });
            if (fresh is not null && !_disposed && _draft.Id == submitted.Id && ReferenceReels.Fingerprint(_draft) == ReferenceReels.Fingerprint(submitted)) {
                _draft = fresh; _saved = fresh.Copy(); _completedVideoId = job.Id;
                _directionSourceId = ""; _directionsNotice = null;
                StateHasChanged();
            }
        } catch (Exception e) { if (!_disposed) _error = e.Message; }
        finally { reset.SetResult(); }
    }
    private string FramingPlan { get { try { return _draft is null ? "" : ReferenceReels.Views(_draft); } catch (WorkspaceStoreException e) { return e.Message; } } }
    private bool HasPromptPair => _draft is not null && !string.IsNullOrWhiteSpace(_draft.Prompt) && !string.IsNullOrWhiteSpace(_draft.UseGuidance);
    private string? ValidationIssue { get { if (_draft is null || _draft.Prompt.Length == 0) return null; try { ReferenceReels.Validate(_draft, true); return null; } catch (WorkspaceStoreException e) { return e.Message; } } }
    protected override void OnInitialized() { _toolsContent = ReelTools; RegisterPanel?.Invoke(this); RegisterFlush?.Invoke(FlushForNavigation); Jobs.Changed += JobsChanged; }
    protected override async Task OnParametersSetAsync()
    {
        if (!_presetsReady) return;
        if (_draft is null)
        {
            var latest = Library.ReelDrafts.LastOrDefault(d => d.AssetId == AssetId &&
                (d.LookId is null || Owner.Looks.Any(l => l.Id == d.LookId && !l.Archived)));
            LoadDraft(latest?.LookId);
        }
        // A save publishes its captured library before Save updates _saved. An edit
        // made meanwhile can match the old baseline (e.g. toggling a mode back).
        // Do not treat our own in-flight save notification as a newer browser draft.
        if (!_saving && _draft is not null && _saved is not null && ReferenceReels.Fingerprint(_draft) == ReferenceReels.Fingerprint(_saved) &&
            Library.ReelDrafts.FirstOrDefault(d => d.Id == _draft.Id) is { } newer && newer.Revision > _saved.Revision)
        { _draft = newer.Copy(); _saved = newer.Copy(); }
        if (RequestedJobId is null) _handledJob = null;
        if (RequestedJobId is { } id && _handledJob != id && RelevantJobs.FirstOrDefault(j => j.Id == id) is { } requested)
        { _handledJob = id; await ViewJob(requested); _openedRequestedJob = id; }
    }
    private void JobsChanged()
    {
        if (!_disposed) _ = InvokeAsync(async () => {
            try {
                await Mutate(_ => AssetStore.LoadAsync(ProjectId, _lifetime.Token));
                if (_videoJob is not null) _videoJob = RelevantJobs.FirstOrDefault(j => j.Id == _videoJob.Id) ?? _videoJob;
                if (_reviewOpen && _reviewJob is not null && RelevantJobs.FirstOrDefault(j => j.Id == _reviewJob.Id) is { } updated &&
                    (updated.State != _reviewJob.State || updated.CancelRequested != _reviewJob.CancelRequested))
                    await ViewJob(updated);
            } catch (Exception e) { if (!_disposed) _error = e.Message; }
            if (!_disposed) StateHasChanged();
        });
    }
    private void LoadDraft(Guid? look)
    {
        var saved = Library.ReelDrafts.LastOrDefault(d => d.AssetId == AssetId && d.LookId == look);
        _draft = saved?.Copy() ?? ReferenceReels.NewDraft(Owner, look, Library);
        if (saved is null && (_globalSetups.Setups.FirstOrDefault(s => s.Id == _globalSetups.SelectedId && !s.Archived)
            ?? _globalSetups.Setups.FirstOrDefault(s => !s.Archived)) is { } preset)
            ReelGenerationSetups.Apply(_draft, preset);
        // Opening the workspace alone must not persist an empty recipe.
        _saved = _draft.Copy();
    }
    private Task ChangeLook(ChangeEventArgs e) => Run(async () =>
    {
        await Flush(); await LoadPresets();
        LoadDraft(Guid.TryParse(e.Value?.ToString(), out var id) ? id : null);
        if (_draft!.Revision == 0) _saved = null;
        await Save();
    });
    public async Task<bool> FlushForNavigation()
    {
        if (_disposed) return true;
        if (_preparing) { _error = "Finish or cancel request preparation before leaving this asset."; return false; }
        return await FlushForClose() && await FlushDetailsAsync();
    }
    private async Task EditCurrentRecipe() { _reviewOpen = false; await RevealPrompts.InvokeAsync(); await OpenSetupDialog("Prompt"); }
    private async Task<bool> FlushForClose() { try { await Flush(); return true; } catch (Exception e) { _error = e.Message; return false; } }
    private async Task Flush()
    {
        await _presetGate.WaitAsync(_lifetime.Token);
        try { if (_prompt is not null) await _prompt.FlushAsync(); await Save(); }
        finally { _presetGate.Release(); }
    }
    private async Task Changed() { try { await Save(); } catch (Exception e) { _error = e.Message; } }
    private Task ChangeFraming(ChangeEventArgs e) => Run(async () => {
        if (_draft is null || !Enum.TryParse<ReelFraming>(e.Value?.ToString(), out var framing)) return;
        await Flush();
        if (CameraReel) ReferenceReels.SelectCameraPreset(_draft, framing);
        else ReferenceReels.SelectCharacterPreset(_draft, framing);
        await Save();
    });
    private async Task PromptChanged(string text) { if (_draft is null || _draft.Prompt == text) return; _draft.Prompt = text; await Changed(); }
    private async Task Save()
    {
        await _saveGate.WaitAsync();
        try
        {
            if (_draft is null || _saved is not null && ReferenceReels.Fingerprint(_draft) == ReferenceReels.Fingerprint(_saved) &&
                _draft.PendingJobId == _saved.PendingJobId && _draft.ResolvedJobs.SequenceEqual(_saved.ResolvedJobs)) { SaveSucceeded(); return; }
            _draft.SaveLosslessFrames ??= true;
            _saving = true; var captured = _draft.Copy(); ReferenceReelDraft? saved = null;
            await Mutate(async _ => { saved = await Reels.SaveDraftAsync(ProjectId, captured, _saved?.Revision ?? 0, _lifetime.Token); return await AssetStore.LoadAsync(ProjectId, _lifetime.Token); });
            if (_draft.Id == captured.Id) { _draft.Revision = saved!.Revision; _saved = saved.Copy(); }
            SaveSucceeded();
        }
        catch (Exception e) { _saveFailed = true; _saveError = e.Message; throw; }
        finally { _saving = false; _saveGate.Release(); }
    }
    // Correcting a recipe, or returning it to what is already saved, clears the error its failed save reported; other errors stay.
    private void SaveSucceeded()
    {
        if (_saveError is not null && _error == _saveError) _error = null;
        _saveError = null; _saveFailed = false;
    }
    private async Task Run(Func<Task> action)
    { if (_busy) return; _busy = true; _error = null; try { await action(); } catch (Exception e) { _error = e.Message; } finally { _busy = false; } }
    private AssetImage? PictureMedia(ShotImageBinding image) => Library.Assets.FirstOrDefault(a => a.Id == image.AssetId)?.Images.FirstOrDefault(i => i.Id == image.MediaId);
    private async Task OpenPictures(Guid? expanded = null)
    {
        // A dialog opened while the recipe starts fresh must copy the new recipe,
        // or Apply would carry the finished recipe's inputs into it.
        await _reset;
        if (_disposed) return;
        _pictureDraft = ReferenceReels.Inputs(_draft!); _pictureExpanded = expanded; _picturesOpen = true;
    }
    private Task ApplyPictures(Shot shot) => Run(async () => {
        _draft!.Images = ShotCopy.Of(shot.Images);
        _draft.KeyframeReels = shot.Videos.Count == 0 ? null : ShotCopy.Of(shot.Videos);
        await Save(); _picturesOpen = false;
    });
    private async Task ChangeVoiceDescription(ChangeEventArgs e)
    { var text = e.Value?.ToString(); _draft!.VoiceDescription = string.IsNullOrWhiteSpace(text) ? null : text; await Changed(); }
    private Task ChangeQuality(ChangeEventArgs e) => EditPreset(p => {
        var key = e.Value?.ToString() ?? "standard"; p.Settings.GenerationPreset = key;
        p.Settings.Turbo = key is "turbo4" or "turbo8"; p.Settings.TurboSteps = key == "turbo8" ? 8 : 4;
    });
    private async Task UseDefaultVoice() {
        if (_draft is null || CharacterVoices.Default(Owner, Library) is not { } voice) return;
        _draft.VoiceMode = ReelVoiceMode.ExistingRecording;
        _draft.Voice = new() { AssetId = voice.AssetId, VoiceId = voice.Id, Speaker = _draft.Speaker, Start = voice.Start, Duration = voice.ExcerptDuration };
        await Changed();
    }
    private async Task SelectVoice(ChangeEventArgs e)
    {
        var voice = Guid.TryParse(e.Value?.ToString(), out var id) ? Library.Voices.FirstOrDefault(v => v.Id == id) : null;
        _draft!.Voice = voice is null ? null : new() { AssetId = voice.AssetId, VoiceId = voice.Id, Speaker = _draft.Speaker, Start = voice.Start, Duration = voice.ExcerptDuration };
        await Changed();
    }
    private Task Compose() => Run(async () =>
    {
        _compositionSubmitting = true;
        try {
        if (_assist is null || !await _assist.PrepareSubmitAsync()) return;
        await Flush(); var id = Guid.NewGuid(); _draft!.PendingJobId = id; await Save();
        _enqueue = _assist.Attribute(await TextCapture.ComposeReelAsync(id, await Reviews.TabIdAsync(), ProjectId, _draft.Id, _draft.Revision, _model!.Model, _model.FollowsDefault, _lifetime.Token));
        _pendingText = true; await Enqueue();
        } finally { _compositionSubmitting = false; }
    });
    private Task Generate() => Run(async () =>
    {
        if (ActiveVideo is not null || _resettingDraft) return;
        if (!_loraValid) { _error = "Enter valid LoRA strengths before generating."; return; }
        _preparing = true; _preparation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try {
            await InvokeAsync(StateHasChanged);
            try {
                _draft!.SaveLosslessFrames ??= true;
                await Flush(); ReferenceReels.Validate(_draft, true);
                _enqueue = await VideoCapture.CaptureAsync(Guid.NewGuid(), await Reviews.TabIdAsync(), ProjectId, _draft!.Id, _draft.Revision, ReelTakeCount, _preparation.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) {
                _error = "Not submitted to ComfyUI · " + (e is OutOfMemoryException
                    ? "Lumibelle ran out of system memory while preparing the request. Your saved recipe is retained."
                    : e.Message);
                return;
            }
            _pendingText = false; await Enqueue();
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { _error = "Preparation cancelled. Your recipe is saved."; }
        finally { _preparing = false; _preparation.Dispose(); _preparation = null; }
    });
    private void CancelPreparation() => _preparation?.Cancel();
    private async Task Enqueue()
    {
        if (_enqueue is null) return;
        await Jobs.EnqueueAsync(_enqueue, _lifetime.Token); _enqueue = null;
        if (_pendingText && _assist is not null) await _assist.QueuedAsync();
    }
    private Task RetryEnqueue() => Run(Enqueue);
    private async Task ViewJob(AiJobHeader job)
    {
        try
        {
            if (job.Kind == AiJobKind.ReelVideo)
            { _videoJob = job; _videoRequest = AiVideoJobHandler.Read(job, await JobStore.ReadSnapshotAsync(job.Id)); return; }
            await Flush();
            _reviewJob = job; _reviewRequest = AiTextJobHandler.Read(job, await JobStore.ReadSnapshotAsync(job.Id));
            var request = _reviewRequest.Payload<ReelCompositionRequest>();
            if (_draft?.Id != request.Draft.Id) { _draft = Library.ReelDrafts.FirstOrDefault(d => d.Id == request.Draft.Id)?.Copy() ?? request.Draft.Copy(); _saved = _draft.Copy(); }
            var result = await JobStore.ReadArtifactAsync<AiTextJobResult>(job.Id, AiJobArtifact.Result);
            _pair = job.State == AiJobState.Completed && !job.CancelRequested && result?.Error is null && !_draft.ResolvedJobs.Contains(job.Id) ? result?.Read<ReelPromptPair>() : null;
            _raw = result?.Raw ?? ""; _reviewError = result?.Error ?? job.Error;
            _unappliedPair = null; _responseDraft = null; _copyNotice = null; _pairChanged = null;
            if (_pair is null && result?.Complete == true && result.Error is not null)
            {
                // Older results saved only raw JSON on validation failure. Decode for
                // inspection without changing their status or bypassing application checks.
                try { _unappliedPair = ReferenceReels.ParsePair(result.Raw); }
                catch (WorkspaceStoreException) { }
            }
            if (_draft.ResolvedJobs.Contains(job.Id)) _reviewError = "This response has already been applied or discarded.";
            _reviewOpen = true;
        }
        catch (Exception e) { _error = e.Message; }
    }
    private async Task CopyResponse(string text)
    {
        try { await JS.InvokeVoidAsync("navigator.clipboard.writeText", text); _copyNotice = "Copied. Paste into the recipe to edit."; }
        catch (JSException) { _copyNotice = "Select and copy the response text below."; }
    }
    private async Task EditResponse()
    {
        var edited = false;
        await Run(async () => {
            if (_unappliedPair is not { } pair || _reviewRequest is null) return;
            try {
                await Flush();
                _responseDraft ??= _reviewRequest.Payload<ReelCompositionRequest>().Draft.Copy() with {
                    Id = Guid.NewGuid(), Revision = 0, PendingJobId = null, ResolvedJobs = [],
                    Prompt = pair.Prompt, UseGuidance = pair.UseGuidance, CheckedInputs = null
                };
                // Keep current edits and the original request. Retain the copy's
                // identity/revision if publication or the subsequent reload fails.
                await Mutate(async _ => {
                    _responseDraft = await Reels.SaveDraftAsync(ProjectId, _responseDraft, _responseDraft.Revision, _lifetime.Token);
                    return await AssetStore.LoadAsync(ProjectId, _lifetime.Token);
                });
                _draft = _responseDraft.Copy(); _saved = _draft.Copy();
                // The old editor was flushed above and is about to be replaced by
                // the new recipe's keyed editor. Do not flush that retired DOM on navigation.
                _prompt = null;
                _reviewOpen = false; _reviewError = null; _error = null;
                await RevealPrompts.InvokeAsync();
                edited = true;
            } catch (Exception e) { _reviewError = e.Message; if (!_reviewOpen) _error = e.Message; }
        });
        if (!edited) return;
        // Navigation asks every editor to flush. Wait until Run has released
        // its busy state so the details editor can allow that transition.
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("jobId", (string?)null), replace: true);
        // The Prompt dialog does not open while Run holds the busy state.
        await OpenSetupDialog("Prompt");
    }
    // Set when the recipe changed since the pair was requested: the review offers Apply anyway.
    private AssistedInputsChangedException? _pairChanged;
    private Task ApplyPair() => Run(async () =>
    {
        await Flush(); if (_pair is null || _reviewJob is null || _reviewRequest is null) return;
        var current = Jobs.View.Jobs.First(j => j.Id == _reviewJob.Id);
        bool applied;
        try { applied = await Reels.ApplyPairAsync(current, _reviewRequest.Payload<ReelCompositionRequest>(), _pair, false, _lifetime.Token, acceptChangedInputs: _pairChanged is not null); }
        catch (AssistedInputsChangedException e) { _pairChanged = e; _reviewError = null; return; }
        catch (WorkspaceStoreException e) { _reviewError = e.Message; return; }
        if (!applied)
        { _pairChanged = null; _reviewError = "A newer Assist request replaced this pair, or it was already applied or discarded. Close this comparison and open the latest suggestion."; return; }
        await Mutate(_ => AssetStore.LoadAsync(ProjectId));
        _draft = (await AssetStore.LoadAsync(ProjectId)).ReelDrafts.Single(d => d.Id == _draft!.Id).Copy(); _saved = _draft.Copy();
        _pair = null; _pairChanged = null; _reviewOpen = false;
    });
    private Task DiscardPair() => Run(async () => { if (_reviewJob is not null) { _draft!.ResolvedJobs.Add(_reviewJob.Id); await Save(); } _pair = null; _reviewOpen = false; });
    public void EditReel(AssetReferenceReel reel, bool rename = false)
    {
        if (_busy) return;
        if (_details?.Id != reel.Id) { _details = reel; _detailName = reel.Name; _detailGuidance = reel.UseGuidance; _detailLook = reel.LookId; _error = null; }
        _renameDetails = rename; _confirmDetailsClose = false; _directionsCopyNotice = null;
        // Rename focuses the name itself; the dialog's focus trap must not apply its own initial focus afterwards. Decided
        // here, not from _renameDetails, which clears once the name has focus and would hand the trap its default back.
        _detailsFocus = rename ? DefaultFocus.None : null;
        StateHasChanged();
    }
    private DefaultFocus? _detailsFocus;
    private void CloseDetails()
    {
        if (_busy) return;
        if (DetailsDirty) _confirmDetailsClose = true; else ClearDetails();
    }
    private Task SaveDetails() => Run(async () => { await SaveDetailFields(); ClearDetails(); });
    private async Task SaveDetailFields()
    {
        if (_details is not { } before || before.Name == _detailName && before.UseGuidance == _detailGuidance && before.LookId == _detailLook) return;
        var name = _detailName; var guidance = _detailGuidance; var look = _detailLook;
        await Mutate(revision => Reels.EditReelDetailsAsync(ProjectId, before, name, guidance, revision, lookId: look, updateLook: true));
        _details = Library.Reels.Single(r => r.Id == before.Id);
        if (_detailName == name) _detailName = _details.Name;
        if (_detailGuidance == guidance) _detailGuidance = _details.UseGuidance;
        if (_detailLook == look) _detailLook = _details.LookId;
    }
    public async Task<bool> FlushDetailsAsync() { if (_busy) return false; try { await SaveDetailFields(); return true; } catch (Exception e) { _error = e.Message; return false; } }
    // Called after the parent has flushed the selected editor through its serialized transition.
    public async Task AssignLookAsync(AssetReferenceReel before, Guid? look)
    {
        await Mutate(revision => Reels.EditReelDetailsAsync(ProjectId, before, before.Name, before.UseGuidance, revision, lookId: look, updateLook: true));
        if (_details?.Id == before.Id) EditReel(Library.Reels.Single(r => r.Id == before.Id));
    }
    public void ClearDetails() { _details = null; _confirmDetailsClose = false; _renameDetails = false; StateHasChanged(); }
    private Task ClearDraftLook() => Run(async () => { await Flush(); _draft = _draft!.Copy(); _draft.Id = Guid.NewGuid(); _draft.LookId = null; _draft.Revision = 0; _draft.PendingJobId = null; _draft.ResolvedJobs = []; _saved = null; await Save(); });
    public Task Trash(AssetReferenceReel reel) => Run(() => Mutate(revision => Reels.TrashReelAsync(ProjectId, reel.Id, revision)));
    /// <summary>
    /// Create similar replaces the Create draft, so it waits for the draft's own work and any reel request to finish. Its buttons
    /// are disabled meanwhile: a click that did nothing left the details open, for example just after a regeneration was saved.
    /// </summary>
    public bool CanVary(AssetReferenceReel reel) => reel.Generation is not null && !_disposed && !_saving && !_preparing && !_resettingDraft &&
        _enqueue is null && ActiveComposition is null && ActiveVideo is null;
    private const string VaryBusy = "Available when the current reel request finishes.";
    public Task Variation(AssetReferenceReel reel) => Run(async () =>
    {
        if (!CanVary(reel) || reel.Generation is not { } generation) return;
        await Flush(); await SaveDetailFields();
        var draft = ReferenceReels.SimilarDraft(generation.Recipe, Owner);
        ReferenceReelDraft? saved = null;
        await Mutate(async _ => { saved = await Reels.SaveDraftAsync(ProjectId, draft, 0, _lifetime.Token); return await AssetStore.LoadAsync(ProjectId, _lifetime.Token); });
        _details = null; _draft = saved!; _saved = saved!.Copy(); _saveFailed = false;
        _directionSourceId = ""; _directionsNotice = null; _completedVideoId = null;
        await InvokeAsync(StateHasChanged); await RevealTools.InvokeAsync();
    });
    public Task OpenImport() => Run(async () => { _takes = (await Shots.LoadAsync(ProjectId)).Takes.OrderByDescending(t => t.CreatedUtc).ToList(); _imported = null; _importTake = null; _takeId = ""; _importId = Guid.NewGuid(); _importTime = DateTimeOffset.UtcNow; _importGuidance = EnvironmentReel ? ReferenceReels.EnvironmentUseGuidance : ShotVideoBinding.DefaultDescription; _importOpen = true; });
    private Task Import(InputFileChangeEventArgs e) => Run(async () =>
    {
        await using var stream = e.File.OpenReadStream(ReferenceVideos.MaximumBytes, _lifetime.Token);
        _imported = await Media.ImportAsync(ProjectId, stream, e.File.Name, (await Settings.LoadAsync()).H3, _lifetime.Token);
        _importName = Path.GetFileNameWithoutExtension(e.File.Name); _importTake = null;
    });
    private Task CopyTake() => Run(async () => { var take = _takes.Single(t => t.Id.ToString() == _takeId); _imported = await Media.CopyTakeAsync(ProjectId, take.Id, (await Settings.LoadAsync()).H3, _lifetime.Token); _importTake = take.Id; _importName = take.Snapshot.Shot.Title; });
    private Task SaveImport() => Run(async () =>
    {
        var reel = new AssetReferenceReel { Id = _importId, AssetId = AssetId, LookId = Guid.TryParse(_filter, out var look) ? look : null, Media = _imported!,
            Name = _importName, UseGuidance = _importGuidance, SourceTakeId = _importTake, CreatedUtc = _importTime };
        await Mutate(revision => Reels.SaveReelAsync(ProjectId, reel, revision)); _importOpen = false;
    });
    private Task PlayExcerpt() => Run(async () => {
        _voiceModule ??= await JS.InvokeAsync<IJSObjectReference>("import", UiAssets.Module("voice-preview.js"));
        var voice = _draft!.Voice!; await _voiceModule.InvokeVoidAsync("playExcerpt", _voicePlayer, voice.Start, voice.Start + voice.Duration);
    });
    public async ValueTask DisposeAsync()
    { _disposed = true; Jobs.Changed -= JobsChanged; _lifetime.Cancel(); if (_voiceModule is not null) try { await _voiceModule.DisposeAsync(); } catch (JSDisconnectedException) { } _lifetime.Dispose(); }
}
