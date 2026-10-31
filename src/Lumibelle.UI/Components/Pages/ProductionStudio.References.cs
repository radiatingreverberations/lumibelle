using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private static bool HasAttachedReferences(Shot shot) => shot.Images.Count + shot.Videos.Count + shot.Voices.Count + (shot.CharacterVoices?.Count ?? 0) > 0 || shot.ShotFrames.Count > 0 || shot.StartFrame is not null;
    private (Shot Shot, ProductionComposition Composition)? SuggestedReferenceCopy =>
        SourceShot?.SceneId is { } scene && PreviousReferenceSource is { } previous && previous.Shot.SceneId == scene &&
        (HasAttachedReferences(previous.Composition.Shot) || previous.Composition.Shot.EnvironmentAssetId is not null || previous.Shot.SelectedTakeId is not null) ? previous : null;
    private string NextReferenceAction => Selected is { } shot && !HasAttachedReferences(shot) && SuggestedReferenceCopy is not null ? "copy" : "edit";
    private int AttachedEnvironmentViews(Shot shot) => shot.EnvironmentAssetId is { } environment
        ? shot.Images.Count(i => i.AssetId == environment) + shot.Videos
            .Where(v => v.OwnerAssetId == environment || _assets.Reels.Any(r => r.AssetId == environment && r.Media.Id == v.Media.Id))
            .Sum(v => v.Keyframes?.Frames.Count ?? 0)
        : 0;
    private Guid? _stagingComposition;
    private bool StagingVisible => _stagingComposition is not null && Current?.Id == _stagingComposition && Selected?.EnvironmentStaging is not null;
    [Inject] public ReelReplacement Replacements { get; set; } = null!;
    private Shot? _imageEditorShot;
    // A copied selection remains separate from the captured baseline until the editor applies it.
    private Shot? _referenceEditorDraft;
    private AssetLibrary _imageEditorAssets = new() { ProjectId = Guid.Empty };
    // The editor now shows the restored library, so Apply compares against it rather than the one it opened with.
    private async Task RestoreEditorImage(Guid trashId)
    {
        var latest = await AssetStore.LoadAsync(Id, _lifetime.Token);
        _assets = await ImageTrash.RestoreImagesAsync(Id, [trashId], latest.Revision, _lifetime.Token);
        _imageEditorAssets = _assets.Copy();
        StateHasChanged(); // Pass the restored library down to the open editor.
    }
    private Guid? _imageEditorExpanded, _referenceComposition;
    private bool _imageEditorSaving, _referenceUndoRecorded;
    private bool ReferenceEditorBusy => _setupBusy || _selectingShot || _imageEditorSaving;
    private void ImageEditorVisibility(bool visible) { if (_imageEditorSaving) return; _referenceOpen = visible; if (!visible) ConsumeAssetPickRequest(); }
    private void OpenReferencePicker() => OpenImageEditor(null);
    private void OpenReferenceDetails(ShotImageBinding b) => OpenImageEditor(b.Id);
    private void OpenImageEditor(Guid? expanded)
    {
        if (CaptureReferenceEditor(expanded)) _referenceOpen = true;
    }
    private bool CaptureReferenceEditor(Guid? expanded)
    {
        // A pending preset/shot change can replace Current after an awaited save.
        // Ignore stale clicks as well as disabling the controls during that transition.
        if (ReferenceEditorBusy || Selected is not { } shot || Current is not { } composition) return false;
        _imageEditorShot = shot.Copy(); _imageEditorAssets = _assets.Copy(); _imageEditorExpanded = expanded;
        _referenceEditorDraft = null;
        _referenceComposition = composition.Id; _referenceUndoRecorded = false;
        return true;
    }
    private bool _copyReferenceOpen;
    private void OpenReferenceCopy() { if (CaptureReferenceEditor(null)) _copyReferenceOpen = true; }
    private void EditCopiedReferences(ReferenceSelection selection)
    {
        CheckReferenceBaseline();
        _referenceEditorDraft = selection.Inputs.Copy();
        _copyReferenceOpen = false;
        _referenceOpen = true;
    }
    private ShotContinuityFrame? CopyContinuityFrame(Shot source, bool first) =>
        _doc.Shots.FirstOrDefault(s => s.Id == source.Id)?.SelectedTakeId is { } id && _doc.Takes.FirstOrDefault(t => t.Id == id) is { } take
            ? new(Guid.NewGuid(), take.Id, first ? 0 : take.FrameCount - 1, $"{source.Title} · {(first ? "first" : "last")} frame") : null;
    private string GuidanceSummary(ShotImageBinding b) => Guidance(b).Override is not null ? "Customized for this composition" : "Using shared reference guidance";
    private static string ReferenceBaseline(Shot shot) => Json(new { shot.EnvironmentAssetId, shot.EnvironmentInspectionDisabled, shot.EnvironmentInspection, shot.EnvironmentStaging, shot.Images, shot.Videos, shot.Voices, shot.CharacterVoices, ContinuityFrame = shot.ShotFrameFingerprint, shot.Dialogue, shot.Characters, shot.Duration });
    private void CheckReferenceBaseline()
    {
        if (Current?.Id != _referenceComposition || _imageEditorShot is not { } original || Selected is not { } shot || ReferenceBaseline(shot) != ReferenceBaseline(original))
            throw new WorkspaceStoreException("The setup or its reference inputs changed. Cancel and reopen references; your selection remains here.");
    }
    // True when the draft differs from the shot only by reels replaced with another reel of the
    // same asset, so the replacement can apply them and carry the prompt review over.
    private async Task<bool> OnlySwapsAsync(Shot original, Shot draft, IReadOnlyList<ReelSwap> swaps, AssetLibrary library)
    {
        var expected = original.Copy(); var h3 = (await Settings.LoadAsync(_lifetime.Token)).H3;
        try {
            foreach (var swap in swaps) {
                if (library.Reels.FirstOrDefault(r => r.Id == swap.From) is not { } from || library.Reels.FirstOrDefault(r => r.Id == swap.To) is not { } to) return false;
                await ReelReplacement.ReplaceAsync(expected, from.Media.Id, to.Media, () => VideoReferences.FrameCatalogAsync(Id, to.Media, h3, _lifetime.Token));
            }
        }
        catch (WorkspaceStoreException) { return false; }
        // RefMod builds are captured again by the replacement.
        static string References(Shot s) => Json(new { s.Images, Videos = s.Videos.Select(v => v with { RefMod = null }), s.Voices, s.CharacterVoices,
            ContinuityFrame = s.ShotFrameFingerprint, s.EnvironmentAssetId, s.EnvironmentInspectionDisabled, s.EnvironmentInspection });
        return References(expected) == References(draft);
    }
    // Other shots changed on disk; show them as saved unless this shot has unsaved edits.
    private async Task ReloadProduction()
    {
        if (_dirty) return;
        _production = await Production.LoadAsync(Id, _lifetime.Token); _savedComposition = Current?.Copy();
    }
    private async Task ApplyReferences(ReferenceSelection selection)
    {
        if (_imageEditorSaving) return;
        _imageEditorSaving = true;
        try
        {
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            await _saveGate.WaitAsync(_lifetime.Token);
            try
            {
                CheckReferenceBaseline();
                var library = await AssetStore.LoadAsync(Id, _lifetime.Token);
                CheckAssistedReferenceContext(selection, library);
                var draft = selection.Inputs;
                var frameSources = draft.ShotFrames.Count > 0 ? await Store.LoadAsync(Id, _lifetime.Token) : null;
                foreach (var frame in draft.ShotFrames) {
                    var take = frameSources!.Takes.FirstOrDefault(t => t.Id == frame.TakeId);
                    if (take is null || frame.Frame < 0 || frame.Frame >= take.FrameCount)
                        throw new WorkspaceStoreException("The selected shot frame is no longer available. Choose another take or frame in Shot frames.");
                    await using var media = await Store.OpenAsync(Id, take.Id, ShotTrashKind.Take, frame.Frame, ct: _lifetime.Token);
                    if (media is null)
                        throw new WorkspaceStoreException("The selected shot frame could not be read. Choose another take or frame in Shot frames.");
                }
                foreach (var b in draft.Images) {
                    if (ImageContext(b, library) != ImageContext(b, _imageEditorAssets))
                        throw new WorkspaceStoreException("Reference context changed. Refresh Assets and review your selection.");
                    if (ProductionPolicy.MediaIssue(b, library) is { } issue) throw new WorkspaceStoreException(issue);
                }
                foreach (var source in selection.ReelSources) {
                    var captured = _imageEditorAssets.Reels.FirstOrDefault(r => r.Id == source.Value);
                    var latest = library.Reels.FirstOrDefault(r => r.Id == source.Value);
                    if (captured is null || latest is null || captured.Media != latest.Media || captured.Name != latest.Name || captured.UseGuidance != latest.UseGuidance || Json(captured.Keyframes) != Json(latest.Keyframes) ||
                        library.Assets.FirstOrDefault(a => a.Id == latest.AssetId) is not { } owner || latest.LookId is { } look && !owner.Looks.Any(l => l.Id == look && !l.Archived))
                        throw new WorkspaceStoreException("The selected asset reel changed or is unavailable. Reopen references to review its current guidance.");
                }
                foreach (var voice in draft.Voices) {
                    var prior = _imageEditorAssets.Voices.FirstOrDefault(v => v.Id == voice.VoiceId);
                    var latest = library.Voices.FirstOrDefault(v => v.Id == voice.VoiceId);
                    if (prior is null || latest is null || Json(prior) != Json(latest)) throw new WorkspaceStoreException("A selected voice changed. Reopen references to review its recording and excerpt.");
                    if (voice.Start + voice.Duration > latest.Duration + .01)
                        throw new WorkspaceStoreException($"The excerpt for {latest.Name} extends beyond the recording.");
                }
                foreach (var choice in draft.CharacterVoices ?? []) {
                    if (choice.FromDefault && _imageEditorShot?.CharacterVoices?.Any(c => Json(c) == Json(choice)) != true &&
                        library.Assets.FirstOrDefault(a => a.Id == choice.AssetId)?.DefaultVoiceId != _imageEditorAssets.Assets.FirstOrDefault(a => a.Id == choice.AssetId)?.DefaultVoiceId)
                        throw new WorkspaceStoreException("The character default changed. Reopen references to review the current default.");
                }
                H3Policy.Validate(draft); ReferenceVideos.Validate(draft);
                await VideoReferences.ValidateAsync(Id, draft.Videos, _lifetime.Token);
                // Awaited media checks must not allow a different target or newer browser inputs to be overwritten.
                CheckReferenceBaseline();
                CheckAssistedReferenceContext(selection, await AssetStore.LoadAsync(Id, _lifetime.Token));
                if (selection.Swaps.Count > 0 && await OnlySwapsAsync(_imageEditorShot!, draft, selection.Swaps, library))
                {
                    // Replacing a reel with another reel of the same asset keeps a reviewed prompt reviewed,
                    // here as in the other shots replaced alongside it.
                    if (!await SaveLocked()) throw new WorkspaceStoreException(_error ?? "Could not save references.");
                    Remember();
                    var shotId = Selected!.Id; var others = 0;
                    foreach (var swap in selection.Swaps)
                    {
                        var result = await Replacements.ApplyAsync(Id, swap.From, swap.To, _lifetime.Token, swap.OtherShots ? null : [shotId]);
                        if (result.Shots.FirstOrDefault(s => s.ShotId == shotId) is not { Issue: null })
                            throw new WorkspaceStoreException(result.Shots.FirstOrDefault(s => s.ShotId == shotId)?.Issue ?? "The reel could not be replaced in this shot.");
                        others += result.Shots.Count(s => s.ShotId != shotId && s.Issue is null);
                    }
                    await ReloadProduction();
                    _assets = library; _referenceOpen = false; ConsumeAssetPickRequest();
                    Notify(others == 0 ? "Reel replaced." : $"Reel replaced in this shot and {(others == 1 ? "1 other shot" : $"{others} other shots")}.");
                    return;
                }
                if (Selected!.EnvironmentInspectionDisabled != draft.EnvironmentInspectionDisabled || Json(Selected.EnvironmentInspection) != Json(draft.EnvironmentInspection) || Selected.EnvironmentAssetId != draft.EnvironmentAssetId || Json(Selected.EnvironmentStaging) != Json(draft.EnvironmentStaging) || Json(Selected.Images) != Json(draft.Images) || Json(Selected.Videos) != Json(draft.Videos) || Json(Selected.Voices) != Json(draft.Voices) || Json(Selected.CharacterVoices) != Json(draft.CharacterVoices) || Json(Selected.ShotFrames) != Json(draft.ShotFrames)) {
                    if (!_referenceUndoRecorded) { Remember(); _referenceUndoRecorded = true; }
                    Current!.Shot.Images = ShotCopy.Of(draft.Images); Current.Shot.Videos = ShotCopy.Of(draft.Videos); Current.Shot.Voices = ShotCopy.Of(draft.Voices); Current.Shot.CharacterVoices = draft.CharacterVoices is null ? null : ShotCopy.Of(draft.CharacterVoices);
                    Current.Shot.ShotFrames = ShotCopy.Of(draft.ShotFrames); Current.Shot.EnvironmentAssetId = draft.EnvironmentAssetId; Current.Shot.EnvironmentInspectionDisabled = draft.EnvironmentInspectionDisabled; Current.Shot.EnvironmentInspection = ShotCopy.Of(draft.EnvironmentInspection); Current.Shot.EnvironmentStaging = draft.EnvironmentStaging; Changed();
                    _imageEditorShot = Selected.Copy(); // Retrying a failed save does not add another Undo entry.
                }
                if (!await SaveLocked()) throw new WorkspaceStoreException(_error ?? "Could not save references.");
                var elsewhere = 0;
                foreach (var swap in selection.Swaps.Where(s => s.OtherShots))
                    elsewhere += (await Replacements.ApplyAsync(Id, swap.From, swap.To, _lifetime.Token)).Ready;
                if (elsewhere > 0) await ReloadProduction();
                _assets = library; _referenceOpen = false; ConsumeAssetPickRequest();
                var replacedElsewhere = elsewhere > 0 ? $" The reel was also replaced in {(elsewhere == 1 ? "1 other shot" : $"{elsewhere} other shots")}." : "";
                // Labels can only be wrong in an existing prompt; with none yet, composing numbers the new references.
                if (_referenceUndoRecorded && !string.IsNullOrWhiteSpace(Current?.Prompt))
                    Warn("Check prompt · References changed. Check the Picture, Video and Audio labels before generating." + replacedElsewhere);
                else if (_referenceUndoRecorded) Notify("References saved." + replacedElsewhere);
            }
            finally { _saveGate.Release(); }
        }
        finally { _imageEditorSaving = false; }
    }
}
