using lumibelle.Models;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Assets;

public partial class ImageReviewDialog
{
    [Parameter] public Func<ImageMetadataEdit, Task<string?>>? ApplyDetails { get; set; }
    [Parameter] public EventCallback<bool> DetailsDirtyChanged { get; set; }
    [Parameter] public bool OpenDetails { get; set; }
    [Parameter] public Guid? RequestedGuidanceId { get; set; }
    private ImageMetadataEdit? _details;
    private Func<Task>? _afterDetails;
    private string? _detailsError;
    private bool _detailsOpen;
    private long _detailsRevision;
    private bool DetailsDirty => _details is { } d && d.Original != d.Value;
    private ReferenceAsset? DetailsAsset => _details is { } d ? Library.Assets.FirstOrDefault(a => a.Id == d.Reference.AssetId) : null;
    private bool DetailsAvailable => _details is { } d && DetailsAsset?.Images.Any(i => i.Id == d.Reference.ImageId) == true;

    private void BeginDetails(bool focusName = false)
    {
        if (ApplyDetails is null || Primary is not { } selected || Resolution(selected).State != ReviewImageState.Active || Image(selected) is not { } image) return;
        _details = new(Session.ProjectId, selected.Reference, ImageMetadataValues.From(image), ImageMetadataValues.From(image));
        _detailsRevision = Library.Revision;
        _detailsError = null; _detailsOpen = true;
        if (focusName) _focusAfterRender = "Name";
    }
    private void EnsureDetails()
    {
        if (!Visible || DetailsDirty || _details is not null && _details.Reference == _primary && Library.Revision <= _detailsRevision) return;
        _details = null; _detailsOpen = false;
        BeginDetails();
    }
    private async Task ChangeDetails(Func<ImageMetadataValues, ImageMetadataValues> update)
    {
        if (_busy || _details is null) return;
        _details = _details with { Value = update(_details.Value) };
        await DetailsDirtyChanged.InvokeAsync(DetailsDirty);
    }
    private async Task LeaveDetails(Func<Task> action)
    {
        if (_busy) return;
        if (DetailsDirty) { _afterDetails = action; return; }
        _details = null; _detailsOpen = false; _detailsError = null; await action();
        EnsureDetails();
    }
    private async Task CancelDetails()
    {
        if (_busy) return;
        if (_details is { } details) _details = details with { Value = details.Original };
        _detailsError = null; _afterDetails = null;
        await DetailsDirtyChanged.InvokeAsync(false);
        EnsureDetails();
        _focusAfterRender = "Name";
    }
    private async Task DiscardDetailsAndLeave()
    {
        var action = _afterDetails; _afterDetails = null; await CancelDetails(); if (action is not null) await action();
        EnsureDetails();
    }
    private async Task SaveDetails()
    {
        if (_busy || _details is null || ApplyDetails is null) return;
        var submitted = _details;
        var submittedRevision = Library.Revision;
        _busy = true; _detailsError = null;
        try
        {
            _detailsError = await ApplyDetails(submitted);
            if (_detailsError is not null) return;
            // Keep the accepted values visible until the parent publishes the newer library.
            _details = submitted with { Original = submitted.Value };
            _detailsRevision = submittedRevision;
            await DetailsDirtyChanged.InvokeAsync(false);
            var action = _afterDetails; _afterDetails = null;
            // Leave actions may start another operation after this save.
            _busy = false; if (action is not null) await action();
            EnsureDetails();
        }
        catch (Exception e) { _detailsError = $"Couldn’t save image details: {e.Message}. Your draft is still here."; }
        finally { _busy = false; }
    }
}
