using Microsoft.AspNetCore.Components;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private Guid? _handledAssetPickJob;
    private bool _openingAssetPick;
    // A request link opens Manage references once; drop it on close so later visits start with the assistant collapsed.
    private void ConsumeAssetPickRequest()
    {
        if (RequestedJobId is { } id && AiJobs.View.Jobs.Any(j => j.Id == id && j.Kind == AiJobKind.AssetPicking))
            Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("jobId", (string?)null), replace: true);
    }
    private async Task HandleRequestedAssetPickAsync()
    {
        if (RequestedJobId is null) { _handledAssetPickJob = null; return; }
        if (!_interactive || _disposed || _openingAssetPick || RequestedJobId is not { } id || _handledAssetPickJob == id ||
            _referenceOpen || _reviewOpen || _compositionDialogJob is not null || _planningOpen) return;
        var job = AiJobs.View.Jobs.FirstOrDefault(j => j.Id == id && j.Kind == AiJobKind.AssetPicking && j.Target.ProjectId == Id);
        if (job?.Target.ShotId is not { } shotId || !_doc.Shots.Any(s => s.Id == shotId)) return;
        _openingAssetPick = true;
        try
        {
            if (Current?.ShotId != shotId) await Select(shotId);
            if (_disposed || Current?.ShotId != shotId) return;
            var library = await AssetStore.LoadAsync(Id, _lifetime.Token);
            if (_disposed || Current?.ShotId != shotId || RequestedJobId != id || _referenceOpen || _reviewOpen || _compositionDialogJob is not null || _planningOpen) return;
            _assets = library; _handledAssetPickJob = id;
            OpenImageEditor(null);
            StateHasChanged();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { _handledAssetPickJob = id; _error = e.Message; StateHasChanged(); }
        finally { _openingAssetPick = false; }
    }
    private void CheckAssistedReferenceContext(ReferenceSelection selection, AssetLibrary library)
    {
        if (selection.AssistanceCatalogueFingerprint is { } catalogue &&
            AssetPickCatalog.Hash(AssetPickCatalog.Capture(library)) != catalogue)
            throw new WorkspaceStoreException("The asset catalogue changed after this selection was staged. Cancel and reopen references, then request a new suggestion; your pending selection is retained here.");
        if (selection.AssistanceDirectionFingerprint is { } direction &&
            (Selected is null || Current is null || AssetPickCatalog.DirectionFingerprint(Selected, Current.Prompt, Current.DirectingNotes) != direction))
            throw new WorkspaceStoreException("The shot or prompt changed after this selection was staged. Review the new direction and request another suggestion; your pending selection is retained here.");
    }
}
