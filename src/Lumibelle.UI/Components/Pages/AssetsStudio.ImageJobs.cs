using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using lumibelle.Components.Assets;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    [Inject] public AiImageJobCapture ImageRequests { get; set; } = null!;
    [Inject] private ILogger<AssetsStudio> Logger { get; set; } = null!;
    private readonly Dictionary<Guid, AiImageJobRequest> _imageRequests = [];
    private readonly Dictionary<Guid, ImageReviewSession> _imageSessions = [];
    private readonly Dictionary<Guid, AiJobSubmission> _pendingImages = [];
    private readonly Dictionary<Guid, ImageComposerDraft> _imageDrafts = [];
    private readonly Dictionary<Guid, ImageComposerDraft> _submittedImageCreates = [];
    private readonly HashSet<Guid> _clearedImageCreates = [];
    private ImageComposerDraft? _freshImageComposer;
    private sealed class ImageAppendRequest(int count)
    {
        public Guid[] Commands { get; } = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        public int Completed { get; set; }
    }
    private readonly Dictionary<Guid, ImageAppendRequest> _imageAppendCommands = [];
    private ImageAppendRequest? ImageAppendAttempt => _confirmImageAppend is { } request ? _imageAppendCommands.GetValueOrDefault(request.BatchId) : null;
    private string? ImageAppendNotice => ImageAppendAttempt is { } attempt ? $"{attempt.Completed} of {attempt.Commands.Length} queued. Retry continues this request without adding duplicates." : null;
    private AssetLibrary? _savedLibrary;
    private Guid? _imageSubmittingAsset, _imageOriginJob, _imageReviewOwner, _requestedImageJob, _requestedImageRoot;
    private bool _imageRefreshing, _imageRefreshAgain, _appendingImage, _focusPromptAfterSubmit;
    private AiImageJobRequest? _confirmImageAppend;
    private string? _imageAppendError;
    private ElementReference _imageOriginElement;
    private string? _imageNavigationError, _imageReviewError;
    // Each submitted batch runs on its own; the composer is only busy while a request is being captured.
    private AiJobHeader[] ActiveImageJobs => AiJobs.View.Jobs.Where(j => j.Target.ProjectId == Id && j.Target.AssetId == _selectedAssetId &&
        AiJobLocks.IsImage(j.Kind) && j.LocksTarget).OrderBy(j => j.CreatedUtc).ToArray();
    private bool ImageQueueFull => AiJobLocks.ActiveImageBatches(AiJobs.View.Jobs, Id, _selectedAssetId) >= AiJobLocks.MaxActiveImageBatchesPerAsset;
    private string? ImageQueueStatus => ActiveImageJobs switch
    {
        [] => null,
        [{ State: AiJobState.Waiting } waiting] => $"Queued · {waiting.TargetName}",
        [var single] => $"Generating image · {AiJobs.Progress(single.Id)?.Progress.Label}",
        var jobs => string.Join(" · ", new[] { (Count: jobs.Count(j => j.State != AiJobState.Waiting), Label: "working"), (Count: jobs.Count(j => j.State == AiJobState.Waiting), Label: "queued") }
            .Where(p => p.Count > 0).Select(p => $"{p.Count} {p.Label}")) + " image requests"
    };
    private bool _generating => _imageSubmittingAsset is not null && _imageSubmittingAsset == _selectedAssetId;
    private int ImageQueuePosition(AiJobHeader job) => Math.Max(1, Array.FindIndex(AiQueueOrder.Waiting(AiJobs.View.Jobs, job.Backend), j => j.Id == job.Id) + 1);
    private string ImageRequestPrompt(AiJobHeader job) => job.Batch is { } batch && _imageRequests.TryGetValue(batch.RootId, out var request) && !string.IsNullOrWhiteSpace(request.Prompt)
        ? $"“{request.Prompt.Trim()}”" : job.TargetName;
    private bool _composerLocked => _generating || _enhancing || AiJobs.View.Jobs.Any(j => j.Target.ProjectId == Id && j.Target.AssetId == _selectedAssetId && j.Kind == AiJobKind.PromptEnhancement && j.LocksTarget);
    private AiJobSubmission? PendingImage => _selectedAssetId is { } id ? _pendingImages.GetValueOrDefault(id) : null;
    private bool ImageErrorIsHistory => ActiveImageJobs.Length == 0 && PendingImage is null && _latestEditBatch is { Job: not null, CompletedCandidates.Count: > 0 } && _generationError == _latestEditBatch.Status;
    private string? LatestImageBatchSummary => _latestEditBatch is { Job: { } job } batch ? $"{job.State} · {batch.CompletedCandidates.Count} {(batch.IsRegional ? "results" : "saved")}" : null;
    private string ReviewImagesLabel => _latestEditBatch?.IsEditBatch == true ? "Review latest edit" : "Review latest images";
    private string? OneMoreTakeUnavailable => _review is not { IsBatch: true, Job: not null } batch ? "This batch has no saved request."
        : _appendingImage ? "Saving the extra candidate…"
        : batch.AssetId != _selectedAssetId ? "Select this asset to add another take."
        : ImageQueueFull && !ActiveImageJobs.Any(j => j.Batch?.RootId == batch.Id) ? AiJobLocks.ImageLimitMessage
        : batch.Job.RemoteUnconfirmed ? "Check the uncertain remote request before adding another take."
        : batch.CancellationRequested ? "Wait for cancellation to finish."
        : batch.Entries.Where(e => !e.IsTake).Any(e => FindImage(e.Reference) is null) ? "Restore the source and references before adding another take." : null;

    private void ImageQueueChanged() { if (!_disposed && !_loading) _ = InvokeAsync(RefreshImageJobsAsync); }
    private async Task ResetImageProjectAsync()
    {
        _confirmImageAppend = null; _imageAppendError = null;
        if (_imageReviewOwner is { } owner) await AiReviews.CloseAsync(owner);
        if (_imageOriginJob is { } origin) await AiReviews.CloseAsync(origin);
        _imageReviewOwner = _imageOriginJob = _requestedImageJob = _imageSubmittingAsset = null;
        _reviewOpen = false; _imageDetailsDirty = false; _openImageDetails = false; _projectPickerOpen = false; _review = _latestEditBatch = null; _savedLibrary = null;
        _moveSource = null; _moveImages = []; _focusMovedEdit = false;
        _imageRequests.Clear(); _imageSessions.Clear(); _imageDrafts.Clear(); _pendingImages.Clear();
        _submittedImageCreates.Clear(); _clearedImageCreates.Clear();
        _imageAppendCommands.Clear(); _requestedImageRoot = null; _imageNavigationError = _imageReviewError = null;
    }
    private async Task ReconcileLibraryLockedAsync()
    {
        if (_library is null) return;
        var project = Id; var saved = await AssetStore.LoadAsync(project);
        if (_disposed || project != Id || _library is null || saved.Revision == _library.Revision) return;
        // Only background media publication/membership is automatically rebased.
        // Ordinary author saves keep the existing revision-conflict review flow.
        if (!AssetLibraryRebase.HasMediaChanges(_savedLibrary ?? _library, saved)) return;
        _library = _dirty ? AssetLibraryRebase.Merge(_savedLibrary ?? _library, _library, saved) : saved;
        _savedLibrary = saved.Copy();
    }
    private async Task RefreshImageJobsAsync()
    {
        if (_disposed || _loading || _library is null) return;
        if (_imageRefreshing) { _imageRefreshAgain = true; return; }
        _imageRefreshing = true;
        try
        {
            do
            {
                _imageRefreshAgain = false;
                var project = Id; var selected = _selectedAssetId;
                var jobs = AiJobs.View.Jobs.Where(j => j.Target.ProjectId == project && j.Kind is AiJobKind.ImageCreate or AiJobKind.ImageEdit && j.Batch is not null).ToArray();
                // Refresh selected/reviewed batches only; avoid loading every historical
                // batch's prepared images during an unrelated progress notification.
                var latestRoot = jobs.Where(j => j.Id == j.Batch!.RootId && j.Target.AssetId == selected).MaxBy(j => j.CreatedUtc)?.Id;
                var roots = jobs.Where(j => j.Id == latestRoot || _submittedImageCreates.ContainsKey(j.Id) || j.Target.AssetId == selected && j.LocksTarget || _review?.Id == j.Batch!.RootId || _requestedImageRoot == j.Batch!.RootId)
                    .Select(j => j.Batch!.RootId).Distinct().ToArray();
                if (roots.Length > 0)
                {
                    await _saveGate.WaitAsync(_extractionLifetime.Token);
                    try { await ReconcileLibraryLockedAsync(); } finally { _saveGate.Release(); }
                }
                if (_disposed || project != Id) return;
                foreach (var root in roots)
                {
                    var header = jobs.Single(j => j.Id == root);
                    if (!_imageRequests.TryGetValue(root, out var request))
                    {
                        request = (await AiJobStore.ReadSnapshotAsync(root, _extractionLifetime.Token)).Deserialize<AiImageJobRequest>(AtomicJsonFile.Options)
                            ?? throw new WorkspaceStoreException("The saved image batch cannot be read.");
                        if (request.BatchId != root || request.ProjectId != project || request.AssetId != header.Target.AssetId)
                            throw new WorkspaceStoreException("The image batch belongs to another target.");
                        // Review uses exact identities/crops and normal media endpoints;
                        // retain no prepared PNG payloads in the page's visit cache.
                        request = request with { Regional = request.Regional is { } regional ? regional with { OriginalPng = [] } : null, Inputs = request.Inputs.Select(i => i with { Png = [] }).ToArray() };
                        _imageRequests[root] = request;
                    }
                    if (_disposed || project != Id) return;
                    var related = jobs.Where(j => j.Batch!.RootId == root).ToArray();
                    var progressJob = related.FirstOrDefault(j => j.State is AiJobState.Waiting or AiJobState.Running) ?? related.MaxBy(j => j.Batch!.Candidates[^1].Number)!;
                    // Progress is display-only; an unreadable checkpoint must not stop the review refresh.
                    AiJobProgress? progress = null;
                    try { progress = await AiJobs.ReadProgressAsync(progressJob.Id, _extractionLifetime.Token); }
                    catch (WorkspaceStoreException e) { Logger.LogWarning(e, "Showed image batch {Job} without its progress", progressJob.Id); }
                    if (_disposed || project != Id) return;
                    _imageSessions[root] = AiImageBatchReview.Update(_imageSessions.GetValueOrDefault(root), request, related, _library!, progress);
                    if (request.Regional is not null)
                    {
                        foreach (var item in related)
                            if (await AiJobStore.ReadArtifactAsync<AiImageJobResult>(item.Id, AiJobArtifact.Result, _extractionLifetime.Token) is { } saved)
                                foreach (var candidate in saved.Candidates) {
                                    _imageSessions[root].CompletedCandidates.Add(candidate.Number);
                                    _imageSessions[root].PendingCandidates.Remove(candidate.Number);
                                    _imageSessions[root].StoppedCandidates.Remove(candidate.Number);
                                }
                    }
                    ClearSuccessfulImageCreate(header, request);
                }
                if (_disposed || project != Id || selected != _selectedAssetId) { _imageRefreshAgain = !_disposed && project == Id; continue; }
                var latest = jobs.Where(j => j.Id == j.Batch!.RootId && j.Target.AssetId == selected).MaxBy(j => j.CreatedUtc);
                _latestEditBatch = latest is null ? null : _imageSessions.GetValueOrDefault(latest.Id);
                foreach (var key in _pendingImages.Where(p => jobs.Any(j => j.Id == p.Value.Id)).Select(p => p.Key).ToArray()) _pendingImages.Remove(key);
                if (ActiveImageJobs.Length == 0 && _latestEditBatch?.Job is not null) _generationError = _latestEditBatch.Status;
                if (_imageOriginJob is { } origin && jobs.FirstOrDefault(j => j.Id == origin) is { } originating &&
                    _imageSessions.GetValueOrDefault(originating.Batch!.RootId) is { ReviewOffered: false } batch && batch.CompletedCandidates.Count > 0)
                {
                    batch.ReviewOffered = true;
                    if (originating.Target.AssetId == selected && !string.IsNullOrEmpty(_imageOriginElement.Id) && await AiReviews.TryOpenAsync(originating, _imageOriginElement, true))
                    {
                        if (!_disposed && project == Id && selected == _selectedAssetId) await ShowImageReviewAsync(batch, originating);
                        else await AiReviews.CloseAsync(originating.Id);
                    }
                }
            } while (_imageRefreshAgain && !_disposed);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (WorkspaceConflictException e) { _conflict = true; _saveError = e.Message; _saveStatus = "Conflict"; }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException or JsonException) { _generationError = e.Message; }
        finally { _imageRefreshing = false; if (!_disposed) StateHasChanged(); }
    }

    private async Task GenerateAsync()
    {
        if (_library is null || _selectedAssetId is not { } assetId || !CanRunImageOperation || PendingImage is not null) return;
        var project = Id; var id = Guid.NewGuid(); var tags = ParseTags(_imageTags);
        var configured = _settings is null ? null : JsonSerializer.Deserialize<AiSettings>(JsonSerializer.SerializeToUtf8Bytes(_settings, AtomicJsonFile.Options), AtomicJsonFile.Options);
        if (!IsCodex && !string.IsNullOrWhiteSpace(_fixedSeed) && (!long.TryParse(_fixedSeed, out var parsed) || parsed < 0 || parsed > long.MaxValue - _candidateCount + 1))
        { _generationError = "Seed must be nonnegative with room for each candidate."; return; }
        var seed = !IsCodex && long.TryParse(_fixedSeed, out var chosen) ? (long?)chosen : null;
        var create = IsEditMode ? null : new ReferenceGenerationRequest { Resolution = CapturedImageResolution, QwenImage21 = CaptureQwenOptions(), Look = CaptureLook(), ProjectId = project, Workflow = _imageWorkflow, Prompt = _imagePrompt,
            Tags = tags, AspectRatio = ImageAspect, Count = _candidateCount, Seed = seed, Loras = LoraPolicy.Capture(SelectedLoras) };
        var edit = !IsEditMode ? null : new ReferenceEditRequest { Resolution = CapturedImageResolution, QwenImage21 = CaptureQwenOptions(), Look = CaptureLook(), ProjectId = project, Workflow = _imageWorkflow, SourceAssetId = BaseReference!.AssetId,
            SourceImageId = _editSourceImageId!.Value, Prompt = _imagePrompt, Tags = tags, AspectRatio = ImageAspect, Count = _candidateCount, Seed = seed,
            ReferenceBoost = _referenceBoost, BaseReferenceBoost = _baseReferenceBoost, GroundingPixels = _groundingPixels, SourceCrop = SourceCrop,
            Regions = CurrentRegions, ReferenceCrops = CaptureReferenceCrops(), ReferenceLooks = CaptureReferenceLooks(), Loras = LoraPolicy.Capture(SelectedLoras) };
        var references = BaseReference is { } source ? new[] { source }.Concat(_additionalReferences).ToArray() : [];
        _imageSubmittingAsset = assetId; _generationError = null; RememberImageComposer(); var submitted = _imageDrafts[assetId];
        if (create is not null) _submittedImageCreates[id] = _imageDrafts[assetId];
        try
        {
            if (!await SaveNowAsync() || _disposed || Id != project) return;
            var tab = await AiReviews.TabIdAsync();
            var request = create is not null ? await ImageRequests.CreateAsync(id, tab, assetId, create, _extractionLifetime.Token, configured)
                : await ImageRequests.EditAsync(id, tab, assetId, edit!, references, _extractionLifetime.Token, configured);
            _pendingImages[assetId] = request; _imageOriginJob = id;
            await AiJobs.EnqueueAsync(request, _extractionLifetime.Token); _pendingImages.Remove(assetId);
            // The request carries its own inputs; start the next one from a clear composer.
            if (!_disposed && Id == project) ClearSubmittedImageComposer(assetId, submitted);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) when (e is AiGenerationException or WorkspaceStoreException or ProjectStoreException) { _generationError = e.Message; }
        finally { if (_imageSubmittingAsset == assetId) _imageSubmittingAsset = null; if (!_disposed) await RefreshImageJobsAsync(); }
    }
    private async Task RetryImageEnqueueAsync()
    {
        if (PendingImage is not { } request || _imageSubmittingAsset is not null) return;
        _imageSubmittingAsset = request.Target.AssetId;
        try { await AiJobs.EnqueueAsync(request, _extractionLifetime.Token); _pendingImages.Remove(request.Target.AssetId!.Value); _generationError = null; }
        catch (WorkspaceStoreException e) { _generationError = e.Message; }
        finally { _imageSubmittingAsset = null; if (!_disposed) await RefreshImageJobsAsync(); }
    }
    private void AddOneMoreTake()
    {
        if (_disposed || OneMoreTakeUnavailable is not null || _review is not { } batch) return;
        if (!_imageRequests.TryGetValue(batch.Id, out var request)) { _imageReviewError = "The saved request is unavailable. Reopen this batch to try again."; return; }
        _confirmImageAppend = request; _imageAppendError = null;
    }
    private async Task ConfirmOneMoreImage(RepeatGenerationDialog.Choices choices)
    {
        if (_disposed || _appendingImage || _confirmImageAppend is not { } captured) return;
        if (OneMoreTakeUnavailable is { } issue) { _imageAppendError = issue; return; }
        if (_review is not { } batch || batch.Id != captured.BatchId) { _imageAppendError = "The reviewed batch changed. Close this confirmation and open it again."; return; }
        _appendingImage = true; _imageReviewError = null;
        _imageAppendError = null;
        try
        {
            if (!await SaveNowAsync()) { _imageAppendError = _saveError; return; }
            if (choices.Count is < 1 or > 4 || choices.KeepSeed) throw new WorkspaceStoreException("Choose one to four new takes.");
            if (!_imageAppendCommands.TryGetValue(batch.Id, out var attempt)) _imageAppendCommands[batch.Id] = attempt = new(choices.Count);
            var tab = await AiReviews.TabIdAsync();
            for (; attempt.Completed < attempt.Commands.Length; attempt.Completed++)
                await AiJobs.ExtendBatchAsync(batch.Id, attempt.Commands[attempt.Completed], tab, _extractionLifetime.Token);
            _imageAppendCommands.Remove(batch.Id); _generationError = null; _confirmImageAppend = null;
            await RefreshImageJobsAsync();
        }
        catch (Exception e) when (e is AiGenerationException or WorkspaceStoreException or ProjectStoreException) { _imageAppendError = e.Message; }
        finally { _appendingImage = false; if (!_disposed) StateHasChanged(); }
    }
    private Task CancelGeneration(AiJobHeader job) => CancelImageJobAsync(job);
    private Task CancelReviewGeneration() => CancelImageJobAsync(_review?.Job);
    private async Task CancelImageJobAsync(AiJobHeader? job)
    {
        if (job is null) return;
        try { await AiJobs.CancelAsync(job.Id, _extractionLifetime.Token); await RefreshImageJobsAsync(); }
        catch (WorkspaceStoreException e) { _generationError = e.Message; }
    }
    private async Task RetryImageOutputAsync()
    {
        if (_review?.Job is not { } job) return;
        try { await AiJobs.ResumeAsync(job.Id, _extractionLifetime.Token); await RefreshImageJobsAsync(); }
        catch (WorkspaceStoreException e) { _generationError = _review.Status = e.Message; }
    }
    private async Task ViewImageRequestAsync(AiJobHeader job)
    {
        await RefreshImageJobsAsync();
        if (job.Batch is { } batch && _imageSessions.GetValueOrDefault(batch.RootId) is { } session) await OpenImageBatchAsync(session);
    }
    private async Task OpenImageBatchAsync(ImageReviewSession batch, AssetImageReference? initial = null)
    {
        if (batch.Job is not { } job || !await AiReviews.TryOpenAsync(job, _imageOriginElement, false))
        { _generationError = "Close the other dialog before opening image review."; return; }
        await ShowImageReviewAsync(batch, job, initial);
    }
    private async Task ShowImageReviewAsync(ImageReviewSession batch, AiJobHeader job, AssetImageReference? initial = null)
    {
        if (_disposed) { await AiReviews.CloseAsync(job.Id); return; }
        _imageReviewOwner = job.Id; batch.ReviewOffered = true; _review = batch; _reviewInitial = initial; _reviewOpen = true;

        StateHasChanged();
    }
    private async Task HandleRequestedImageAsync()
    {
        if (!_extractionInitialized || _imageRefreshing || string.IsNullOrEmpty(_imageOriginElement.Id) || RequestedJobId is not { } id || _requestedImageJob == id) return;
        var job = AiJobs.View.Jobs.FirstOrDefault(j => j.Id == id && j.Target.ProjectId == Id && j.Kind is AiJobKind.ImageCreate or AiJobKind.ImageEdit);
        if (job?.Batch is null) return;
        _requestedImageRoot = job.Batch.RootId;
        if (_library?.Assets.Any(a => a.Id == job.Target.AssetId) != true)
        { _requestedImageJob = id; _imageNavigationError = "This image request’s destination asset was removed. Restore it from Trash to review or extend this batch. Saved job details remain available in Activity."; StateHasChanged(); return; }
        if (_selectedAssetId != job.Target.AssetId) { await SelectAssetAsync(job.Target.AssetId!.Value); if (_selectedAssetId != job.Target.AssetId) return; }
        if (_imageRefreshing) return;
        await RefreshImageJobsAsync();
        _requestedImageJob = id;
        if (_imageSessions.GetValueOrDefault(job.Batch.RootId) is { } batch) await OpenImageBatchAsync(batch);
    }

    private sealed record ImageComposerDraft(ImageWorkflow Workflow, string Prompt, string Tags, string Aspect, string Seed, int Count, Guid? Source, Guid? Look,
        IReadOnlyList<AssetImageReference> References, IReadOnlyList<AssetReferenceCrop> Crops, float ReferenceBoost, float BaseBoost, int Grounding, IReadOnlyList<RegionalImageSelection>? Regions = null, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] QwenImage21Options? QwenImage21 = null,
        bool CreateFromRecipe = false, Guid? SourceAsset = null, IReadOnlyList<LoraSelection>? Loras = null, int Resolution = 1024)
    {
        public static ImageComposerDraft From(AiImageJobRequest request) => new(request.Workflow, request.Prompt, string.Join(", ", request.Tags), request.AspectRatio,
            (request.Create?.Seed ?? request.Edit?.Seed)?.ToString() ?? "", request.Create?.Count ?? request.Edit!.Count, request.Edit?.SourceImageId, request.Look?.LookId,
            request.Inputs.Skip(1).Select(i => i.Reference).ToArray(), request.Inputs.Where(i => i.Crop is not null).Select(i => new AssetReferenceCrop(i.Reference, i.Crop!)).ToArray(),
            request.Edit?.ReferenceBoost ?? 4, request.Edit?.BaseReferenceBoost ?? 1, request.Edit?.GroundingPixels ?? 768, request.Edit?.Regions,
            request.Workflow == ImageWorkflow.QwenImage21 ? QwenImage21Policy.Options(request) : null, Resolution: request.Create?.Resolution ?? request.Edit!.Resolution);
    }
    private void RememberImageComposer()
    {
        if (_selectedAssetId is not { } id || _library is null) return;
        _imageDrafts[id] = CaptureImageComposer();
    }
    private ImageComposerDraft CaptureImageComposer()
    {
        var crops = BaseReference is { } source && SourceCrop is { } crop ? new[] { new AssetReferenceCrop(source, crop) }.Concat(CaptureReferenceCrops()).ToArray() : CaptureReferenceCrops();
        return new(_imageWorkflow, _imagePrompt, _imageTags, ImageAspect, _fixedSeed, _candidateCount, _editSourceImageId, _targetLookId,
            _additionalReferences.ToArray(), crops, _referenceBoost, _baseReferenceBoost, _groundingPixels, CurrentRegions, CaptureQwenOptions(),
            _imageRecipeCreation, _imageRecipeSourceAsset, _imageRecipeLoras, CapturedImageResolution);
    }
    private void ClearSuccessfulImageCreate(AiJobHeader job, AiImageJobRequest request)
    {
        if (request.Create is null || job.Id != request.BatchId || _clearedImageCreates.Contains(job.Id) || job.State != AiJobState.Completed ||
            job.CancelRequested || job.RemoteUnconfirmed || job.Error is not null || job.Batch is not { Candidates.Count: > 0 } batch ||
            !batch.Candidates.All(c => _library!.ImagePublications.Any(p => p.JobId == job.Id && p.ImageId == c.Id))) return;
        var submitted = _submittedImageCreates.GetValueOrDefault(job.Id) ?? ImageComposerDraft.From(request);
        var fresh = submitted with { Prompt = "", Tags = "", Seed = "", Look = null, Count = 1, References = [], Crops = [] };
        var asset = request.AssetId;
        var current = CaptureImageComposer();
        if (_selectedAssetId == asset && _editSourceImageId is null &&
            (Same(current, submitted) || _freshImageComposer is not null && Same(current, _freshImageComposer) && _freshComposer == EnhancementContext().Fingerprint())) {
            ApplyImageComposer(fresh); RememberMediaDraft();
            _lookPromptDrafts[(asset, null)] = "";
        }
        if (_mediaImageDrafts.TryGetValue((asset, null), out var cached) && Same(cached, submitted)) _mediaImageDrafts[(asset, null)] = fresh;
        if (_imageDrafts.TryGetValue(asset, out cached) && Same(cached, submitted)) _imageDrafts[asset] = fresh;
        if (_lookPromptDrafts.GetValueOrDefault((asset, submitted.Look)) == submitted.Prompt) _lookPromptDrafts[(asset, submitted.Look)] = "";
        _submittedImageCreates.Remove(job.Id); _clearedImageCreates.Add(job.Id);
    }
    private static bool Same(ImageComposerDraft a, ImageComposerDraft b) => JsonElement.DeepEquals(JsonSerializer.SerializeToElement(a), JsonSerializer.SerializeToElement(b));
    // Like Clear, but an edit keeps its source image and destination look.
    private static ImageComposerDraft Cleared(ImageComposerDraft draft) => draft with { Prompt = "", Tags = "", Seed = "", References = [], Crops = [], Regions = [],
        Source = draft.CreateFromRecipe ? null : draft.Source, Look = draft.CreateFromRecipe || draft.Source is null ? null : draft.Look, SourceAsset = null, CreateFromRecipe = false };
    private void ClearSubmittedImageComposer(Guid assetId, ImageComposerDraft submitted)
    {
        var cleared = Cleared(submitted);
        if (_selectedAssetId == assetId && Same(CaptureImageComposer(), submitted))
        {
            ApplyImageComposer(cleared); _lookPromptDrafts[(assetId, cleared.Look)] = ""; RestoreLookPrompt(); RememberMediaDraft();
            _focusPromptAfterSubmit = true;
            return;
        }
        // The author moved to another asset while this request was captured; clear its remembered drafts.
        if (_imageDrafts.TryGetValue(assetId, out var draft) && Same(draft, submitted)) _imageDrafts[assetId] = cleared;
        foreach (var key in _mediaImageDrafts.Where(d => d.Key.Asset == assetId && Same(d.Value, submitted)).Select(d => d.Key).ToArray()) _mediaImageDrafts[key] = cleared;
    }
    private void ApplyImageComposer(ImageComposerDraft draft)
    {
        _imageRecipeCreation = draft.CreateFromRecipe; _imageRecipeSourceAsset = draft.SourceAsset; _imageRecipeLoras = draft.Loras;
        _qwenOptions = draft.QwenImage21 ?? new();
        _imageResolution = draft.Resolution;
        _regions.Clear(); foreach (var region in draft.Regions ?? []) _regions[region.Source] = region;
        _resolveImageDefault = false;
        _imageWorkflow = draft.Workflow; _imagePrompt = draft.Prompt; _imageTags = draft.Tags; _aspect = draft.Aspect; _fixedSeed = draft.Seed;
        _candidateCount = draft.Count; _editSourceImageId = draft.Source; _targetLookId = draft.Look;
        _additionalReferences.Clear(); _additionalReferences.AddRange(draft.References); ResetSourceCrop(); _referenceCropSelections.Clear(); _restoredCrops.Clear();
        foreach (var crop in draft.Crops) _restoredCrops[crop.Reference] = crop.Crop;
        _referenceBoost = draft.ReferenceBoost; _baseReferenceBoost = draft.BaseBoost; _groundingPixels = draft.Grounding;
        _freshComposer = null; _freshImageComposer = null;
    }
}
