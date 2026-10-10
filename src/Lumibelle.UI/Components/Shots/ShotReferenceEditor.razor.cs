using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection;

namespace lumibelle.Components.Shots;

public partial class ShotReferenceEditor
{
    [Parameter, EditorRequired] public Shot Shot { get; set; } = null!;
    [Parameter, EditorRequired] public AssetLibrary Library { get; set; } = null!;
    [Parameter] public IReadOnlyList<Shot> CopySources { get; set; } = [];
    [Parameter] public Shot? PreviousCopySource { get; set; }
    /// <summary>A shot's number in the shot list, to order and label the shots to copy from around this one.</summary>
    [Parameter] public Func<Shot, int>? CopySourceNumber { get; set; }
    [Parameter] public int CurrentShotNumber { get; set; }
    // Adds the previous shot's last production frame without replacing this shot's references.
    private void AddContinuityFrame()
    {
        if (PreviousCopySource is { } previous && ContinuityFrameOf?.Invoke(previous) is { } frame && ResolvedReferences.For(_draft).Pictures.Count < 9) _draft.ContinuityFrame = frame;
    }
    private IEnumerable<Shot> OtherCopySources => CopySources.Where(s => PreviousCopySource is null || s.Id != PreviousCopySource.Id);
    private string CopyLabel(Shot source) => CopySourceNumber is null ? source.Title : $"{CopySourceNumber(source):00} · {source.Title}";
    /// <summary>The previous shot's last production frame, added as a continuity picture when its references are copied.</summary>
    [Parameter] public Func<Shot, ShotContinuityFrame?>? ContinuityFrameOf { get; set; }
    [Parameter] public bool AllowReels { get; set; }
    [Parameter] public bool ReelAuthoring { get; set; }
    [Parameter] public bool AllowOtherShotSwaps { get; set; } = true;
    [Parameter] public Func<DerivedImageRequest, Task<SavedAssetImage>>? SaveImage { get; set; }
    [Parameter] public EventCallback<SavedAssetImage> ImageSaved { get; set; }
    [Parameter] public EventCallback<ReferenceSelection> ReferencesApplied { get; set; }
    private readonly Dictionary<Guid, Guid> _reelSources = [];
    private Guid? _replacingVideo;
    [Parameter] public bool AllowInference { get; set; }
    private string ContextName => ReelAuthoring ? "reel" : AllowInference ? "setup" : "shot";
    [Parameter] public Guid? Expanded { get; set; }
    [Parameter] public EventCallback<Shot> Applied { get; set; }
    [Parameter] public EventCallback Cancelled { get; set; }
    // Restores a trashed image by its Trash id and refreshes the host's Library.
    [Parameter] public Func<Guid, Task>? RestoreTrashedImage { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private bool _preparingFrames;
    private async Task CancelDialog() { await _lifetime.CancelAsync(); await Cancelled.InvokeAsync(); }
    public async ValueTask DisposeAsync() { await _lifetime.CancelAsync(); _lifetime.Dispose(); }
    private Shot _draft = new();
    private HashSet<Guid> _originalVoiceOwners = [];
    private bool ManagedVoice(ShotVideoBinding v) => _draft.CharacterVoices?.Any(c => c.AssetId == v.OwnerAssetId) == true;
    private void EnsureVoice(ReferenceAsset asset) { if (AllowReels && !ReelAuthoring && asset.Category == AssetCategory.Character && !_originalVoiceOwners.Contains(asset.Id) && _draft.CharacterVoices?.Any(c => c.AssetId == asset.Id) != true) CharacterVoices.Set(_draft, CharacterVoices.Initial(_draft, asset, Library), Library); }
    private Guid? _expanded, _replacing, _cropTarget;
    private string? _actionFocus;
    private bool _selectedTab, _busy, _focusGuidance, _focusCustomize;
    private string? _error;
    private readonly HashSet<Guid> _missing = [];
    private ElementReference _guidanceInput, _customizeButton, _imagesHeading, _reelsHeading, _voicesHeading;
    private string? _selectionNotice;
    private string _copySource = "";
    private string PictureBreakdown {
        get {
            var parts = new List<string> { $"{_draft.Images.Count} library images" };
            if (_draft.ContinuityFrame is not null) parts.Add("1 continuity frame");
            var frames = ResolvedReferences.For(_draft).Pictures.Count - _draft.Images.Count - (_draft.ContinuityFrame is null ? 0 : 1);
            if (frames > 0) parts.Add($"{frames} reel keyframes");
            return string.Join(" · ", parts);
        }
    }
    private void StartImageReplacement(ShotImageBinding binding)
    {
        _replacing = binding.Id; _replacingVideo = _swapVideo = null; _selectedTab = false; _selectionNotice = null;
    }
    protected override void OnInitialized() { _draft = Shot.Copy(); _originalVoiceOwners = CharacterVoices.Owners(Shot, Library).ToHashSet(); ShotReferences.RetainCharacters(_draft); _expanded = Expanded; _selectedTab = Expanded is not null; }
    // What a copy did, line by line; warnings ask for a decision before applying.
    private IReadOnlyList<(string Text, bool Warning)>? _copyNotice;
    private void CopyFromChanged(ChangeEventArgs e)
    {
        var value = e.Value?.ToString();
        var source = value == "previous" ? PreviousCopySource : Guid.TryParse(value, out var id) ? CopySources.FirstOrDefault(s => s.Id == id) : null;
        if (_busy || source is null) return;
        try
        {
            var copy = ReferenceCopies.Into(source, _draft, Library);
            _draft = copy.Inputs;
            var continuity = value == "previous" ? ContinuityFrameOf?.Invoke(source) : null;
            var continuityNotice = continuity is null ? null : ResolvedReferences.For(_draft).Pictures.Count >= 9
                ? "Its last frame was not added: this shot already has 9 pictures."
                : "Its production take's last frame is added as a continuity picture; remove it if you don't need it.";
            if (continuity is not null && ResolvedReferences.For(_draft).Pictures.Count < 9) _draft.ContinuityFrame = continuity;
            _reelVoiceNotice = null;
            // Copied references retain the source setup's captured guidance; they
            // are not new selections of the current asset-library reel defaults.
            _reelSources.Clear(); _swaps.Clear(); _swapOthers.Clear(); _swapVideo = null;
            _missing.Clear();
            _replacing = _replacingVideo = _expanded = null;
            _keyframeBinding = null;
            _focusGuidance = _focusCustomize = false;
            _originalVoiceOwners = CharacterVoices.Owners(_draft, Library).ToHashSet();
            _selectedTab = true;
            _error = null;
            _copyNotice = [.. copy.Warnings.Select(w => (w, true)),
                .. (copy.Warnings.Count == 0 ? ["References copied into the draft. Apply changes to keep them."] : Array.Empty<string>()).Select(n => (n, false)),
                .. (copy.Notes ?? []).Select(n => (n, false)),
                .. (continuityNotice is null ? Array.Empty<string>() : [continuityNotice]).Select(n => (n, false))];
            _copySource = ""; // Reset the picker so the same source can be copied again.
            _selectionNotice = null;
        }
        catch (WorkspaceStoreException error) { _error = error.Message; }
    }
    protected override async Task OnAfterRenderAsync(bool firstRender) { if (_focusGuidance) { _focusGuidance = false; await _guidanceInput.FocusAsync(); } if (_focusCustomize) { _focusCustomize = false; await _customizeButton.FocusAsync(); } }
    private IReadOnlyList<AssetImageReference> Assigned => _draft.Images.Where(b => b.Kind == ShotImageKind.AssetImage).Select(b => new AssetImageReference(b.AssetId, b.MediaId)).ToArray();
    private ShotImageBinding? Current => _draft.Images.FirstOrDefault(b => b.Id == _expanded);
    private AssetImage? Image(ShotImageBinding b) => b.Kind == ShotImageKind.AssetImage ? Library.Assets.FirstOrDefault(a => a.Id == b.AssetId)?.Images.FirstOrDefault(i => i.Id == b.MediaId) : null;
    private ReferenceAsset? CharacterAsset(ShotImageBinding b) => Library.Assets.FirstOrDefault(a => a.Id == b.AssetId && a.Category == AssetCategory.Character);
    private string Url(ShotImageBinding b) => $"/media/projects/{Library.ProjectId}/assets/{b.AssetId}/images/{b.MediaId}";
    private string Role(ShotImageBinding b) => AllowInference ? b.AiUseHint : b.Purpose is { } p ? ShotLooks.Label(p) : b.Use is { } u ? ReferenceSetups.UseLabel(u) : b.Role;
    private ShotReferenceGuidance Guidance(ShotImageBinding b) => ShotReferences.Resolve(b, Library, new());
    // A missing picture whose image, or whole asset, is still recoverable from Trash.
    private TrashedImage? Trashed(ShotImageBinding b) => b.Kind != ShotImageKind.AssetImage ? null :
        AssetImageLocations.RecoverableTrash(Library, new(b.AssetId, b.MediaId), Now);
    private DateTimeOffset Now => (ReferenceServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
    private async Task Restore(ShotImageBinding b, TrashedImage trash)
    {
        if (_busy || RestoreTrashedImage is null) return;
        _busy = true; _error = null;
        try { await RestoreTrashedImage(trash.Id); _missing.Remove(b.Id); }
        catch (WorkspaceStoreException e) { _error = e.Message + " Refresh references and retry."; }
        finally { _busy = false; }
    }
    private string? Issue(ShotImageBinding b) => _missing.Contains(b.Id) ? "Image unavailable. Restore or replace it before applying."
        : AllowInference ? lumibelle.Services.Production.ProductionPolicy.MediaIssue(b, Library) : ShotLooks.Issue(new Shot { Characters = _draft.Characters, Dialogue = _draft.Dialogue, Images = [b] }, Library);
    private void Add(AssetImageReference reference)
    {
        if (_busy || _replacing is null && ResolvedReferences.For(_draft).Pictures.Count >= 9 || Assigned.Contains(reference)) return;
        var asset = Library.Assets.First(a => a.Id == reference.AssetId); var image = asset.Images.First(i => i.Id == reference.ImageId);
        var binding = ReferenceSetups.Bind(asset, image, _draft);
        if (AllowInference) { binding.RepresentsId = null; binding.LookId = null; binding.InferUsage = true; binding.Use = null; binding.Purpose = null; binding.Role = "Let AI decide"; }
        if (_replacing is { } replace && _draft.Images.FirstOrDefault(b => b.Id == replace) is { } prior) { binding.Id = prior.Id; binding.AiUseHint = prior.AiUseHint; _draft.Images[_draft.Images.IndexOf(prior)] = binding; _replacing = null; _selectionNotice = $"Replaced Picture {_draft.Images.IndexOf(binding) + 1} with {binding.Name}. Apply changes to save."; }
        else { _draft.Images.Add(binding); _selectionNotice = $"Added {binding.Name} as Picture {_draft.Images.Count}. Apply changes to save."; }
        _expanded = binding.Id; EnsureVoice(asset);
    }
    private void Remove(ShotImageBinding b) { if (_replacing == b.Id) _replacing = null; _draft.Images.Remove(b); if (_expanded == b.Id) _expanded = null; }
    private void Move(ShotImageBinding b, int by) { var i = _draft.Images.IndexOf(b); if (i + by < 0 || i + by >= _draft.Images.Count) return; _draft.Images.RemoveAt(i); _draft.Images.Insert(i + by, b); }
    private void RoleChanged(ShotImageBinding b) { _draft.Images[_draft.Images.FindIndex(i => i.Id == b.Id)] = b; }
    private void LinkCharacter(ChangeEventArgs e)
    {
        if (Current is not { } b) return;
        b.RepresentsId = Guid.TryParse(e.Value?.ToString(), out var id) ? id : null;
        b.InferUsage = false; b.Purpose = b.RepresentsId is null ? null : ShotReferencePurpose.Identity;
        b.Use = null; b.Role = b.RepresentsId is null ? "Appearance" : "Identity";
    }
    private void AddCharacter(ReferenceAsset asset) { var c = new ShotCharacter(Guid.NewGuid(), asset.Name); _draft.Characters.Add(c); LinkCharacter(new() { Value = c.Id }); }
    private void Customize() { if (Current is not { } b) return; b.PreservationOverride = Guidance(b).Effective; b.Notes = ""; _focusGuidance = true; }
    private void ResetGuidance() { if (Current is not { } b) return; b.PreservationOverride = null; b.Notes = ""; _focusCustomize = true; }
    private static string N(double n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private void CloseCrop()
    {
        if (_draft.Images.FirstOrDefault(b => b.Id == _cropTarget) is { } binding)
            _actionFocus = $"Picture {_draft.Images.IndexOf(binding) + 1}: Crop image";
        _cropTarget = null;
    }
    private void ApplyCrop(ImageCropRegion crop)
    {
        if (_draft.Images.FirstOrDefault(b => b.Id == _cropTarget) is { } binding)
            binding.Crop = crop is { X: 0, Y: 0, Width: 1, Height: 1 } ? null : crop;
        CloseCrop();
    }
    private async Task PickReference(AssetReferenceChoice choice)
    {
        if (choice is AssetReferenceChoice.Image image) { Add(new(image.Asset.Id, image.Media.Id)); return; }
        if (choice is AssetReferenceChoice.Reel swapIn && _swapVideo is { } swapping && _draft.Videos.FirstOrDefault(v => v.Id == swapping) is { } target)
        {
            // Another reel of the same asset keeps this reference's settings; another asset's reel starts from its defaults.
            if (swapIn.Asset.Id == CharacterVoices.Owner(target, Library)) { await Swap(target, swapIn.Media); return; }
            _swapVideo = null; _replacingVideo = swapping;
        }
        if (_busy || !AllowReels || choice is not AssetReferenceChoice.Reel selected || _draft.Videos.Any(v => v.Media.Id == selected.Media.Media.Id)) return;
        var reel = selected.Media;
        var prior = _draft.Videos.FirstOrDefault(v => v.Id == _replacingVideo);
        if (prior is null && _draft.Videos.Count >= 3) return;
        var binding = new ShotVideoBinding { Id = prior?.Id ?? Guid.NewGuid(), Media = reel.Media, Name = reel.Name, Description = reel.UseGuidance,
            UseSoundtrack = !ReelAuthoring && selected.Asset.Category != AssetCategory.Character && reel.Media.HasAudio && reel.Generation?.Recipe.VoiceMode != ReelVoiceMode.Silent, Speaker = null, OwnerAssetId = selected.Asset.Id,
            Visuals = ReelUsageDefaults.Initial(selected.Asset), Keyframes = reel.Keyframes is null ? null : ShotCopy.Of(reel.Keyframes), OwnerCategory = selected.Asset.Category,
            AudioExcerpt = new(0, Math.Min(15, reel.Media.Duration)) };
        if (binding.EffectiveVisuals is ReelVisuals.Keyframes or ReelVisuals.RefMod && binding.Keyframes is null) {
            _busy = true; _preparingFrames = true; _error = null;
            try { binding.Keyframes = await ReelMedia.SuggestFramesAsync(Library.ProjectId, reel.Media, 3, (await AiSettings.LoadAsync(_lifetime.Token)).H3, _lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception e) { _error = e.Message; }
            finally { _busy = false; _preparingFrames = false; }
        }
        if (prior is not null) _draft.Videos[_draft.Videos.IndexOf(prior)] = binding; else _draft.Videos.Add(binding);
        if (!SelectReelVoice(binding)) EnsureVoice(selected.Asset);
        if (_draft.CharacterVoices?.FirstOrDefault(c => c.AssetId == selected.Asset.Id) is { } voiceChoice) CharacterVoices.Set(_draft, ShotCopy.Of(voiceChoice), Library);
        // A library pick starts from the reel's defaults; it is no longer a swap of the earlier reel.
        _swaps.Remove(binding.Id); _swapOthers.Remove(binding.Id);
        _reelSources[binding.Id] = reel.Id; _expanded = binding.Id; _replacingVideo = null;
        if (binding.EffectiveVisuals == ReelVisuals.RefMod && binding.Keyframes?.Frames.Count is not (>= 2 and <= 9)) _keyframeBinding = binding;
    }
    // Replacing a reel with another reel of the same asset keeps this attachment's settings,
    // unlike choosing a reel from the library, which starts from the reel's defaults.
    private Guid? _swapVideo;
    private readonly Dictionary<Guid, ReelSwap> _swaps = [];
    private readonly Dictionary<Guid, int> _swapOthers = [];
    private AssetReferenceReel? LibraryReel(Guid? id) => Library.Reels.FirstOrDefault(r => r.Id == id);
    private IReadOnlyList<AssetReferenceReel> SwapChoices(ShotVideoBinding v)
    {
        var current = Library.Reels.FirstOrDefault(r => r.Media.Id == v.Media.Id);
        var owner = CharacterVoices.Owner(v, Library);
        return Library.Reels.Where(r => r.AssetId == owner && !_draft.Videos.Any(d => d.Media.Id == r.Media.Id))
            .OrderByDescending(r => RegeneratedFrom(r, current)).ThenByDescending(r => current is not null && r.LookId == current.LookId)
            .ThenByDescending(r => (long)r.Media.Width * r.Media.Height).ThenByDescending(r => r.CreatedUtc).ToArray();
    }
    private static bool RegeneratedFrom(AssetReferenceReel reel, AssetReferenceReel? source) =>
        source is not null && reel.Generation?.Snapshot.Reel?.RegenerationSource?.ReelId == source.Id;
    private void ToggleSwap(ShotVideoBinding v) { _swapVideo = _swapVideo == v.Id ? null : v.Id; if (_swapVideo is not null) _expanded = null; }
    // The reel the reference list replaces rather than adds to.
    private Guid? ReelTarget => _swapVideo ?? _replacingVideo;
    private string LookName(AssetReferenceReel reel) =>
        Library.Assets.FirstOrDefault(a => a.Id == reel.AssetId)?.Looks.FirstOrDefault(l => l.Id == reel.LookId)?.Name ?? "General";
    private async Task Swap(ShotVideoBinding v, AssetReferenceReel next)
    {
        if (_busy) return;
        _busy = true; _error = null;
        try {
            // The attachment's first reel in this visit, so swapping twice replaces the original.
            var from = _swaps.TryGetValue(v.Id, out var prior) ? prior.From : Library.Reels.FirstOrDefault(r => r.Media.Id == v.Media.Id)?.Id;
            var h3 = (await AiSettings.LoadAsync(_lifetime.Token)).H3;
            var replaced = await ReelReplacement.ReplaceAsync(_draft, v.Media.Id, next.Media,
                () => ReelMedia.FrameCatalogAsync(Library.ProjectId, next.Media, h3, _lifetime.Token));
            _reelSources[replaced.Id] = next.Id; _swapVideo = null; _swapOthers.Remove(v.Id);
            if (from is not { } source || source == next.Id) { _swaps.Remove(v.Id); return; }
            _swaps[v.Id] = new(source, next.Id, false);
            // Only shots are offered the same change elsewhere, and only when the old reel is in the library.
            if (AllowOtherShotSwaps && !ReelAuthoring && ReferencesApplied.HasDelegate && ReferenceServices.GetService<ReelReplacement>() is { } replacement) {
                var plan = await replacement.PlanAsync(Library.ProjectId, source, next.Id, _lifetime.Token);
                _swapOthers[v.Id] = plan.Shots.Count(s => s.ShotId != Shot.Id && s.Issue is null);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { _error = e.Message; }
        finally { _busy = false; }
    }
    private void SwapElsewhere(ShotVideoBinding v, bool value) { if (_swaps.TryGetValue(v.Id, out var swap)) _swaps[v.Id] = swap with { OtherShots = value }; }
    private ShotVideoBinding? _keyframeBinding;
    private Task ApplyKeyframes(ReelKeyframeSet frames) { _keyframeBinding!.Keyframes = ShotCopy.Of(frames); _keyframeBinding = null; return Task.CompletedTask; }
    private async Task ChangeVisuals(ShotVideoBinding reel, ChangeEventArgs e)
    {
        if (!Enum.TryParse<ReelVisuals>(e.Value?.ToString(), out var mode) || !Enum.IsDefined(mode) || ReelAuthoring && mode == ReelVisuals.None) return;
        reel.Visuals = mode;
        if (mode == ReelVisuals.RefMod) SelectReelVoice(reel);
        if (mode == ReelVisuals.RefMod && reel.Keyframes?.Frames.Count is not (>= 2 and <= 9) ||
            mode == ReelVisuals.Keyframes && reel.Keyframes?.Frames.Count is not > 0) _keyframeBinding = reel;
        await Task.CompletedTask;
    }
    private void AudioChanged(ShotVideoBinding reel, ChangeEventArgs e, bool start)
    {
        if (!double.TryParse(e.Value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) return;
        var prior = ResolvedReferences.Excerpt(reel); reel.AudioExcerpt = start ? prior with { Start = value } : prior with { Duration = value };
    }
    private void MoveVideo(int index, int by) { var v = _draft.Videos[index]; _draft.Videos.RemoveAt(index); _draft.Videos.Insert(index + by, v); }
    private void RemoveVideo(ShotVideoBinding v) { _reelVoiceNotice = null; _draft.Videos.Remove(v); _reelSources.Remove(v.Id); _swaps.Remove(v.Id); _swapOthers.Remove(v.Id); if (_swapVideo == v.Id) _swapVideo = null; if (_replacingVideo == v.Id) _replacingVideo = null; if (_expanded == v.Id) _expanded = null; }
    private async Task CopyUsage(int index) {
        var snippet = ReelVoiceDefaults.Usage(_draft, _draft.Videos[index], Library);
        try { await JS.InvokeVoidAsync("navigator.clipboard.writeText", snippet); }
        catch (JSException) { _error = "Clipboard unavailable. Copy the displayed reference identifiers and guidance manually."; }
    }
    private async Task Apply()
    {
        if (_busy) return; _busy = true; _error = null;
        try
        {
            H3Policy.Validate(_draft); if (!AllowInference) ShotLooks.Validate(_draft, Library);
            if (_draft.Images.Any(b => _missing.Contains(b.Id))) throw new WorkspaceStoreException("Restore or replace unavailable images before applying.");
            if (AllowReels) {
                ReferenceVideos.Validate(_draft);
                foreach (var choice in _draft.CharacterVoices ?? []) {
                    if (choice.Recording is not { } recording) continue;
                    var source = Library.Voices.FirstOrDefault(v => v.Matches(recording));
                    if (source is null) throw new WorkspaceStoreException("Restore the unavailable recording in Assets or choose another voice.");
                    if (recording.Start + recording.Duration > source.Duration + .01)
                        throw new WorkspaceStoreException($"The excerpt for {source.Name} extends beyond the recording.");
                }
                if (_draft.Videos.Any(v => v.EffectiveVisuals == ReelVisuals.RefMod && !ReelRefMods.Matches(v, v.RefMod)))
                {
                    _preparingFrames = true;
                    try {
                        var preparation = ReferenceServices.GetService<ReelRefModPreparation>()
                            ?? throw new WorkspaceStoreException("Local RefMod source preparation is unavailable. Restart Lumibelle after updating.");
                        var baseline = H3Policy.Fingerprint(_draft);
                        var captured = await preparation.CaptureAsync(Library.ProjectId, _draft, _lifetime.Token);
                        _lifetime.Token.ThrowIfCancellationRequested();
                        if (H3Policy.Fingerprint(_draft) != baseline) throw new WorkspaceConflictException();
                        _draft = captured; // A failed parent save retains these exact inputs for retry.
                    }
                    finally { _preparingFrames = false; }
                }
                foreach (var mod in _draft.Videos.Where(v => v.EffectiveVisuals == ReelVisuals.RefMod)) ReelRefMods.ValidateBinding(mod, true);
                ReferenceVideos.Validate(_draft);
                await ReelMedia.PrepareFramesAsync(Library.ProjectId, ResolvedReferences.For(_draft).Pictures.Where(p => p.Keyframe is not null).Select(p => p.Keyframe!.Frame), (await AiSettings.LoadAsync(_lifetime.Token)).H3, _lifetime.Token);
                _lifetime.Token.ThrowIfCancellationRequested();
                if (ReelAuthoring) await Applied.InvokeAsync(_draft.Copy());
                else await ReferencesApplied.InvokeAsync(new(_draft.Copy(), new Dictionary<Guid, Guid>(_reelSources)) {
                    AssistanceCatalogueFingerprint = _assistedCatalogueFingerprint,
                    AssistanceDirectionFingerprint = _assistedDirectionFingerprint,
                    Swaps = _draft.Videos.Where(v => _swaps.ContainsKey(v.Id)).Select(v => _swaps[v.Id]).ToArray()
                });
            }
            else await Applied.InvokeAsync(_draft.Copy());
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { _error = e.Message; }
        finally { _busy = false; }
    }
}
