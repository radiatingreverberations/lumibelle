using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private PromptDraftRecovery? _compositionRecovery;
    private Guid? _recoveryJob;
    private string? _recoveryRaw;
    private lumibelle.Components.Shots.PromptEditor? _promptEditor;
    private readonly string _promptHistoryScope = Guid.NewGuid().ToString();
    private string _takeSetupFilter = "";
    private List<ShotTake> ShotTakes => _doc.Takes.Where(t => t.ShotId == _selected).OrderBy(t => t.CreatedUtc).ThenBy(t => t.Id).ToList();
    private Task ChangeSetup(Microsoft.AspNetCore.Components.ChangeEventArgs e) => Guid.TryParse(e.Value?.ToString(), out var id) ? SelectGlobalSetup(id) : Task.CompletedTask;
    private async Task PromptChanged(Guid shotId, string text) { if (Current is not { } current || current.ShotId != shotId || current.Prompt == text) return; EditComposition(c => c.Prompt = text); await Save(); }
    private ShotImageBinding? _picturePreview;
    private int _picturePreviewNumber;
    private ResolvedPicture? _keyframePreview;
    private Task PreviewPicture(int number) {
        var picture = Selected is { } shot ? ResolvedReferences.For(shot).Pictures.FirstOrDefault(p => p.Number == number) : null;
        _picturePreviewNumber = number; _picturePreview = picture?.Image is { } image ? ShotCopy.Of(image) : null;
        _keyframePreview = picture?.Keyframe is not null ? picture : null; return Task.CompletedTask;
    }
    private lumibelle.Components.AI.TextAssistance? _compositionAssist;
    private TextModelSelectionState? _compositionModel;
    private bool _compositionBusy;
    // Request-local choice. A queued request freezes this; generation references never change.
    // Request-local: scene text and neighbouring shots are the part of the prompt that can be dropped safely.
    private bool _fullCompositionContext = true;
    // The last size estimate of the composition request, per step; cleared when the model changes.
    private IReadOnlyList<ComfyTextStageSize>? _compositionStages;
    private string? _compositionSizeError, _compositionSizeModel;
    private bool _compositionSizeBusy, _compositionBriefReused;
    private void CompositionModelChanged(TextModelSelectionState? value)
    { _compositionModel = value; _compositionStages = null; _compositionSizeError = null; }
    private Task RefreshCompositionSize() => _compositionStages is null && _compositionSizeError is null ? Task.CompletedTask : EstimateCompositionSize();
    private static string FitLabel(ComfyTextFit fit) => fit switch
    {
        ComfyTextFit.Fits => "Fits", ComfyTextFit.AtLimit => "At the limit", ComfyTextFit.TooLarge => "Too large", _ => "Not measured"
    };
    /// <summary>Builds the request the Compose button would send, without queueing it, and sizes it against the model's capacity.</summary>
    private async Task EstimateCompositionSize()
    {
        if (Current is not { } c || _compositionModel is not { } selection || _compositionSizeBusy) return;
        _compositionSizeBusy = true;
        try
        {
            var submission = await TextRequests.ComposeAsync(Guid.NewGuid(), await AiReviews.TabIdAsync(), Id, c.Id, c.Version, selection.Model, selection.FollowsDefault,
                _lifetime.Token, reducedScriptContext: !_fullCompositionContext);
            var request = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
            _compositionStages = ComfyTextCapacity.Assess(request);
            _compositionSizeModel = TextModelPolicy.DisplayName(request.Model, request.Settings);
            _compositionBriefReused = request.TwoStep && request.VisualBrief is not null;
            _compositionSizeError = null;
        }
        catch (Exception e) when (e is WorkspaceStoreException or AiGenerationException or lumibelle.Services.ProjectStoreException)
        { _compositionStages = null; _compositionSizeError = "The size cannot be estimated right now: " + e.Message; }
        finally { _compositionSizeBusy = false; }
    }
    private string? _compositionError;
    private string? _compositionApplyError;
    private string _compositionRaw = "";
    private PromptCompositionResult? _compositionResult;
    private Guid? _compositionReviewId;
    private Guid? _compositionDialogJob;
    private Microsoft.AspNetCore.Components.ElementReference _compositionReviewOrigin;
    private MudBlazor.MudButton? _composeButton;
    private bool _restoreCompositionFocus;
    private AiJobSubmission? _compositionEnqueue;
    private async Task OpenCompositionReview()
    {
        if (_compositionResult is null || CompositionJob is not { } job) return;
        if (_promptEditor is not null) await _promptEditor.FlushAsync();
        if (await AiReviews.TryOpenAsync(job, _compositionReviewOrigin, automatic: false)) _compositionDialogJob = job.Id;
    }
    private Task CompositionReviewVisibility(bool visible) => visible ? Task.CompletedTask : CloseCompositionReview();
    private async Task CloseCompositionReview()
    {
        if (_compositionBusy) return;
        await ReleaseCompositionReview(); _restoreCompositionFocus = true;
    }
    private async Task ReleaseCompositionReview()
    {
        if (_compositionDialogJob is { } id) await AiReviews.CloseAsync(id);
        _compositionDialogJob = null;
    }
    private AiJobHeader? CompositionJob => AiJobs.View.Jobs.FirstOrDefault(j => j.Id == _compositionReviewId);
    private TextRequestPresentation? CompositionPresentation => CompositionJob is { } job ? new(job, "Writing prompt…",
        Current?.AppliedJobId == job.Id ? TextRequestOutcome.Resolved : _compositionError is not null ? TextRequestOutcome.Invalid : TextRequestOutcome.Proposal, "Write prompt") : null;
    private Task InspectComposition() => _compositionResult is not null ? OpenCompositionReview() : _compositionAssist?.OpenRequestAsync() ?? Task.CompletedTask;
    private Task CancelComposition() => CompositionJob is { } job ? AiJobs.CancelAsync(job.Id) : Task.CompletedTask;
    private Task NewCompositionRequest() => _compositionAssist?.OpenAsync() ?? Task.CompletedTask;
    private async Task RetryComposition(Guid id)
    {
        if (_compositionBusy) return;
        _compositionBusy = true;
        try { await AiJobs.ResumeAsync(id, _lifetime.Token); await RefreshCompositionResult(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { _compositionError = e.Message; }
        finally { _compositionBusy = false; }
    }
    private bool CanCompose => Current is { Archived: false } && !_compositionBusy && _compositionEnqueue is null &&
        _compositionModel?.Ready == true &&
        ((Selected is null || ResolvedReferences.For(Selected).Pictures.Count == 0 && !ReelRefMods.Uses(Selected)) || _compositionModel.SupportsImages) &&
        !AiJobs.View.Jobs.Any(j => j.Kind == AiJobKind.PromptComposition && j.Target.ProjectId == Id && j.Target.ShotId == Current.ShotId && j.LocksTarget);
    private bool CanClearPrompt => Current is { Archived: false } c && !_compositionBusy && _compositionEnqueue is null &&
        (c.Prompt.Length > 0 || c.RevisionNotes.Length > 0 || c.ReferenceUsage.Length > 0 || c.ReviewJobId is not null) &&
        !AiJobs.View.Jobs.Any(j => j.Kind == AiJobKind.PromptComposition && j.Target.ProjectId == Id && j.Target.ShotId == c.ShotId && j.LocksTarget);
    private string? PromptReviewIssue => Current is { } c && _project is not null
        ? ProductionPolicy.PromptReviewIssue(c, _assets, _doc, _project) : "Choose a shot.";
    private bool PromptNeedsReview => Current is { } c &&
        (c.Accepted?.Prompt != c.Prompt || c.ReviewJobId is not null || SourceChanged || CurrentPromptReferenceCheck.NeedsAttention);
    private bool CanAcceptPrompt => CanClearPrompt && PromptReviewIssue is null && PromptNeedsReview;
    private string? CompositionPromptWarning => Current is { } c && _project is not null && c.Shot.Duration is not null
        ? ProductionPolicy.PromptWarning(c.Prompt, lumibelle.Services.Shots.ShotVideoDefaults.Capture(c.Shot, _project)) : null;
    private void ClearPrompt()
    {
        if (!CanClearPrompt) return;
        EditComposition(c => { c.Prompt = ""; c.ReferenceUsage = ""; c.ReviewJobId = null; });
        _compositionReviewId = null; _compositionResult = null; _compositionRaw = ""; _compositionError = null; _compositionRecovery = null;
        Notify("Prompt cleared. Choose Compose prompt to start from the current shot and references. You can undo this.");
    }
    private async Task ComposePrompt()
    {
        if (!CanCompose || Current is null || _compositionAssist is null || !await _compositionAssist.PrepareSubmitAsync()) return;
        _compositionBusy = true; _compositionError = null;
        try
        {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (!await Save()) return;
            var c = Current!; var id = Guid.NewGuid();
            _compositionEnqueue = await TextRequests.ComposeAsync(id, await AiReviews.TabIdAsync(), Id, c.Id, c.Version, _compositionModel!.Model, _compositionModel.FollowsDefault, _lifetime.Token,
                reducedScriptContext: !_fullCompositionContext);
            _compositionEnqueue = _compositionAssist.Attribute(_compositionEnqueue);
            EditComposition(d => d.ReviewJobId = id);
            if (!await Save()) return;
            await EnqueueComposition();
        }
        catch (Exception e) { _compositionError = e.Message; }
        finally { _compositionBusy = false; }
    }
    private async Task EnqueueComposition()
    {
        if (_compositionEnqueue is not { } request) return;
        await AiJobs.EnqueueAsync(request, _lifetime.Token); _compositionEnqueue = null;
        if (_compositionAssist is not null) await _compositionAssist.QueuedAsync();
        await RefreshCompositionResult(request.Id);
    }
    private async Task RetryCompositionEnqueue()
    {
        if (_compositionBusy) return; _compositionBusy = true;
        try { if (await Save()) await EnqueueComposition(); } catch (Exception e) { _compositionError = e.Message; }
        finally { _compositionBusy = false; }
    }
    private async Task RefreshCompositionResult(Guid? explicitId = null)
    {
        if (!_dirty && Current is { } current) {
            var latest = await Production.LoadAsync(Id, _lifetime.Token);
            if (!_dirty && Current?.Id == current.Id && latest.Compositions.FirstOrDefault(c => c.Id == current.Id) is { } updated && updated.Version > current.Version) {
                _production = latest; _savedComposition = Current?.Copy();
            }
        }
        var composition = Current?.Id; var id = explicitId ?? Current?.ReviewJobId;
        if (_compositionReviewId != id) { await ReleaseCompositionReview(); _compositionReviewId = id; _compositionRaw = ""; _compositionResult = null; _compositionError = null; _compositionApplyError = null; _compositionChanged = null; _compositionRecovery = null; _recoveryJob = null; _recoveryRaw = null; }
        if (id is null) return;
        var job = AiJobs.View.Jobs.FirstOrDefault(j => j.Id == id && j.Kind == AiJobKind.PromptComposition && j.Target.ProjectId == Id && j.Target.ShotId == Current?.ShotId);
        if (job is null) return;
        var result = await AiJobStore.ReadArtifactAsync<AiTextJobResult>(id.Value, AiJobArtifact.Result, _lifetime.Token);
        if (Current?.Id != composition || _compositionReviewId != id) return;
        _compositionRaw = result?.Raw ?? "";
        _compositionError = result?.Error ?? job.Error;
        if (_recoveryJob != id || _recoveryRaw != _compositionRaw)
        {
            _recoveryJob = id; _recoveryRaw = _compositionRaw; _compositionRecovery = null;
            if (job is { State: AiJobState.NeedsAttention, Recovery: AiJobRecovery.GenerateAgain, CancelRequested: false, RemoteUnconfirmed: false } &&
                result is { Complete: true, Error: not null, FinishReason: null or "stop" })
            {
                try
                {
                    var request = AiTextJobHandler.Read(job, await AiJobStore.ReadSnapshotAsync(job.Id, _lifetime.Token)).Payload<PromptCompositionRequest>();
                    var recovered = PromptComposer.RecoverResponse(result.Raw, request);
                    if (Current?.Id == composition && _compositionReviewId == id) _compositionRecovery = recovered;
                }
                catch (WorkspaceStoreException) { /* Other validation failures still need manual editing or a new request. */ }
            }
        }
        if (_compositionRecovery is { } recovery) _compositionError = recovery.Sections.Count == 0 ? null : "Missing H3 sections: " + string.Join(", ", recovery.Sections) + ".";
        _compositionResult = job is { State: AiJobState.Completed, CancelRequested: false } && result is { Complete: true, Error: null } ? result.Read<PromptCompositionResult>() : null;
        if (Current?.AppliedJobId == id) _compositionResult = null;
        if (_compositionResult is null) await ReleaseCompositionReview();
    }
    private async Task RecoverCompositionResponse()
    {
        if (_compositionBusy || _compositionRecovery is not { } recovery || Current is null || CompositionJob is not { } job) return;
        _compositionBusy = true; _compositionError = null;
        try
        {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (!await Save()) return;
            Remember();
            var targetVersion = _production.Compositions.Single(c => c.Id == job.Target.CompositionId).Version;
            _production = await Production.RecoverResponseAsync(Id, job.Id, targetVersion, _lifetime.Token);
            _savedComposition = Current?.Copy(); _compositionRecovery = null; _compositionResult = null;
            _compositionRaw = ""; _compositionReviewId = null;
            Notify(recovery.Sections.Count > 0
                ? "Recovered the saved visual prompt and added the shot's sound settings. Review the H3 prompt before generating. You can undo this."
                : "Recovered the saved response unchanged. Review the H3 prompt before generating. You can undo this.");
            await ReleaseCompositionReview(); _restoreCompositionFocus = true;
        }
        catch (WorkspaceConflictException) { _compositionError = "This setup changed in another tab. Reload before recovering the saved response."; }
        catch (Exception e) { _compositionError = e.Message; }
        finally { _compositionBusy = false; }
    }
    // Set when the response's inputs changed since it was requested: the review offers Apply anyway.
    private AssistedInputsChangedException? _compositionChanged;
    private async Task ApplyCompositionResult() => await ApplyCompositionResult(acceptChangedInputs: _compositionChanged is not null);
    private async Task ApplyCompositionResult(bool acceptChangedInputs)
    {
        if (_compositionBusy || _compositionResult is not { } result || Current is not { } c || CompositionJob is not { } job) return;
        _compositionBusy = true; _compositionApplyError = null;
        try
        {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (!await Save()) { _compositionApplyError = _error ?? "Could not save the current draft. Your suggestion is retained; try Apply again."; return; }
            Remember();
            _production = await Production.ApplyResultAsync(Id, job.Id, false, _lifetime.Token, acceptChangedInputs);
            _compositionChanged = null;
            if (Current?.AppliedJobId != job.Id) throw new WorkspaceStoreException("This response can no longer be applied. Compose again using the current prompt and inputs.");
            _savedComposition = Current?.Copy(); _compositionResult = null; _compositionReviewId = null; _compositionRaw = ""; Notify("Changes applied. This prompt will be used for new takes.");
            await ReleaseCompositionReview(); _restoreCompositionFocus = true;
        }
        catch (AssistedInputsChangedException e) { _compositionChanged = e; }
        catch (WorkspaceConflictException) { _compositionApplyError = "A newer prompt request replaced this one. Close this review and open the latest suggestion."; }
        catch (Exception e) { _compositionApplyError = e.Message; }
        finally { _compositionBusy = false; }
    }
    private async Task DiscardCompositionResult()
    {
        if (_compositionBusy) return;
        _compositionBusy = true;
        try {
            EditComposition(c => c.ReviewJobId = null);
            if (!await Save()) return;
            _compositionReviewId = null; _compositionResult = null; _compositionRaw = ""; _compositionError = null;
            await ReleaseCompositionReview(); _restoreCompositionFocus = true;
        } finally { _compositionBusy = false; }
    }
    private async Task AcceptPrompt()
    {
        if (!CanAcceptPrompt || Current is not { } c) return; _compositionBusy = true; _compositionError = null;
        try
        {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (Current?.Id != c.Id) return;
            if (!await Save()) { _compositionError = _error ?? "Save the prompt before marking it reviewed."; return; }
            _production = await Production.AcceptAsync(Id, c.Id, Current!.Version, ct: _lifetime.Token); _savedComposition = Current?.Copy();
            _compositionReviewId = null; _compositionResult = null; _compositionRaw = ""; _compositionError = null;
            _compositionRecovery = null; _recoveryJob = null; _recoveryRaw = null;
            await ReleaseCompositionReview();
            _assets = await AssetStore.LoadAsync(Id, _lifetime.Token); _doc = await Store.LoadAsync(Id, _lifetime.Token);
            Notify("Prompt marked reviewed. Review notes are advisory.");
        }
        catch (Exception e) { _compositionError = e.Message; }
        finally { _compositionBusy = false; }
    }
    private void RestorePrompt(CompositionPromptRevision revision) => EditComposition(c => { c.Prompt = revision.Prompt; c.ReferenceUsage = revision.ReferenceUsage; c.AcceptedRevisionId = null; });
}
